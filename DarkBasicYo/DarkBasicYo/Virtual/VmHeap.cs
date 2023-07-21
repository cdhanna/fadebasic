using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.ComTypes;

namespace DarkBasicYo.Virtual
{
    public class VmHeap
    {
        struct Allocation
        {
            
        }
        
        
        public byte[] memory;
        private int _cursor;

        /// <summary>
        /// key: a ptr
        /// value: a length
        ///
        /// If it exists in this dictionary, then it is "leased"
        /// </summary>
        private Dictionary<int, int> _allocations = new Dictionary<int, int>();

        // private Dictionary<int, int> _ptrToFreed = new Dictionary<int, int>();
        private Dictionary<int, Stack<int>> _lengthToPtrs = new Dictionary<int, Stack<int>>();

        public VmHeap(int initialCapacity=128)
        {
            memory = new byte[initialCapacity];
        }

        public void Write(int ptr, int length, byte[] data)
        {
            for (var i = 0; i < length; i++)
            {
                memory[ptr + i] = data[i];
            }
        }
        
        public void Read(int ptr, int length, out byte[] memory)
        {
            memory = new byte[length];
            for (var i = ptr; i < ptr + length; i++)
            {
                memory[i - ptr] = this.memory[i];
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

                throw new NotImplementedException("Heap exception: Heap overflow! Maybe I should auto expand the heap");
            }
            
            // reserve from the cursor to size offset...
            ptr = _cursor;
            _allocations[ptr] = size;
            _cursor += size;
        }
        
        
    }
}