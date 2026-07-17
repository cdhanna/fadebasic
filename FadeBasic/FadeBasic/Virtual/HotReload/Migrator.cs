using System;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Applies a reconcile to a live VM. Split into granular steps so tests and
    /// the session can exercise each independently:
    ///  - <see cref="RemapGlobals"/>  : rebuild the global register bank name-keyed.
    ///  - <see cref="SwapProgram"/>    : install new bytecode + interned data.
    ///  - <see cref="RemapProgramCounter"/> : move PC/return-addresses to new code.
    ///
    /// The heap is left untouched here (Tier A); struct migration is <see cref="HeapMigrator"/>.
    /// </summary>
    public static class Migrator
    {
        /// <summary>
        /// Rebuild the global register bank so each new global's value comes from
        /// the same-named old global (name-keyed, since addresses shift). New or
        /// type-incompatible globals default. Heap pointers carry over unchanged
        /// (the heap is not rebuilt), so pointer-typed registers stay valid.
        /// </summary>
        public static void RemapGlobals(VirtualMachine vm, ProgramFacts oldFacts, ProgramFacts newFacts)
        {
            var g = vm.globalScope;
            int size = Math.Max(newFacts.MaxRegisterAddress, g.dataRegisters.Length);

            // Start from a COPY of the running register bank. This preserves EVERY
            // slot — named globals, array metadata, and anything the facts don't
            // name (compiler temporaries, hidden registers). A zero-filled rebuild
            // that only re-copies named globals silently drops those live values
            // and the VM diverges a few frames later. For a body edit (no address
            // changes) this copy is exactly correct and nothing is touched below.
            var data = Grow(g.dataRegisters, size);
            var types = Grow(g.typeRegisters, size);
            var ins = Grow(g.insIndexes, size);
            var flags = Grow(g.flags, size);

            // Relocate a global's cells, reading from the ORIGINAL bank (so swaps
            // between two addresses don't clobber each other).
            void Relocate(int oldAddr, int newAddr)
            {
                if (oldAddr == newAddr) return;
                if (oldAddr < 0 || oldAddr >= g.dataRegisters.Length) return;
                if (newAddr < 0 || newAddr >= size) return;
                data[newAddr] = g.dataRegisters[oldAddr];
                types[newAddr] = g.typeRegisters[oldAddr];
                ins[newAddr] = g.insIndexes[oldAddr];
                flags[newAddr] = g.flags[oldAddr];
            }

            void Default(int addr)
            {
                if (addr < 0 || addr >= size) return;
                data[addr] = 0; types[addr] = 0; ins[addr] = 0;
                flags[addr] = VirtualScope.FLAG_GLOBAL;
            }

            foreach (var kvp in newFacts.Globals)
            {
                var nv = kvp.Value;
                if (oldFacts.Globals.TryGetValue(kvp.Key, out var ov) && ov.typeCode == nv.typeCode)
                    Relocate((int)ov.registerAddress, (int)nv.registerAddress);
                else
                    Default((int)nv.registerAddress); // brand-new / retyped global
            }

            foreach (var kvp in newFacts.GlobalArrays)
            {
                var nv = kvp.Value;
                if (!oldFacts.GlobalArrays.TryGetValue(kvp.Key, out var ov) || ov.typeCode != nv.typeCode)
                    continue;
                Relocate((int)ov.registerAddress, (int)nv.registerAddress);
                int ranks = Math.Min(
                    ov.rankSizeRegisterAddresses?.Length ?? 0,
                    nv.rankSizeRegisterAddresses?.Length ?? 0);
                for (var i = 0; i < ranks; i++)
                {
                    Relocate(ov.rankSizeRegisterAddresses[i], nv.rankSizeRegisterAddresses[i]);
                    Relocate(ov.rankIndexScalerRegisterAddresses[i], nv.rankIndexScalerRegisterAddresses[i]);
                }
            }

            SetGlobalRegisters(vm, data, types, ins, flags);
        }

        static ulong[] Grow(ulong[] src, int size) { var a = (ulong[])src.Clone(); if (a.Length != size) Array.Resize(ref a, size); return a; }
        static byte[] Grow(byte[] src, int size) { var a = (byte[])src.Clone(); if (a.Length != size) Array.Resize(ref a, size); return a; }
        static int[] Grow(int[] src, int size) { var a = (int[])src.Clone(); if (a.Length != size) Array.Resize(ref a, size); return a; }

        /// <summary>
        /// Assign the global register arrays across every alias of the global
        /// scope (the struct is copied into <c>globalScope</c>, <c>scope</c>, and
        /// the bottom of <c>scopeStack</c> — all must point at the new arrays).
        /// </summary>
        public static void SetGlobalRegisters(VirtualMachine vm, ulong[] data, byte[] types, int[] ins, byte[] flags)
        {
            vm.globalScope.dataRegisters = data;
            vm.globalScope.typeRegisters = types;
            vm.globalScope.insIndexes = ins;
            vm.globalScope.flags = flags;

            if (vm.scopeStack.ptr > 0)
            {
                vm.scopeStack.buffer[0].dataRegisters = data;
                vm.scopeStack.buffer[0].typeRegisters = types;
                vm.scopeStack.buffer[0].insIndexes = ins;
                vm.scopeStack.buffer[0].flags = flags;
            }

            // if the current scope IS the global scope, update its copy too
            if (vm.scopeStack.ptr <= 1)
            {
                vm.scope.dataRegisters = data;
                vm.scope.typeRegisters = types;
                vm.scope.insIndexes = ins;
                vm.scope.flags = flags;
            }
        }

        /// <summary>
        /// Install new bytecode + interned data, preserving the register scopes.
        /// Re-reads interned data so the runtime type table / string interning
        /// match the new program. Does NOT move the PC (see RemapProgramCounter).
        /// </summary>
        public static void SwapProgram(VirtualMachine vm, ProgramFacts newFacts)
        {
            vm.program = newFacts.Program;
            vm.ReloadInternedData();
        }

        /// <summary>
        /// Move the program counter (and every call-frame return address) from
        /// old-program instruction space to the new program, by mapping through
        /// source statements. Returns false if any active location could not be
        /// mapped (caller should treat as not-yet-safe / rude).
        /// </summary>
        public static bool RemapProgramCounter(VirtualMachine vm, ProgramFacts oldFacts, ProgramFacts newFacts)
        {
            if (!TryMapInstruction(vm.instructionIndex, oldFacts, newFacts, out var newIp)) return false;
            for (var i = 0; i < vm.methodStack.ptr; i++)
            {
                if (!TryMapInstruction(vm.methodStack.buffer[i].fromIns, oldFacts, newFacts, out var mappedFrom))
                    return false;
                vm.methodStack.buffer[i].fromIns = mappedFrom;
            }
            vm.instructionIndex = newIp;
            return true;
        }

        // Map an old instruction index -> new instruction index via the statement
        // it belongs to, matched by source line + within-line ordinal.
        internal static bool TryMapInstruction(int oldIns, ProgramFacts oldFacts, ProgramFacts newFacts, out int newIns)
        {
            newIns = oldIns;
            if (oldFacts.Debug == null || newFacts.Debug == null) return false;

            int stmtStart = HotReloadUtil.StatementStartForInstruction(oldFacts, oldIns);
            if (stmtStart < 0) return false;
            int offsetWithinStmt = oldIns - stmtStart;

            if (!HotReloadUtil.TryGetStatementLineAndOrdinal(oldFacts, stmtStart, out var line, out var ordinal))
                return false;
            if (!HotReloadUtil.TryGetStatementStartByLineOrdinal(newFacts, line, ordinal, out var newStmtStart))
                return false;

            newIns = newStmtStart + offsetWithinStmt;
            return true;
        }
    }
}
