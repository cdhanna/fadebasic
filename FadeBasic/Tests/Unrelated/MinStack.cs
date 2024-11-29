namespace Tests.Unrelated;

public class TestsForMinStack
{
    [Test]
    public void Doop()
    {
        var s = new MinStack();
        
        s.Push(4);
        s.Push(3);
        s.Push(5);
        s.Push(2);
        s.Push(10);

        while (s.GetSize() > 0)
        {
            Console.WriteLine($"min=[{s.GetMin()}] val=[{s.Pop()}]");
        }
    }
    
    [Test]
    public void Doop2()
    {
        var s = new MinStack();
        
        s.Push(8);
        s.Push(1);
        s.Push(-5);
        s.Push(1);
        s.Push(2);
        s.Push(10);

        while (s.GetSize() > 0)
        {
            Console.WriteLine($"min=[{s.GetMin()}] val=[{s.Pop()}]");
        }
    }
}


public class MinStack
{
    // regular stack...
    private int[] _stack = new int[100]; // TODO: eh, make it resizable some day, not really important for now.
    private int _ptr = 0;

    // keep a stack of the index values in the regular stack
    private int[] _minTrace = new int[100];
    private int _minPtr = 0;
    
    public void Push(int num)
    {
        
        if (_minPtr == 0)
        {
            // base case, there is only one element, so it _must_ be the min. 
            _minTrace[_minPtr++] = _ptr + 1;
        }
        else
        {
            var currentMin = GetMin();
            if (num < currentMin)
            {
                _minTrace[_minPtr++] = _ptr + 1;
            }
        }
        
        _stack[_ptr++] = num;
        
        
    }

    public int Pop()
    {
        // if this pointer is on top of the mintrace, then pop that.
        if (_ptr == _minTrace[_minPtr - 1])
        {
            _minPtr--;
        }
        return _stack[--_ptr];
    }

    public int GetMin()
    {
        var p = _minTrace[_minPtr - 1] - 1;
        return _stack[p];
    }

    public int GetSize()
    {
        return _ptr;
    }
}