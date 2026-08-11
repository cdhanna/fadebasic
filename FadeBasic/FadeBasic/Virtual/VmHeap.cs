using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace FadeBasic.Virtual
{
    public struct VmAllocation
    {
        public VmPtr ptr;
        public int length;
        public HeapTypeFormat format;
    }

    /// <summary>
    /// The max size of an array in C# is ~2 billion, or roughly 2^31. The 1 unused bit on the int isn't
    /// useful enough to do much with.
    ///
    /// A ptr needs to be able to address more than 2^31 in a 64bit OS world.
    /// A second integer is kept to add more bits, and acts as a way to bucketize the original 31 bit pointer.
    ///
    /// An object is not allowed to exceed the bucket size, which is 2^31 (C#'s addressable array size) 
    /// </summary>
    public struct VmPtr : IEquatable<VmPtr>
    {
        
        
        public static  VmPtr operator +(VmPtr a, int b)
        {
            return new VmPtr
            {
                bucketPtr = a.bucketPtr,
                memoryPtr = a.memoryPtr + b
            };
        }

        public readonly static VmPtr TEMP = new VmPtr
        {
            bucketPtr = int.MaxValue, memoryPtr = int.MaxValue
        };
        
        public static VmPtr FromRaw(ulong raw)
        {
            // little-endian equivalent of BitConverter.GetBytes(raw) → FromBytes:
            // the low 32 bits hold the bucket, the high 32 bits the memory offset.
            return new VmPtr
            {
                bucketPtr = unchecked((int)raw),
                memoryPtr = unchecked((int)(raw >> 32)),
            };
        }

        public static VmPtr FromBytes(ReadOnlySpan<byte> span)
        {
            return new VmPtr
            {
                bucketPtr = MemoryMarshal.Read<int>(span),
                memoryPtr = MemoryMarshal.Read<int>(span.Slice(4)),
            };
        }

        public static VmPtr FromBytes(byte[] bytes)
        {
            // TODO: after all tests pass, flip the bytes around so that ptrs are more easily readable. 
            var ptr = new VmPtr
            {
                bucketPtr = BitConverter.ToInt32(bytes, 0),
                memoryPtr = BitConverter.ToInt32(bytes, 4),
            };

            return ptr;
        }
        
        public static byte[] GetBytes(ref VmPtr ptr)
        {
            var bytes = new byte[sizeof(int) * 2];
            var bucketBytes = BitConverter.GetBytes(ptr.bucketPtr);
            var memoryBytes = BitConverter.GetBytes(ptr.memoryPtr);

            for (var i = 0; i < bucketBytes.Length; i++)
            {
                bytes[i] = bucketBytes[i];
                bytes[i + 4] = memoryBytes[i];
            }

            return bytes;
        }
        
        
        public static ulong GetRaw(ref VmPtr ptr)
        {
            return unchecked((uint)ptr.bucketPtr | ((ulong)(uint)ptr.memoryPtr << 32));
        }
        
        
        // https://learn.microsoft.com/en-us/dotnet/api/system.array?view=net-9.0&redirectedfrom=MSDN#remarks
        public const uint MAX_ARRAY_SIZE = 0X7FEFFFFF; // ~2 billion
        
        public int bucketPtr;
        public int memoryPtr;

        public bool Equals(VmPtr other)
        {
            return bucketPtr == other.bucketPtr && memoryPtr == other.memoryPtr;
        }

        public override bool Equals(object obj)
        {
            return obj is VmPtr other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (bucketPtr * 397) ^ memoryPtr;
            }
        }

        public override string ToString()
        {
            return $"ptr(m={memoryPtr}, b={bucketPtr})";
        }
    }

    public class HeapData : List<byte[]>
    {
        public int BucketSize(VmPtr ptr) => this[ptr.bucketPtr].Length;
    }
    
    public partial struct VmHeap
    {
        // private byte[] memory;
        private HeapData memory;
        private VmPtr _cursor;

        public VmPtr Cursor => _cursor;
        /// <summary>
        /// key: a ptr
        /// value: a length (and metadata)
        ///
        /// If it exists in this dictionary, then it is "leased"
        /// </summary>
        private Dictionary<VmPtr, VmAllocation> _allocations;// = new Dictionary<int, int>();

        private Dictionary<int, Stack<VmPtr>> _lengthToPtrs;// = new Dictionary<int, Stack<int>>();

        // reused by SweepUnmarked so freeing doesn't mutate _allocations mid-enumeration
        private List<VmPtr> _sweepKillList;

        /// <summary>
        /// Number of allocations made since the last garbage collection.
        /// The VM reads this at collection trigger points (pointer stores,
        /// heap writes, allocs) to decide whether a collection is worth running.
        /// </summary>
        public int allocsSinceCollect;

        public int Allocations => _allocations.Count;

        // Live-allocation enumeration used by hot-reload heap migration to find
        // every instance of a changed struct type. Snapshots to a list so the
        // caller may allocate/free during iteration without invalidating it.
        public List<VmAllocation> SnapshotAllocations()
        {
            var list = new List<VmAllocation>(_allocations.Count);
            foreach (var kvp in _allocations) list.Add(kvp.Value);
            return list;
        }
        
        public VmHeap(int initialCapacity)
        {
            memory = new HeapData
            {
                new byte[initialCapacity]
            };
            _cursor = new VmPtr();
            _allocations = new Dictionary<VmPtr, VmAllocation>();
            _lengthToPtrs = new Dictionary<int, Stack<VmPtr>>();
            _sweepKillList = new List<VmPtr>();
            allocsSinceCollect = 0;
            paranoid = false;
        }

        public void Write(VmPtr ptr, int length, byte[] data)
        {
            for (var i = 0; i < length; i++)
            {
                memory[ptr.bucketPtr][ptr.memoryPtr + i] = data[i];
            }
        }

        public void WriteSpan(VmPtr ptr, int length, ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < length; i++)
            {
                memory[ptr.bucketPtr][ptr.memoryPtr + i] = data[i];
            }
        }

        public void ReadSpan(VmPtr ptr, int length, out ReadOnlySpan<byte> memory)
        {
            
            memory = new ReadOnlySpan<byte>(this.memory[ptr.bucketPtr], ptr.memoryPtr, length);
        }
        
        public void Read(VmPtr ptr, int length, out byte[] memory)
        {
            memory = new byte[length];
            for (var i = ptr.memoryPtr; i < ptr.memoryPtr + length; i++)
            {
                // var memAddr = length - (i - ptr) - 1;
                var memAddr = i - ptr.memoryPtr;
                memory[memAddr] = this.memory[ptr.bucketPtr][i];
            }
        }

        public void Copy(VmPtr srcPtr, VmPtr dstPtr, int length)
        {
            for (var i = 0; i < length; i++)
            {
                memory[dstPtr.bucketPtr][dstPtr.memoryPtr + i] = memory[srcPtr.bucketPtr][srcPtr.memoryPtr + i];
            }
        }

        // public void Allocate(byte typeCode, int arrayLength, out int ptr)
        // {
        //     var size = arrayLength * TypeCodes.GetByteSize(typeCode);
        //     Allocate(ref HeapTypeFormat.STRING_FORMAT, size, out ptr);
        // }
        public void AllocateString(int length, out VmPtr ptr)
        {
            Allocate(ref HeapTypeFormat.STRING_FORMAT, length, out ptr);
        }

        // Diagnostic mode: when set, freed blocks are overwritten with a poison
        // byte and are NOT returned to the size-keyed reuse free-list. Any
        // use-after-free (a live pointer the GC wrongly reclaimed) then reads
        // poison instead of either its own stale data or a same-size neighbor's
        // fresh data — turning a silent corruption into visible garbage / a bad
        // pointer that trips a bounds/allocation check. Costs memory (freed
        // space is never recycled), so it's for hunting GC bugs, not production.
        public bool paranoid;
        public const byte POISON = 0xDD;

        public void Free(VmPtr ptr)
        {
            if (!_allocations.TryGetValue(ptr, out var allocation))
            {
                throw new Exception("VmHeap: cannot free a section of memory that was not directly allocated");
            }

            _allocations.Remove(ptr);

            if (paranoid)
            {
                // Scribble over the block so any read through a dangling pointer
                // returns poison, and drop it on the floor (no reuse) so the
                // poison sticks instead of being overwritten by the next alloc.
                var bucket = memory[ptr.bucketPtr];
                var end = ptr.memoryPtr + allocation.length;
                for (var i = ptr.memoryPtr; i < end; i++) bucket[i] = POISON;
                return;
            }

            // _ptrToFreed[ptr] = length;
            if (!_lengthToPtrs.TryGetValue(allocation.length, out var ptrs))
            {
                ptrs = _lengthToPtrs[allocation.length] = new Stack<VmPtr>();
            }

            ptrs.Push(ptr);
        }
        
        public void Allocate(ref HeapTypeFormat format, int size, out VmPtr ptr)
        {
            // TODO: assert that the size is less than the max array size.
            allocsSinceCollect++;

            // If there is something with the exact size we need, grab it!
            if (_lengthToPtrs.TryGetValue(size, out var availablePtrs) && availablePtrs.Count > 0)
            {
                ptr = availablePtrs.Pop();
                _allocations[ptr] = new VmAllocation
                {
                    length = size,
                    format = format,
                    ptr = ptr
                };
                return;
            }
            
            if (_cursor.memoryPtr + size >= memory.BucketSize(_cursor))
            {
                if (_cursor.memoryPtr + size >= memory.BucketSize(_cursor) && (uint)_cursor.memoryPtr + (uint)size >= VmPtr.MAX_ARRAY_SIZE)
                {
                    // game over! We need a new bucket!
                    _cursor.bucketPtr += 1;
                    _cursor.memoryPtr = 0;
                        
                    // allocate the next bucket at the full size, since we've exhausted one anyway... 
                    //  this does mean that the program mem jumps from 2GB to 4GB. You cannot have
                    //  3GB working set :( 
                    memory.Add(new byte[VmPtr.MAX_ARRAY_SIZE]);
                    
                }
                else
                {
                    // expand the current bucket size
                    while (_cursor.memoryPtr + size >= memory.BucketSize(_cursor))
                    {
                        var nextSize = (uint)memory.BucketSize(_cursor) * 2;
                        if (nextSize >= VmPtr.MAX_ARRAY_SIZE)
                        {
                            nextSize = VmPtr.MAX_ARRAY_SIZE;
                        }
                        var arr = memory[_cursor.bucketPtr];
                        Array.Resize(ref arr, (int)nextSize);
                        memory[_cursor.bucketPtr] = arr;
                    }
                }
            }

            // reserve from the cursor to size offset...
            ptr = _cursor;
            _allocations[ptr] = new VmAllocation
            {
                length = size,
                format = format,
                ptr = ptr
            };
            if (size == 0)
            {
                _cursor.memoryPtr += 1; // increase the cursor by 1, so that this spot is held as "empty"
            }
            else
            {
                _cursor.memoryPtr += size;
            }
        }

        public void GetAllocationSize(VmPtr ptr, out int size)
        {
            if (!_allocations.TryGetValue(ptr, out var allocation))
            {
                throw new Exception("vm heap: invalid ptr cannot access allocation size, " + ptr);
            }

            size = allocation.length;
        }
        public bool TryGetAllocationSize(VmPtr ptr, out int size)
        {
            var success = _allocations.TryGetValue(ptr, out var allocation);
            size = allocation.length;
            return success;
        }

        public bool TryGetAllocation(VmPtr ptr, out VmAllocation allocation)
        {
            return _allocations.TryGetValue(ptr, out allocation);
        }

        public bool IsAllocated(VmPtr ptr) => _allocations.ContainsKey(ptr);

        /// <summary>
        /// Free every allocation whose pointer is not in <paramref name="marked"/>.
        /// The mark set is produced by <see cref="VirtualMachine.CollectGarbage"/>,
        /// which traces reachability from the VM's registers, eval stack, and
        /// interned strings. The heap itself has no notion of liveness.
        /// </summary>
        public void SweepUnmarked(HashSet<VmPtr> marked)
        {
            var killList = _sweepKillList ?? (_sweepKillList = new List<VmPtr>());
            killList.Clear();
            foreach (var allocation in _allocations)
            {
                if (!marked.Contains(allocation.Key))
                {
                    killList.Add(allocation.Key);
                }
            }

            for (var i = 0; i < killList.Count; i++)
            {
                Free(killList[i]);
            }

            allocsSinceCollect = 0;
        }
    }
}