using System.Collections.Generic;

namespace DarkBasicYo.Virtual
{
    public struct FastStack
    {
        public byte[] buffer;
        public int ptr;

        // public Span<byte> 
        // public int Count;

        public FastStack(int capacity)
        {
            buffer = new byte[capacity];
            ptr = 0;
        }
        
        public byte Pop()
        {
            // Count--;
            return buffer[--ptr];
        }

        public void PopArray(int size, ref byte[] value)
        {
            // value = new byte[size];
            // Count -= size;
            for (var n = 0; n < size; n++)
            {
                value[n] = buffer[ptr - (n + 1)];
            }
            ptr -= size;
        }

        public void Push(byte data)
        {
            // Count++;
            buffer[ptr++] = data;
        }

        public void PushArray(byte[] data, int start, int length)
        {
            // Count += length;
            for (var n = start; n < start + length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }
        public void PushArrayReverse(byte[] data, int start, int length)
        {
            // Count += length;
            // for (var n = typeSize - 1; n >= 0; n --)
            // {
            //     stack.Push(values[n]);
            // }
            for (var n = start + (length - 1); n >= start ; n --)
            {
                // var value = Advance();
                buffer[ptr++] = data[n];
            }
        }

        public void Pop2(out byte data)
        {
            // Count--;
            data = buffer[--ptr];
        }

    }
}