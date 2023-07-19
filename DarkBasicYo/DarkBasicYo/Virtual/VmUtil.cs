using System;
using System.Collections.Generic;

namespace DarkBasicYo.Virtual
{
    public static class VmUtil
    {
        
        public static object DbgConvert(byte typeCode, byte[] values)
        {
            switch (typeCode)
            {
                case TypeCodes.BYTE:
                    return values[0];
                case TypeCodes.WORD:
                    return BitConverter.ToInt16(values, 0);
                case TypeCodes.INT:
                    return BitConverter.ToInt32(values, 0);
                case TypeCodes.REAL:
                    return BitConverter.ToSingle(values, 0);
                case TypeCodes.BOOL:
                    return values[0] == 1;
                default:
                    throw new Exception("DbgConvert unsupported typecode");
            }
        }

        public static void Read(Stack<byte> stack, byte typeCode, out byte[] value)
        {
            var size = TypeCodes.GetByteSize(typeCode);
            value = new byte[size];
            for (var n = 0; n < size; n++)
            {
                var aPart = stack.Pop();
                value[n] = aPart;
            }
        }
        
        
        public static void Multiply(byte aTypeCode, byte[] a, byte[] b, out byte[] c)
        {
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    byte sumByte = (byte)(aByte * bByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    short sumShort = (short)(aShort * bShort);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int sumInt = (int)(aInt * bInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = (aReal * bReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }


        public static void Add(byte aTypeCode, byte[] a, byte[] b, out byte[] c)
        {
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    byte sumByte = (byte)(aByte + bByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    short sumShort = (short)(aShort + bShort);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int sumInt = (int)(aInt + bInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = (aReal + bReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }
        
        public static void Push(Stack<byte> stack, byte[] values, byte typeCode)
        {
            var typeSize = TypeCodes.GetByteSize(typeCode);
            for (var n = typeSize - 1; n >= 0; n --)
            {
                stack.Push(values[n]);
            }
            stack.Push(typeCode);
        }
        

        public static void Pad(byte size, byte[] bytes, out byte[] output)
        {
            if (size == bytes.Length)
            {
                output = bytes;
            }
            else
            {
                output = new byte[size];
                for (var i = bytes.Length - 1; i >= 0; i--)
                {
                    output[i] = bytes[i];
                }
            }
        }

        public static void ReadTwoValues(Stack<byte> stack, out byte typeCode, out byte[] aBytes, out byte[] bBytes)
        {
            var aTypeCode = stack.Pop();
            Read(stack, aTypeCode, out aBytes);
                            
            var bTypeCode = stack.Pop();
            Read(stack, bTypeCode, out bBytes);

            // if the type codes are not the same, take the bigger one...
            var bigTypeCode = aTypeCode;
            var aSize = TypeCodes.GetByteSize(aTypeCode);
            var bSize = TypeCodes.GetByteSize(bTypeCode);
            if (bSize > aSize) bigTypeCode = bTypeCode;
            var bigSize = TypeCodes.GetByteSize(bigTypeCode);

            Pad(bigSize, aBytes, out aBytes);
            Pad(bigSize, bBytes, out bBytes);
            typeCode = bigTypeCode;
        }
        
    }
}