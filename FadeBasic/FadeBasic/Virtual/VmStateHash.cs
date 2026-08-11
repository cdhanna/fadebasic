using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual
{
    /// <summary>
    /// Layout-independent structural hash of a VM's logical state, for lockstep
    /// desync detection.
    ///
    /// The hard part is the heap. Two peers that ran the same code over the same
    /// inputs can legitimately hold the same logical objects at *different*
    /// addresses — different allocation histories, different GC timing, different
    /// free-list reuse. Hashing raw pointer values would report desyncs that
    /// aren't real; hashing only allocation type/size would miss desyncs that are.
    ///
    /// So pointers are replaced by their **visit-order sequence number** in a
    /// deterministic walk of the reachable object graph. Roots are enumerated in
    /// register order, contents in element/field order — both already canonical
    /// (this mirrors <see cref="VirtualMachine.CollectGarbage"/>'s mark phase).
    /// Sequence numbering also distinguishes aliasing from duplication: two
    /// references to one object hash differently from two identical objects.
    ///
    /// Deliberately excluded: raw pointer values, `allocsSinceCollect` (a peer
    /// that rolled back has allocated more times than one that didn't —
    /// legitimately divergent), `sweepInterval`, `insIndexes` (debug metadata),
    /// and the whole test/debug surface.
    /// </summary>
    public class VmStateHasher
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        ulong _hash;
        readonly Dictionary<VmPtr, int> _visited = new Dictionary<VmPtr, int>();
        readonly Stack<PendingWalk> _work = new Stack<PendingWalk>();
        int _seq;

        struct PendingWalk
        {
            public VmPtr ptr;
        }

        public ulong Hash(VirtualMachine vm)
        {
            _hash = FnvOffset;
            _visited.Clear();
            _work.Clear();
            _seq = 0;

            Fold(vm.instructionIndex);

            // Operand stack: live prefix only. At a yield boundary this is
            // normally empty, but a yield inside an expression would not be.
            Fold(vm.stack.ptr);
            for (var i = 0; i < vm.stack.ptr; i++) Fold(vm.stack.buffer[i]);

            // methodStack field-by-field: JumpHistoryData is {int,int,bool} — 9
            // bytes padded to 12, and padding is uninitialised.
            Fold(vm.methodStack.ptr);
            for (var i = 0; i < vm.methodStack.ptr; i++)
            {
                var f = vm.methodStack.buffer[i];
                Fold(f.fromIns);
                Fold(f.toIns);
                Fold(f.isLauncherFrame ? (byte)1 : (byte)0);
            }

            Fold(vm.scopeStack.ptr);
            for (var i = 0; i < vm.scopeStack.ptr; i++)
                HashScope(vm, ref vm.scopeStack.buffer[i]);

            // Drain the graph walk. Roots were queued by HashScope in register
            // order; children are queued in element/field order.
            while (_work.Count > 0)
                HashAllocation(vm, _work.Pop());

            return _hash;
        }

        void HashScope(VirtualMachine vm, ref VirtualScope scope)
        {
            if (scope.dataRegisters == null)
            {
                Fold(-1);
                return;
            }

            var n = scope.dataRegisters.Length;
            Fold(n);
            for (var r = 0; r < n; r++)
            {
                var tc = scope.typeRegisters[r];
                Fold(tc);
                Fold(scope.flags[r]);

                if (IsPointerRegister(tc, scope.flags[r]))
                {
                    // The address itself never enters the hash.
                    Fold(Reference(VmPtr.FromRaw(scope.dataRegisters[r])));
                }
                else
                {
                    Fold(scope.dataRegisters[r]);
                }
            }

            var dj = scope.deferredJumps;
            var djCount = dj.buffer == null ? 0 : dj.ptr;
            Fold(djCount);
            for (var i = 0; i < djCount; i++) Fold(dj.buffer[i]);
        }

        static bool IsPointerRegister(byte typeCode, byte flags)
        {
            return typeCode == TypeCodes.STRING
                   || typeCode == TypeCodes.STRUCT
                   || typeCode == TypeCodes.PTR_HEAP
                   || VirtualScope.IsPtr(flags);
        }

        /// <summary>
        /// Canonical name for a pointer: its position in visit order. Queues the
        /// target for traversal the first time it is seen. 0 means null/unallocated.
        /// </summary>
        int Reference(VmPtr ptr)
        {
            if (ptr.bucketPtr == 0 && ptr.memoryPtr == 0) return 0;
            if (_visited.TryGetValue(ptr, out var existing)) return existing;

            var id = ++_seq;
            _visited[ptr] = id;
            _work.Push(new PendingWalk { ptr = ptr });
            return id;
        }

        void HashAllocation(VirtualMachine vm, PendingWalk pending)
        {
            var ptr = pending.ptr;
            if (!vm.heap.TryGetAllocationSpan(ptr, out var alloc, out var span))
            {
                // A register still holding a pointer to freed memory. Record the
                // fact without touching the address.
                Fold(_visited[ptr]);
                Fold(-1);
                return;
            }

            Fold(_visited[ptr]);
            Fold(alloc.length);
            Fold(alloc.format.typeId);
            Fold(alloc.format.typeCode);
            Fold(alloc.format.typeFlags);

            var elementType = alloc.format.typeCode;

            if (elementType == TypeCodes.STRUCT && vm.typeTable != null
                && vm.typeTable.TryGetValue(alloc.format.typeId, out var internedType))
            {
                // Array of structs (or a single struct — same layout, count 1).
                var stride = StructSize(internedType);
                if (stride <= 0) { FoldRaw(span); return; }
                for (var offset = 0; offset + stride <= span.Length; offset += stride)
                    HashStructFields(vm, span, offset, internedType);
            }
            else if (elementType == TypeCodes.PTR_HEAP || elementType == TypeCodes.STRING)
            {
                // Array of references: rename each, don't hash addresses.
                for (var offset = 0; offset + 8 <= span.Length; offset += 8)
                    Fold(Reference(VmPtr.FromBytes(span.Slice(offset))));
            }
            else
            {
                FoldRaw(span);
            }
        }

        void HashStructFields(VirtualMachine vm, ReadOnlySpan<byte> span, int baseOffset, InternedType type)
        {
            var fields = OrderedFields(type);
            for (var f = 0; f < fields.Length; f++)
            {
                var field = fields[f];
                var offset = baseOffset + field.offset;
                if (offset < 0 || offset >= span.Length) continue;

                var tc = field.typeCode;
                if (tc == TypeCodes.STRUCT && vm.typeTable != null
                    && vm.typeTable.TryGetValue(field.typeId, out var nested))
                {
                    // A struct-typed field holds a pointer to its own allocation,
                    // so rename rather than recursing inline.
                    if (offset + 8 <= span.Length)
                        Fold(Reference(VmPtr.FromBytes(span.Slice(offset))));
                }
                else if (tc == TypeCodes.STRING || tc == TypeCodes.PTR_HEAP || tc == TypeCodes.STRUCT)
                {
                    if (offset + 8 <= span.Length)
                        Fold(Reference(VmPtr.FromBytes(span.Slice(offset))));
                }
                else
                {
                    var size = field.length > 0 ? field.length : TypeCodes.GetByteSize(tc);
                    if (size > 0 && offset + size <= span.Length)
                        FoldRaw(span.Slice(offset, size));
                }
            }
        }

        // InternedType.fields is keyed by string, and .NET randomises string
        // hashing per process — so Dictionary iteration order differs between
        // peers. Field offsets are unique and stable, so sort by offset to get a
        // canonical order. Cached per type; the table is immutable per build.
        readonly Dictionary<int, InternedField[]> _fieldOrder = new Dictionary<int, InternedField[]>();

        InternedField[] OrderedFields(InternedType type)
        {
            if (_fieldOrder.TryGetValue(type.typeId, out var cached)) return cached;

            var count = type.fields?.Count ?? 0;
            var ordered = new InternedField[count];
            if (count > 0)
            {
                var i = 0;
                foreach (var kvp in type.fields) ordered[i++] = kvp.Value;
                Array.Sort(ordered, (a, b) => a.offset.CompareTo(b.offset));
            }

            _fieldOrder[type.typeId] = ordered;
            return ordered;
        }

        static int StructSize(InternedType type)
        {
            return type.byteSize;
        }

        void FoldRaw(ReadOnlySpan<byte> bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                _hash ^= bytes[i];
                _hash *= FnvPrime;
            }
        }

        void Fold(byte value)
        {
            _hash ^= value;
            _hash *= FnvPrime;
        }

        void Fold(int value)
        {
            for (var i = 0; i < 4; i++)
            {
                _hash ^= (byte)(value >> (i * 8));
                _hash *= FnvPrime;
            }
        }

        void Fold(ulong value)
        {
            for (var i = 0; i < 8; i++)
            {
                _hash ^= (byte)(value >> (i * 8));
                _hash *= FnvPrime;
            }
        }
    }
}
