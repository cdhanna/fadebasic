using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Virtual
{
    public static class VmUtil
    {
        public static byte GetTypeCode(VariableType vt)
        {
            switch (vt)
            {
                case VariableType.Integer:
                    return TypeCodes.INT;
                case VariableType.Byte:
                    return TypeCodes.BYTE;
                case VariableType.Word:
                    return TypeCodes.WORD;
                case VariableType.Float:
                    return TypeCodes.REAL;
                default:
                    throw new NotImplementedException("Unknown type code");
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        public static void ReadAsInt(Stack<byte> stack, out int result)
        {
            var typeCode = stack.Pop();
            if (typeCode != TypeCodes.INT)
            {
                throw new Exception("vm exception: expected an int");
            }
                            
            Read(stack, TypeCodes.INT, out var aBytes);
            result = BitConverter.ToInt32(aBytes, 0);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        
        public static void Read(Stack<byte> stack, out byte typeCode, out byte[] value)
        {
            typeCode = stack.Pop();
            Read(stack, typeCode, out value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Divide(byte aTypeCode, byte[] a, byte[] b, out byte[] c)
        {
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    byte sumByte = (byte)(aByte / bByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    short sumShort = (short)(aShort / bShort);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int sumInt = (int)(aInt / bInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = (bReal / aReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        public static void Cast(Stack<byte> stack, byte typeCode)
        {
            Read(stack, out var currentTypeCode, out var bytes);
            if (currentTypeCode == typeCode)
            {
                Push(stack, bytes, typeCode);
                return;
            }

            
            // handle int conversions
            if (currentTypeCode == TypeCodes.INT)
            {
                switch (typeCode)
                {
                    case TypeCodes.WORD:
                        bytes = new byte[] { bytes[0], bytes[1] }; // take the last 2 parts of the int
                        break;
                    case TypeCodes.BYTE:
                        bytes = new byte[] { bytes[0] };
                        break;
                    case TypeCodes.REAL:
                        var actual = BitConverter.ToInt32(bytes, 0);
                        var castFloat = (float)actual;
                        bytes = BitConverter.GetBytes(castFloat);
                        break;
                    default:
                        throw new NotImplementedException($"cast from int to typeCode=[{typeCode}] is not supported yet.");
                }
            }
            else if (currentTypeCode == TypeCodes.REAL)
            {
                switch (typeCode)
                {
                    case TypeCodes.WORD:
                        var actual = BitConverter.ToSingle(bytes, 0);
                        var castWord = (ushort)actual;
                        bytes = BitConverter.GetBytes(castWord);
                        break;
                    default:
                        throw new NotImplementedException($"cast from float to typeCode=[{typeCode}] is not supported yet.");
                }
            }
            else if (currentTypeCode == TypeCodes.BYTE)
            {
                switch (typeCode)
                {
                    case TypeCodes.INT:
                        var castInt = (int)bytes[0];
                        bytes = BitConverter.GetBytes(castInt);
                        break;
                    default:
                        throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                }
            }
            else if (currentTypeCode == TypeCodes.WORD)
            {
                switch (typeCode)
                {
                    case TypeCodes.INT:
                        short actualWord = BitConverter.ToInt16(bytes, 0);
                        var castInt = (int)actualWord;
                        bytes = BitConverter.GetBytes(castInt);
                        break;
                    default:
                        throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                }
            }
            else
            {
                throw new NotImplementedException($"casts from typeCode=[{currentTypeCode}] types are not supported. target=[{typeCode}]");
            }

            Push(stack, bytes, typeCode);
            // var expectedSize = TypeCodes.GetByteSize(typeCode);
            // for (var n = expectedSize -1 ; n >= 0; n --)
            // {
            //     stack.Push(bytes[n]);
            // }
            // stack.Push(typeCode);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Push(Stack<byte> stack, byte[] values, byte typeCode)
        {
            var typeSize = TypeCodes.GetByteSize(typeCode);
            for (var n = typeSize - 1; n >= 0; n --)
            {
                stack.Push(values[n]);
            }
            stack.Push(typeCode);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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