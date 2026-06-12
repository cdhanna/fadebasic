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
            Array.Copy(data, start, buffer, ptr, length);
            ptr += length;
        }

        void Expand(int wiggle)
        {
            // post-condition: buffer.Length > ptr + wiggle, leaving one spare
            // slot — PushSpanAndType writes wiggle+1 items after Expand(wiggle).
            if (ptr + wiggle < buffer.Length) return;
            var newSize = buffer.Length * 2;
            while (ptr + wiggle >= newSize)
            {
                newSize *= 2;
            }
            Array.Resize(ref buffer, newSize);
        }

        public void PushFiller(T filler, int length)
        {
            Expand(length);

            new Span<T>(buffer, ptr, length).Fill(filler);
            ptr += length;
        }

        public void PushSpan(ReadOnlySpan<T> data, int length)
        {
            Expand(length);

            // most pushes are 1-8 element values; a manual loop beats the
            // Span.CopyTo call overhead there, while large payloads
            // (strings, arrays) want the block copy.
            if (length <= 8)
            {
                for (var n = 0; n < length; n ++)
                {
                    buffer[ptr + n] = data[n];
                }
            }
            else
            {
                data.Slice(0, length).CopyTo(new Span<T>(buffer, ptr, length));
            }
            ptr += length;
        }
        public void PushSpanAndType(ReadOnlySpan<T> data, T typecode, int length)
        {
            Expand(length);

            if (length <= 8)
            {
                for (var n = 0; n < length; n ++)
                {
                    buffer[ptr + n] = data[n];
                }
            }
            else
            {
                data.Slice(0, length).CopyTo(new Span<T>(buffer, ptr, length));
            }
            ptr += length;

            buffer[ptr++] = typecode;
        }

    }
}