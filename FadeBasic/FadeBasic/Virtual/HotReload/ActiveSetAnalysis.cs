using System.Collections.Generic;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Computes the "active set" A — the instruction locations currently in
    /// flight on the VM: the program counter plus each call frame's return
    /// address. The control gate asks, for every active location, whether a swap
    /// is safe there (see <see cref="HotReloadUtil.IsActiveLocationSafe"/>).
    /// </summary>
    public static class ActiveSetAnalysis
    {
        /// <summary>Old-program instruction indexes for every active location (PC + return addresses).</summary>
        public static List<int> ActiveInstructions(VirtualMachine vm)
        {
            var list = new List<int> { vm.instructionIndex };
            for (var i = 0; i < vm.methodStack.ptr; i++)
                list.Add(vm.methodStack.buffer[i].fromIns);
            return list;
        }

        /// <summary>Old-program statement starts for the active locations (for UI / S intersection).</summary>
        public static List<int> ActiveStatementStarts(VirtualMachine vm, ProgramFacts oldFacts)
        {
            var set = new HashSet<int>();
            foreach (var ins in ActiveInstructions(vm))
            {
                int start = HotReloadUtil.StatementStartForInstruction(oldFacts, ins);
                if (start >= 0) set.Add(start);
            }
            return new List<int>(set);
        }

        /// <summary>
        /// Control gate: safe iff every active location is safe to swap at (its
        /// statement is mappable, and either we're at a statement boundary or the
        /// statement's bytecode is unchanged).
        /// </summary>
        public static bool IsControlSafe(VirtualMachine vm, ProgramFacts oldFacts, ProgramFacts newFacts)
        {
            var active = ActiveInstructions(vm);
            if (active.Count == 0) return false;
            foreach (var ins in active)
                if (!HotReloadUtil.IsActiveLocationSafe(oldFacts, newFacts, ins))
                    return false;
            return true;
        }

        /// <summary>Statement starts of active locations that block the apply (for UI).</summary>
        public static List<int> BlockingStatements(VirtualMachine vm, ProgramFacts oldFacts, ProgramFacts newFacts)
        {
            var blocking = new HashSet<int>();
            foreach (var ins in ActiveInstructions(vm))
            {
                if (HotReloadUtil.IsActiveLocationSafe(oldFacts, newFacts, ins)) continue;
                int start = HotReloadUtil.StatementStartForInstruction(oldFacts, ins);
                blocking.Add(start);
            }
            return new List<int>(blocking);
        }
    }
}
