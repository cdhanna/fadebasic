using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;

namespace FadeBasic.Virtual
{
    public struct VmAllocation
    {
        public int ptr;
        public int length;
        public HeapTypeFormat format;
    }
    
    public struct VmHeap
    {
        public byte[] memory;
        private int _cursor;

        public int Cursor => _cursor;
        /// <summary>
        /// key: a ptr
        /// value: a length (and metadata)
        ///
        /// If it exists in this dictionary, then it is "leased"
        /// </summary>
        private Dictionary<int, VmAllocation> _allocations;// = new Dictionary<int, int>();

        private Dictionary<int, Stack<int>> _lengthToPtrs;// = new Dictionary<int, Stack<int>>();

        private Dictionary<int, int> _ptrToRefCount;

        public int Allocations => _allocations.Count;
        
        public VmHeap(int initialCapacity)
        {
            memory = new byte[initialCapacity];
            _cursor = 0;
            _allocations = new Dictionary<int, VmAllocation>();
            _lengthToPtrs = new Dictionary<int, Stack<int>>();
            _ptrToRefCount = new Dictionary<int, int>();
        }

        public void Write(int ptr, int length, byte[] data)
        {
            for (var i = 0; i < length; i++)
            {
                memory[ptr + i] = data[i];
            }
        }

        public void WriteSpan(int ptr, int length, ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < length; i++)
            {
                memory[ptr + i] = data[i];
            }
        }

        public void ReadSpan(int ptr, int length, out ReadOnlySpan<byte> memory)
        {
            memory = new ReadOnlySpan<byte>(this.memory, ptr, length);
        }
        
        public void Read(int ptr, int length, out byte[] memory)
        {
            memory = new byte[length];
            for (var i = ptr; i < ptr + length; i++)
            {
                // var memAddr = length - (i - ptr) - 1;
                var memAddr = i - ptr;
                memory[memAddr] = this.memory[i];
            }
        }

        public void Copy(int srcPtr, int dstPtr, int length)
        {
            for (var i = 0; i < length; i++)
            {
                memory[dstPtr + i] = memory[srcPtr + i];
            }
        }

        // public void Allocate(byte typeCode, int arrayLength, out int ptr)
        // {
        //     var size = arrayLength * TypeCodes.GetByteSize(typeCode);
        //     Allocate(ref HeapTypeFormat.STRING_FORMAT, size, out ptr);
        // }
        public void AllocateString(int length, out int ptr)
        {
            Allocate(ref HeapTypeFormat.STRING_FORMAT, length, out ptr);
        }

        public void Free(int ptr)
        {
            if (!_allocations.TryGetValue(ptr, out var allocation))
            {
                throw new Exception("VmHeap: cannot free a section of memory that was not directly allocated");
            }

            _allocations.Remove(ptr);
            // _ptrToFreed[ptr] = length;
            if (!_lengthToPtrs.TryGetValue(allocation.length, out var ptrs))
            {
                ptrs = _lengthToPtrs[allocation.length] = new Stack<int>();
            }
            
            ptrs.Push(ptr);
        }
        
        public void Allocate(ref HeapTypeFormat format, int size, out int ptr)
        {
            
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
            
            if (_cursor + size >= memory.Length)
            {
                
                

                while (_cursor + size >= memory.Length)
                {
                    Array.Resize(ref memory, memory.Length * 2);
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
                _cursor += 1; // increase the cursor by 1, so that this spot is held as "empty"
            }
            else
            {
                _cursor += size;
            }
        }

        public void GetAllocationSize(int ptr, out int size)
        {
            if (!_allocations.TryGetValue(ptr, out var allocation))
            {
                throw new Exception("vm heap: invalid ptr cannot access allocation size, " + ptr);
            }

            size = allocation.length;
        }
        public bool TryGetAllocationSize(int ptr, out int size)
        {
            var success = _allocations.TryGetValue(ptr, out var allocation);
            size = allocation.length;
            return success;
        }

        public bool TryGetAllocation(int ptr, out VmAllocation allocation)
        {
            return _allocations.TryGetValue(ptr, out allocation);
        }

        public void IncrementRefCount(ulong ptr)
        {
            var key = (int)ptr;
            if (_ptrToRefCount.TryGetValue(key, out var refCount))
            {
                _ptrToRefCount[key] = refCount + 1;
            }
            else
            {
                _ptrToRefCount[key] = 1;
            }
        }

        public void Sweep()
        {
            foreach (var allocation in _allocations)
            {
                if (_ptrToRefCount.TryGetValue(allocation.Key, out var refCount))
                {
                    if (refCount <= 0)
                    {
                        Free(allocation.Key);
                    }
                }
            }
            // foreach (var kvp in _ptrToRefCount)
            // {
            //     if (kvp.Value <= 0 && _allocations.try)
            //     {
            //         Free(kvp.Key);
            //     }
            // }
        }
        
        public void TryDecrementRefCount(ulong ptr)
        {
            var key = (int)ptr;

            if (_ptrToRefCount.TryGetValue(key, out var refCount))
            {
                _ptrToRefCount[key] = refCount - 1;

                if (refCount == 1) 
                {
                    // the reference can be free'd
                    // Free(key);
                }
            }
            else
            {
                // ?
            }
        }
    }
}