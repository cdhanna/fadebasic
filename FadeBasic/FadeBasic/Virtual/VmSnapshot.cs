using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual
{
    /// <summary>
    /// A complete, restorable capture of a <see cref="VirtualMachine"/>'s mutable
    /// state. Built for rollback netcode: a host keeps a ring of these (one per
    /// simulated tick) and restores one wholesale when a prediction turns out to
    /// be wrong, so the Fade program re-experiences those ticks.
    ///
    /// Reusable by design — <see cref="VirtualMachine.SnapshotInto"/> grows the
    /// buffers on demand and then keeps reusing them, so a steady-state rollback
    /// loop allocates nothing.
    ///
    /// What is deliberately NOT captured: the program bytecode, the host method
    /// table, the interned data / type table (all static per build), and the
    /// test/debug surface (assertionFailure, mockTable, hostCallCounts,
    /// runtoStack, error, logger). Those either cannot change or are not
    /// simulation state.
    /// </summary>
    public class VmSnapshot
    {
        public bool HasValue;

        public int instructionIndex;
        public VirtualMachine.VmState state;

        // --- operand stack (live prefix only) ---
        public byte[] stackBuffer = new byte[256];
        public int stackPtr;

        // --- method stack (live prefix only) ---
        public JumpHistoryData[] methodBuffer = new JumpHistoryData[64];
        public int methodPtr;

        // --- scopes: globalScope, then scopeStack[0..n), then `scope` ---
        public ScopeCapture globalScope = new ScopeCapture();
        public ScopeCapture[] scopeStack = new ScopeCapture[8];
        public int scopeStackCount;
        /// <summary>
        /// Index into <see cref="scopeStack"/> that `vm.scope` aliased at capture
        /// time, or -1 when it aliased <see cref="globalScope"/>. VirtualScope is
        /// a struct holding array references, so `scope` is normally an alias of
        /// whatever is on top of the stack; recording which one keeps restore
        /// from silently splitting them into two independent copies.
        /// </summary>
        public int scopeAlias;

        public HeapCapture heap = new HeapCapture();

        public class ScopeCapture
        {
            public bool present;
            public ulong[] dataRegisters = Array.Empty<ulong>();
            public byte[] typeRegisters = Array.Empty<byte>();
            public int[] insIndexes = Array.Empty<int>();
            public byte[] flags = Array.Empty<byte>();
            public int registerCount;

            public int[] deferredJumps = Array.Empty<int>();
            public int deferredJumpCount;
        }

        public class HeapCapture
        {
            /// <summary>Bucket contents, copied up to <see cref="bucketFill"/>.</summary>
            public byte[][] buckets = Array.Empty<byte[]>();
            public int[] bucketFill = Array.Empty<int>();
            public int bucketCount;

            public VmPtr cursor;

            public VmPtr[] allocPtrs = Array.Empty<VmPtr>();
            public VmAllocation[] allocs = Array.Empty<VmAllocation>();
            public int allocCount;

            // _lengthToPtrs, flattened: freeGroupLength[i] owns
            // freeGroupCount[i] entries starting at freeGroupStart[i].
            public int[] freeGroupLength = Array.Empty<int>();
            public int[] freeGroupStart = Array.Empty<int>();
            public int[] freeGroupCount = Array.Empty<int>();
            public int freeGroups;
            public VmPtr[] freePtrs = Array.Empty<VmPtr>();
            public int freePtrCount;
        }

        internal static void EnsureCapacity<T>(ref T[] array, int needed)
        {
            if (array.Length >= needed) return;
            var size = array.Length == 0 ? 8 : array.Length;
            while (size < needed) size *= 2;
            Array.Resize(ref array, size);
        }
    }
}
