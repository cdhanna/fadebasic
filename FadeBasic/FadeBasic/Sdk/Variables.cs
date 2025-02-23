using System;
using System.Collections.Generic;
using FadeBasic.Virtual;

namespace FadeBasic.Sdk
{
    
    public abstract class FadeArray<T> : FadeArray<T>.IArrayController
    {
        public interface IArrayController
        {
            public void SetElements(T[] elements);
            public T ReadMemoryValue(ref VmAllocation allocation, byte[] memory, int offset);
            void SetArrayMetadata(int[] dimensions, int[] strides, int elementByteSize);
        }
        public abstract byte TypeCode { get; }
        public int Ranks => _dimensions.Length;

        public int[] Dimensions => _dimensions;
        
        private int[] _dimensions;
        private int[] _strides;
        private int _elementByteSize;

        private T[] rawElements;

        public T GetElement(params int[] indecies)
        {
            if (indecies.Length != Ranks)
            {
                throw new InvalidOperationException(
                    $"Array has {Ranks} dimensions, so cannot be indexed with {indecies.Length} values");
            }

            var index = 0;
            for (var i = 0; i < indecies.Length; i++)
            {
                index += indecies[i] * _strides[i];
            }

            return rawElements[index];
        }

        public T GetRawElement(int index)
        {
            return rawElements[index];
        }

        void IArrayController.SetElements(T[] elements)
        {
            rawElements = elements;
        }

        void IArrayController.SetArrayMetadata(int[] dimensions, int[] strides, int elementByteSize)
        {
            _dimensions = dimensions;
            _strides = strides;
            _elementByteSize = elementByteSize;
        }

        T IArrayController.ReadMemoryValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return ReadValue(ref allocation, memory, offset);
        }
        
        protected abstract T ReadValue(ref VmAllocation allocation, byte[] memory, int offset);
    }
    
    public class FadeIntegerArray : FadeArray<int>
    {
        public override byte TypeCode => TypeCodes.INT;
        protected override int ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToInt32(memory, offset);
        }
    }
    
    
    public class FadeDoubleIntegerArray : FadeArray<long>
    {
        public override byte TypeCode => TypeCodes.DINT;
        protected override long ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToInt64(memory, offset);
        }
    }
    
    public class FadeWordArray : FadeArray<ushort>
    {
        public override byte TypeCode => TypeCodes.WORD;
        protected override ushort ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToUInt16(memory, offset);
        }
    }
    
    public class FadeDWordArray : FadeArray<uint>
    {
        public override byte TypeCode => TypeCodes.DWORD;
        protected override uint ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToUInt32(memory, offset);
        }
    }
    
    public class FadeByteArray : FadeArray<byte>
    {
        public override byte TypeCode => TypeCodes.BYTE;
        protected override byte ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return memory[offset];
        }
    }
    
    public class FadeBoolArray : FadeArray<bool>
    {
        public override byte TypeCode => TypeCodes.BOOL;
        protected override bool ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return memory[offset] > 0;
        }
    }
    
    public class FadeFloatArray : FadeArray<float>
    {
        public override byte TypeCode => TypeCodes.REAL;
        protected override float ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToSingle(memory, offset);
        }
    }
    
    
    public class FadeDoubleFloatArray : FadeArray<double>
    {
        public override byte TypeCode => TypeCodes.DFLOAT;
        protected override double ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToDouble(memory, offset);
        }
    }

    public class FadeObjectArray : FadeArray<FadeObject>
    {
        private readonly FadeRuntimeContext _ctx;
        public override byte TypeCode => TypeCodes.STRUCT;

        public FadeObjectArray(FadeRuntimeContext ctx)
        {
            _ctx = ctx;
          
        }
        
        protected override FadeObject ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            _ctx.ReadSubObject(ref allocation, allocation.format.typeId, memory, offset, out var obj);
            return obj;
        }
    }
    
    public class FadeObject
    {
        public string typeName;

        public Dictionary<string, FadeObject> objects = new Dictionary<string, FadeObject>();
        public Dictionary<string, bool> boolFields = new Dictionary<string, bool>();
        public Dictionary<string, byte> byteFields = new Dictionary<string, byte>();
        public Dictionary<string, ushort> wordFields = new Dictionary<string, ushort>();
        public Dictionary<string, uint> dWordFields = new Dictionary<string, uint>();
        public Dictionary<string, int> integerFields = new Dictionary<string, int>();
        public Dictionary<string, long> doubleIntegerFields = new Dictionary<string, long>();
        public Dictionary<string, float> floatFields = new Dictionary<string, float>();
        public Dictionary<string, double> doubleFloatFields = new Dictionary<string, double>();
        public Dictionary<string, string> stringFields = new Dictionary<string, string>();

        // public Dictionary<string, FadeValue> fields = new Dictionary<string, FadeValue>();
    }

}