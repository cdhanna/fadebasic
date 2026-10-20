using System;

namespace FadeBasic.Compiled
{
    /// <summary>
    /// A small growable int stack for compiled code: the gosub return stack and the
    /// per-scope defer stack. A struct, so a region's stack lives in its frame.
    /// </summary>
    public struct FadeIntStack
    {
        int[] _items;
        int _count;

        public FadeIntStack(int capacity)
        {
            _items = new int[capacity < 1 ? 1 : capacity];
            _count = 0;
        }

        public int Count => _count;

        public void Push(int value)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }
            _items[_count++] = value;
        }

        public int Pop() => _items[--_count];

        public int Peek() => _items[_count - 1];
    }
}
