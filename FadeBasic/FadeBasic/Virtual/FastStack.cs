using System;

namespace FadeBasic.Virtual
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

        public static FastStack<T> Copy(FastStack<T> original)
        {
            var copied = new T[original.buffer.Length];
            Array.Copy(original.buffer, copied, copied.Length);
            return new FastStack<T>
            {
                buffer = copied,
                ptr = original.ptr
            };
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
            // TODO: is it ever worth it to shrink the stack?
        }

        public void Push(T data)
        {
            Expand(1);

            // Count++;
            buffer[ptr++] = data;
        }

        public void PushArray(T[] data, int start, int length)
        {
            Expand(length);
            for (var n = start; n < start + length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }

        void Expand(int wiggle)
        {
            while (ptr + wiggle >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }
        }
        
        public void PushFiller(T filler, int length)
        {
            Expand(length);

            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = filler;
            }
        }
        
        public void PushSpan(ReadOnlySpan<T> data, int length)
        {
            Expand(length);

            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }
        }
        public void PushSpanAndType(ReadOnlySpan<T> data, T typecode, int length)
        {
            Expand(length);

            for (var n = 0; n < length; n ++)
            {
                buffer[ptr++] = data[n];
            }

            buffer[ptr++] = typecode;
        }

    }
}