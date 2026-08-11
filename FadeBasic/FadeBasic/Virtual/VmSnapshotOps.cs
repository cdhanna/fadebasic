using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual
{
    public partial struct VmHeap
    {
        /// <summary>
        /// Capture every mutable field into <paramref name="c"/>, reusing its
        /// buffers. Bucket bytes are copied only up to the high-water mark for
        /// that bucket, since everything past the cursor is unallocated.
        /// </summary>
        public void SnapshotInto(VmSnapshot.HeapCapture c)
        {
            var bucketCount = memory?.Count ?? 0;
            c.bucketCount = bucketCount;
            VmSnapshot.EnsureCapacity(ref c.buckets, bucketCount);
            VmSnapshot.EnsureCapacity(ref c.bucketFill, bucketCount);

            for (var b = 0; b < bucketCount; b++)
            {
                // Everything at or past the cursor in the newest bucket is
                // untouched, so only the used prefix needs copying. Older
                // buckets are full by construction.
                var fill = b < _cursor.bucketPtr ? memory[b].Length : _cursor.memoryPtr;
                if (fill > memory[b].Length) fill = memory[b].Length;
                c.bucketFill[b] = fill;

                if (c.buckets[b] == null || c.buckets[b].Length < fill)
                    c.buckets[b] = new byte[memory[b].Length];
                Array.Copy(memory[b], c.buckets[b], fill);
            }

            c.cursor = _cursor;

            var allocCount = _allocations?.Count ?? 0;
            c.allocCount = allocCount;
            VmSnapshot.EnsureCapacity(ref c.allocPtrs, allocCount);
            VmSnapshot.EnsureCapacity(ref c.allocs, allocCount);
            if (_allocations != null)
            {
                var i = 0;
                foreach (var kvp in _allocations)
                {
                    c.allocPtrs[i] = kvp.Key;
                    c.allocs[i] = kvp.Value;
                    i++;
                }
            }

            var groups = _lengthToPtrs?.Count ?? 0;
            c.freeGroups = 0;
            c.freePtrCount = 0;
            VmSnapshot.EnsureCapacity(ref c.freeGroupLength, groups);
            VmSnapshot.EnsureCapacity(ref c.freeGroupStart, groups);
            VmSnapshot.EnsureCapacity(ref c.freeGroupCount, groups);
            if (_lengthToPtrs != null)
            {
                var total = 0;
                foreach (var kvp in _lengthToPtrs) total += kvp.Value.Count;
                VmSnapshot.EnsureCapacity(ref c.freePtrs, total);

                var g = 0;
                var write = 0;
                foreach (var kvp in _lengthToPtrs)
                {
                    c.freeGroupLength[g] = kvp.Key;
                    c.freeGroupStart[g] = write;
                    c.freeGroupCount[g] = kvp.Value.Count;
                    // Stack<T> enumerates top-to-bottom; RestoreFrom pushes in
                    // reverse so pop order is preserved exactly.
                    foreach (var ptr in kvp.Value) c.freePtrs[write++] = ptr;
                    g++;
                }
                c.freeGroups = g;
                c.freePtrCount = write;
            }
        }

        public void RestoreFrom(VmSnapshot.HeapCapture c)
        {
            if (memory == null) memory = new HeapData();

            // Buckets only ever grow, so a restore to a shallower state keeps the
            // extra capacity and just stops using it.
            while (memory.Count < c.bucketCount)
                memory.Add(new byte[c.buckets[memory.Count]?.Length ?? 0]);

            for (var b = 0; b < c.bucketCount; b++)
            {
                if (memory[b] == null || memory[b].Length < c.bucketFill[b])
                    memory[b] = new byte[c.buckets[b].Length];
                Array.Copy(c.buckets[b], memory[b], c.bucketFill[b]);
            }

            _cursor = c.cursor;

            if (_allocations == null) _allocations = new Dictionary<VmPtr, VmAllocation>();
            _allocations.Clear();
            for (var i = 0; i < c.allocCount; i++)
                _allocations[c.allocPtrs[i]] = c.allocs[i];

            if (_lengthToPtrs == null) _lengthToPtrs = new Dictionary<int, Stack<VmPtr>>();
            _lengthToPtrs.Clear();
            for (var g = 0; g < c.freeGroups; g++)
            {
                var stack = new Stack<VmPtr>(c.freeGroupCount[g] + 1);
                var start = c.freeGroupStart[g];
                for (var i = c.freeGroupCount[g] - 1; i >= 0; i--)
                    stack.Push(c.freePtrs[start + i]);
                _lengthToPtrs[c.freeGroupLength[g]] = stack;
            }

            if (_sweepKillList == null) _sweepKillList = new List<VmPtr>();
            _sweepKillList.Clear();
        }

        /// <summary>
        /// Contents of one allocation, for the canonical hash walk. Returns false
        /// when the pointer isn't a live allocation.
        /// </summary>
        public bool TryGetAllocationSpan(VmPtr ptr, out VmAllocation allocation, out ReadOnlySpan<byte> span)
        {
            span = default;
            allocation = default;
            if (_allocations == null || !_allocations.TryGetValue(ptr, out allocation)) return false;
            var bucket = memory[ptr.bucketPtr];
            var len = allocation.length;
            if (ptr.memoryPtr + len > bucket.Length) len = bucket.Length - ptr.memoryPtr;
            span = new ReadOnlySpan<byte>(bucket, ptr.memoryPtr, len);
            return true;
        }
    }

    public partial class VirtualMachine
    {
        /// <summary>
        /// Capture the VM's complete mutable state. Reuses the snapshot's buffers,
        /// so a steady-state rollback loop allocates nothing after warm-up.
        /// </summary>
        public void SnapshotInto(VmSnapshot snap)
        {
            snap.HasValue = true;
            snap.instructionIndex = instructionIndex;
            snap.state = state;

            // Live prefix only — the tail past `ptr` is stale bytes that would
            // make two logically identical VMs look different.
            VmSnapshot.EnsureCapacity(ref snap.stackBuffer, stack.ptr);
            Array.Copy(stack.buffer, snap.stackBuffer, stack.ptr);
            snap.stackPtr = stack.ptr;

            VmSnapshot.EnsureCapacity(ref snap.methodBuffer, methodStack.ptr);
            Array.Copy(methodStack.buffer, snap.methodBuffer, methodStack.ptr);
            snap.methodPtr = methodStack.ptr;

            CaptureScope(ref globalScope, snap.globalScope);

            snap.scopeStackCount = scopeStack.ptr;
            VmSnapshot.EnsureCapacity(ref snap.scopeStack, scopeStack.ptr);
            for (var i = 0; i < scopeStack.ptr; i++)
            {
                if (snap.scopeStack[i] == null) snap.scopeStack[i] = new VmSnapshot.ScopeCapture();
                CaptureScope(ref scopeStack.buffer[i], snap.scopeStack[i]);
            }

            // `scope` is always an alias of the top of the scope stack (see
            // PUSH_SCOPE / POP_SCOPE). Record which entry so restore re-aliases
            // rather than creating an independent copy — VirtualScope is a struct,
            // and its FastStack<int> deferredJumps would otherwise diverge.
            snap.scopeAlias = scopeStack.ptr - 1;

            heap.SnapshotInto(snap.heap);
        }

        public void RestoreFrom(VmSnapshot snap)
        {
            if (!snap.HasValue) throw new InvalidOperationException("VmSnapshot is empty");

            instructionIndex = snap.instructionIndex;
            state = snap.state;

            if (stack.buffer.Length < snap.stackPtr) Array.Resize(ref stack.buffer, snap.stackBuffer.Length);
            Array.Copy(snap.stackBuffer, stack.buffer, snap.stackPtr);
            stack.ptr = snap.stackPtr;

            if (methodStack.buffer.Length < snap.methodPtr) Array.Resize(ref methodStack.buffer, snap.methodBuffer.Length);
            Array.Copy(snap.methodBuffer, methodStack.buffer, snap.methodPtr);
            methodStack.ptr = snap.methodPtr;

            if (scopeStack.buffer.Length < snap.scopeStackCount)
                Array.Resize(ref scopeStack.buffer, snap.scopeStackCount);

            for (var i = 0; i < snap.scopeStackCount; i++)
                RestoreScope(ref scopeStack.buffer[i], snap.scopeStack[i]);
            scopeStack.ptr = snap.scopeStackCount;

            // Registers are restored in place, so globalScope's array references
            // still point at the restored data. Re-copy the struct anyway to pick
            // up deferredJumps.
            if (snap.scopeStackCount > 0) globalScope = scopeStack.buffer[0];
            else RestoreScope(ref globalScope, snap.globalScope);

            scope = snap.scopeAlias >= 0 && snap.scopeAlias < scopeStack.ptr
                ? scopeStack.buffer[snap.scopeAlias]
                : globalScope;

            heap.RestoreFrom(snap.heap);
        }

        static void CaptureScope(ref VirtualScope s, VmSnapshot.ScopeCapture c)
        {
            if (s.dataRegisters == null)
            {
                c.present = false;
                c.registerCount = 0;
                c.deferredJumpCount = 0;
                return;
            }

            c.present = true;
            var n = s.dataRegisters.Length;
            c.registerCount = n;
            VmSnapshot.EnsureCapacity(ref c.dataRegisters, n);
            VmSnapshot.EnsureCapacity(ref c.typeRegisters, n);
            VmSnapshot.EnsureCapacity(ref c.insIndexes, n);
            VmSnapshot.EnsureCapacity(ref c.flags, n);
            Array.Copy(s.dataRegisters, c.dataRegisters, n);
            Array.Copy(s.typeRegisters, c.typeRegisters, n);
            Array.Copy(s.insIndexes, c.insIndexes, n);
            Array.Copy(s.flags, c.flags, n);

            var dj = s.deferredJumps;
            c.deferredJumpCount = dj.buffer == null ? 0 : dj.ptr;
            VmSnapshot.EnsureCapacity(ref c.deferredJumps, c.deferredJumpCount);
            if (c.deferredJumpCount > 0)
                Array.Copy(dj.buffer, c.deferredJumps, c.deferredJumpCount);
        }

        static void RestoreScope(ref VirtualScope s, VmSnapshot.ScopeCapture c)
        {
            if (!c.present)
            {
                s = default;
                return;
            }

            var n = c.registerCount;
            if (s.dataRegisters == null || s.dataRegisters.Length != n)
            {
                s.dataRegisters = new ulong[n];
                s.typeRegisters = new byte[n];
                s.insIndexes = new int[n];
                s.flags = new byte[n];
            }
            Array.Copy(c.dataRegisters, s.dataRegisters, n);
            Array.Copy(c.typeRegisters, s.typeRegisters, n);
            Array.Copy(c.insIndexes, s.insIndexes, n);
            Array.Copy(c.flags, s.flags, n);

            if (s.deferredJumps.buffer == null || s.deferredJumps.buffer.Length < c.deferredJumpCount)
                s.deferredJumps = new FastStack<int>(Math.Max(4, c.deferredJumpCount));
            if (c.deferredJumpCount > 0)
                Array.Copy(c.deferredJumps, s.deferredJumps.buffer, c.deferredJumpCount);
            s.deferredJumps.ptr = c.deferredJumpCount;
        }
    }
}
