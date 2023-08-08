using System;

namespace DarkBasicYo.Virtual
{
    public struct FastStack<T> where T : struct
    {
        public T[] buffer;
        public int ptr; // for some reason, it is faster to have the int second...

        public FastStack(int capacity)
        {
            buffer = new T[capacity];
            ptr = 0;
        }
        
        public T Pop()
        {
            return buffer[--ptr];
        }

        public int Count => ptr;
        public T Peek() => buffer[ptr - 1];

        
        public void PopArraySpan(int size, out ReadOnlySpan<T> span)
        {
            // value = new byte[size];
            // Count -= size;
            span = new ReadOnlySpan<T>(buffer, ptr - size, size);
            
            // span = x;
            ptr -= size;
        }

        public void Push(T data)
        {
            // Count++;
            buffer[ptr++] = data;
        }

        public void PushArray(T[] data, int start, int length)
        {
            for (var n = start; n < start + length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }
        
        
        public void PushSpan(ReadOnlySpan<T> data, int length)
        {
            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }
        public void PushSpanAndType(ReadOnlySpan<T> data, T typecode, int length)
        {
            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }

            buffer[ptr++] = typecode;
        }

    }
}