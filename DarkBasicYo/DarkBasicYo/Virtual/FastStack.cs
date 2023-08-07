using System;
using System.Collections.Generic;

namespace DarkBasicYo.Virtual
{
    public struct FastStack
    {
        public byte[] buffer;
        public int ptr;

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
        
        public void PopArraySpan(int size, out ReadOnlySpan<byte> span)
        {
            // value = new byte[size];
            // Count -= size;
            span = new ReadOnlySpan<byte>(buffer, ptr - size, size);
            
            // span = x;
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
            for (var n = start + (length - 1); n >= start ; n --)
            {
                buffer[ptr++] = data[n];
            }
        }
        
        public void PushSpan(ReadOnlySpan<byte> data, int length)
        {
            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }
        public void PushSpanAndType(ReadOnlySpan<byte> data, byte typecode, int length)
        {
            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }

            buffer[ptr++] = typecode;
        }

        public void Pop2(out byte data)
        {
            // Count--;
            data = buffer[--ptr];
        }

    }
}