using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;

namespace DarkBasicYo.Virtual
{
    public struct VmHeap
    {
        
        public byte[] memory;
        private int _cursor;

        public int Cursor => _cursor;
        /// <summary>
        /// key: a ptr
        /// value: a length
        ///
        /// If it exists in this dictionary, then it is "leased"
        /// </summary>
        private Dictionary<int, int> _allocations;// = new Dictionary<int, int>();

        private Dictionary<int, Stack<int>> _lengthToPtrs;// = new Dictionary<int, Stack<int>>();

        public VmHeap(int initialCapacity)
        {
            memory = new byte[initialCapacity];
            _cursor = 0;
            _allocations = new Dictionary<int, int>();
            _lengthToPtrs = new Dictionary<int, Stack<int>>();
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

        public void Allocate(byte typeCode, int arrayLength, out int ptr)
        {
            var size = arrayLength * TypeCodes.GetByteSize(typeCode);
            Allocate(size, out ptr);
        }

        public void Free(int ptr)
        {
            if (!_allocations.TryGetValue(ptr, out var length))
            {
                throw new Exception("VmHeap: cannot free a section of memory that was not directly allocated");
            }

            _allocations.Remove(ptr);
            // _ptrToFreed[ptr] = length;
            if (!_lengthToPtrs.TryGetValue(length, out var ptrs))
            {
                ptrs = _lengthToPtrs[length] = new Stack<int>();
            }
            
            ptrs.Push(ptr);
        }
        
        public void Allocate(int size, out int ptr)
        {
            
            if (_cursor + size >= memory.Length)
            {
                
                // If there is something with the exact size we need, grab it!
                if (_lengthToPtrs.TryGetValue(size, out var availablePtrs))
                {
                    throw new NotImplementedException(
                        "this is untested code. When you see this, check the code and remove this exception...");
                    ptr = availablePtrs.Pop();
                    _allocations[ptr] = size;
                    return;
                }

                while (_cursor + size >= memory.Length)
                {
                    Array.Resize(ref memory, memory.Length * 2);
                }
            }

            if (_cursor == 476)
            {
                
            }
            
            // reserve from the cursor to size offset...
            ptr = _cursor;
            _allocations[ptr] = size;
            _cursor += size;
        }

        public void GetAllocationSize(int ptr, out int size)
        {
            if (!_allocations.TryGetValue(ptr, out size))
            {
                throw new Exception("vm heap: invalid ptr cannot access allocation size, " + ptr);
            }
        }
        public bool TryGetAllocationSize(int ptr, out int size)
        {
            return _allocations.TryGetValue(ptr, out size);
        }
    }
}