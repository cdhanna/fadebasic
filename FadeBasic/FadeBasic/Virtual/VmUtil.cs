using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FadeBasic.Ast;

namespace FadeBasic.Virtual
{

    public static class VmUtil
    {
        public static bool TryGetVariableTypeDisplay(int typeCode, out string type)
        {
            type = "void";
            switch (typeCode)
            {
                case TypeCodes.INT:
                    type = VariableType.Integer.ToString();
                    return true;
                case TypeCodes.DINT:
                    type = VariableType.DoubleInteger.ToString();
                    return true;
                case TypeCodes.STRING:
                    type = VariableType.String.ToString();
                    return true;
                case TypeCodes.STRUCT:
                    type = VariableType.Struct.ToString();
                    return true;
                case TypeCodes.REAL:
                    type = VariableType.Float.ToString();
                    return true;
                case TypeCodes.WORD:
                    type = VariableType.Word.ToString();
                    return true;
                case TypeCodes.DWORD:
                    type = VariableType.DWord.ToString();
                    return true;
                case TypeCodes.DFLOAT:
                    type = VariableType.DoubleFloat.ToString();
                    return true;
                case TypeCodes.BYTE:
                    type = VariableType.Byte.ToString();
                    return true;
                case TypeCodes.BOOL:
                    type = VariableType.Boolean.ToString();
                    return true;
                case TypeCodes.VM:
                    type = "vm";
                    return true;
                case TypeCodes.ANY:
                    type = "any";
                    return true;
                default:
                    throw new NotImplementedException("Unknown type code");
            }
        }
        
        public static bool TryGetVariableType(int typeCode, out VariableType type)
        {
            type = VariableType.Void;
            switch (typeCode)
            {
                case TypeCodes.INT:
                    type = VariableType.Integer;
                    return true;
                case TypeCodes.DINT:
                    type = VariableType.DoubleInteger;
                    return true;
                case TypeCodes.STRING:
                    type = VariableType.String;
                    return true;
                case TypeCodes.STRUCT:
                    type = VariableType.Struct;
                    return true;
                case TypeCodes.REAL:
                    type = VariableType.Float;
                    return true;
                case TypeCodes.DFLOAT:
                    type = VariableType.DoubleFloat;
                    return true;
                case TypeCodes.WORD:
                    type = VariableType.Word;
                    return true;
                case TypeCodes.DWORD:
                    type = VariableType.DWord;
                    return true;
                case TypeCodes.BYTE:
                    type = VariableType.Byte;
                    return true;
                case TypeCodes.BOOL:
                    type = VariableType.Boolean;
                    return true;
                case TypeCodes.ANY:
                    return false;
                default:
                    throw new NotImplementedException("Unknown type code");
            }
        }
        
        public static byte GetTypeCode(VariableType vt)
        {
            switch (vt)
            {
                case VariableType.Integer:
                    return TypeCodes.INT;
                case VariableType.Boolean:
                    return TypeCodes.BOOL;
                case VariableType.Byte:
                    return TypeCodes.BYTE;
                case VariableType.Word:
                    return TypeCodes.WORD;
                case VariableType.DWord:
                    return TypeCodes.DWORD;
                case VariableType.DoubleFloat:
                    return TypeCodes.DFLOAT;
                case VariableType.DoubleInteger:
                    return TypeCodes.DINT;
                case VariableType.Float:
                    return TypeCodes.REAL;
                case VariableType.String:
                    return TypeCodes.STRING;
                case VariableType.Struct:
                    return TypeCodes.STRUCT;
                default:
                    throw new NotImplementedException("Unknown type code");
            }
        }

        public static int ConvertToInt(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            int output = BitConverter.ToInt32(outputRegisterBytes, 0);
            return output;
        }
        public static float ConvertToFloat(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            float output = BitConverter.ToSingle(outputRegisterBytes, 0);
            return output;
        }

        public static byte ConvertToByte(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            return outputRegisterBytes[0];
        }
        
        public static ushort ConvertToWord(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            return BitConverter.ToUInt16(outputRegisterBytes, 0);
        }

        public static uint ConvertToDWord(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            return BitConverter.ToUInt32(outputRegisterBytes, 0);
        }
        
        
        public static long ConvertToDInt(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            return BitConverter.ToInt64(outputRegisterBytes, 0);
        }
        
        
        public static double ConvertToDFloat(ulong rawValue)
        {
            var outputRegisterBytes = BitConverter.GetBytes(rawValue);
            return BitConverter.ToDouble(outputRegisterBytes, 0);
        }
        
        public static string GetTypeName(byte typeCode, VirtualMachine vm, ulong rawValue)
        {
            if (!VmUtil.TryGetVariableTypeDisplay(typeCode, out var typeName))
            {
                return "UNKNOWN";
            }

            if (typeCode == TypeCodes.STRUCT)
            {
                // the rawValue is a ptr into the heap...
                if (vm.heap.TryGetAllocation((int) rawValue, out var allocation))
                {
                    var typeId = allocation.format.typeId;
                    var type = vm.typeTable[typeId];
                    typeName = type.name;
                }
            }

            return typeName;
        }

        public static string ConvertValueToDisplayString(byte typeCode, VirtualMachine vm, ref ReadOnlySpan<byte> span)
        {
            switch (typeCode)
            {
                case TypeCodes.INT:
                    return BitConverter.ToInt32(span.ToArray(), 0).ToString();
                case TypeCodes.WORD:
                    return BitConverter.ToUInt16(span.ToArray(), 0).ToString();
                case TypeCodes.DWORD:
                    return BitConverter.ToUInt32(span.ToArray(), 0).ToString();
                case TypeCodes.DINT:
                    return BitConverter.ToInt64(span.ToArray(), 0).ToString();
                case TypeCodes.BOOL:
                    return span[0] == 0 ? "false" : "true";
                case TypeCodes.REAL:
                    var num = BitConverter.ToSingle(span.ToArray(), 0);
                    return num.ToString(CultureInfo.InvariantCulture);
                case TypeCodes.DFLOAT:
                    var num2 = BitConverter.ToDouble(span.ToArray(), 0);
                    return num2.ToString(CultureInfo.InvariantCulture);
                case TypeCodes.STRING:
                    var address = BitConverter.ToInt32(span.ToArray(), 0);
                    if (vm.heap.TryGetAllocationSize(address, out var strSize))
                    {
                        vm.heap.Read(address, strSize, out var strBytes);
                        return  VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        return "<?>";
                    }

                default:
                    throw new NotImplementedException($"don't know how to convert span of size=[{span.Length}] to typecode=[{typeCode}]");
                //
                // case TypeCodes.STRING:
                //     var address = (int)rawValue;
                //     if (vm.heap.TryGetAllocationSize(address, out var strSize))
                //     {
                //         vm.heap.Read(address, strSize, out var strBytes);
                //         return  VmConverter.ToString(strBytes);
                //     }
                //     else
                //     {
                //         return "<?>";
                //     }
             
                // case TypeCodes.STRUCT:
                //     return "[" + GetTypeName(typeCode, vm, rawValue) + "]";
                //     
                // default:
                //     return rawValue.ToString();
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object DbgConvert(byte typeCode, ref ReadOnlySpan<byte> values)
        {
            switch (typeCode)
            {
                case TypeCodes.BYTE:
                    return values[0];
                case TypeCodes.WORD:
                    return BitConverter.ToInt16(values.ToArray(), 0);
                case TypeCodes.INT:
                    return BitConverter.ToInt32(values.ToArray(), 0);
                case TypeCodes.REAL:
                    return BitConverter.ToSingle(values.ToArray(), 0);
                case TypeCodes.BOOL:
                    return values[0] == 1;
                default:
                    throw new Exception("DbgConvert unsupported typecode");
            }
        }


        public static void GetBytes<T>(T value, out byte[] bytes)
        {
            switch (value)
            {
                case int i:
                    bytes = BitConverter.GetBytes(i);
                    break;
                default:
                    throw new NotImplementedException("cannot convert unknown type to bytes " + value + " typeof " + typeof(T));
            }
        }
        public static void HandleValue<T>(VirtualMachine vm, T value, byte typeCode, CommandArgRuntimeState state, int address) where T : struct
        {
            switch (state)
            {
                case CommandArgRuntimeState.GlobalRegisterRef:
                    GetBytes(value, out var regBytes);
                    vm.globalScope.dataRegisters[address] = BitConverter.ToUInt32(regBytes, 0);
                    break;
                case CommandArgRuntimeState.RegisterRef:
                    GetBytes(value, out regBytes);
                    vm.dataRegisters[address] = BitConverter.ToUInt32(regBytes, 0);
                    break;
                case CommandArgRuntimeState.HeapRef:
                    var size = TypeCodes.GetByteSize(typeCode);
                    GetBytes(value, out var heapBytes);
                    vm.heap.Write(address, size, heapBytes);
                    break;
                case CommandArgRuntimeState.Value:
                    // do nothing.
                    break;
            }
        }
        
        
        public static void HandleValueString(VirtualMachine vm, string value, byte typeCode, CommandArgRuntimeState state, int address)
        {
            byte[] strBytes;
            int strPtr;
            switch (state)
            {
                case CommandArgRuntimeState.RegisterRef:
                    VmConverter.FromString(value, out strBytes);
                    vm.heap.Allocate(ref HeapTypeFormat.STRING_FORMAT, strBytes.Length, out strPtr);
                    vm.heap.Write(strPtr, strBytes.Length, strBytes);
                    vm.dataRegisters[address] = BitConverter.ToUInt32(BitConverter.GetBytes(strPtr), 0);
                    vm.typeRegisters[address] = typeCode;
                    break;
                case CommandArgRuntimeState.HeapRef:
                    
                    VmConverter.FromString(value, out strBytes);
                    vm.heap.Allocate(ref HeapTypeFormat.STRING_FORMAT,strBytes.Length, out strPtr);
                    vm.heap.Write(strPtr, strBytes.Length, strBytes);
                    vm.heap.Write(address, 4, BitConverter.GetBytes(strPtr));
                    break;
                case CommandArgRuntimeState.Value:
                    // do nothing.
                    break;
            }
        }

        
        // public static void ReadValue(VirtualMachine vm, string defaultValue, out string )
        public static void ReadValueString(VirtualMachine vm, string defaultValue, out string value,
            out CommandArgRuntimeState state, out int address)
        {
            ReadSpan(ref vm.stack, out var typeCode, out var span);
            int strPtr = 0, strSize = 0;
            switch (typeCode)
            {
                case TypeCodes.VOID:
                    address = 0;
                    state = CommandArgRuntimeState.Value;
                    value = defaultValue;
                    break;
                case TypeCodes.PTR_REG:
                    state = CommandArgRuntimeState.RegisterRef;
                    address = (int) span[0];
                    var data = vm.dataRegisters[address];
                    typeCode = vm.typeRegisters[address];
                    // TODO: we could validate that typeCode is equal to the given typeCode for <T>
                    var bytes = BitConverter.GetBytes(data);
                    strPtr = (int)BitConverter.ToUInt32(bytes, 0);
                    if (vm.heap.TryGetAllocationSize(strPtr, out strSize))
                    {
                        vm.heap.Read(strPtr, strSize, out var strBytes);
                        value = VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        value = null;
                    }
                    break;
                case TypeCodes.PTR_GLOBAL_REG:
                    state = CommandArgRuntimeState.GlobalRegisterRef;
                    address = (int) span[0];
                    data = vm.globalScope.dataRegisters[address];
                    typeCode = vm.globalScope.typeRegisters[address];
                    // TODO: we could validate that typeCode is equal to the given typeCode for <T>
                    bytes = BitConverter.GetBytes(data);
                    strPtr = (int)BitConverter.ToUInt32(bytes, 0);
                    if (vm.heap.TryGetAllocationSize(strPtr, out strSize))
                    {
                        vm.heap.Read(strPtr, strSize, out var strBytes);
                        value = VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        value = null;
                    }
                    break;
                case TypeCodes.PTR_HEAP:
                    state = CommandArgRuntimeState.HeapRef;
                    address = MemoryMarshal.Read<int>(span);
                    // the heap does not store type info, which means we need to assume the next value on the stack is the type code.
                    typeCode = vm.stack.Pop();
                    if (vm.heap.TryGetAllocationSize(address, out strSize))
                    {
                        vm.heap.Read(address, strSize, out var strBytes);
                        value = VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        value = null;
                    }

                    break;
                default:
                    state = CommandArgRuntimeState.Value;
                    address = 0;
                    strPtr = MemoryMarshal.Read<int>(span);
                    if (vm.heap.TryGetAllocationSize(strPtr, out strSize))
                    {
                        vm.heap.Read(strPtr, strSize, out var strBytes);
                        value = VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        value = null;
                    }
                    break;
            }
        }
        
        public static void ReadValue<T>(VirtualMachine vm, T defaultValue, out T value, out CommandArgRuntimeState state, out int address) where T : struct
        {
            ReadSpan(ref vm.stack, out var typeCode, out var span);
            switch (typeCode)
            {
                case TypeCodes.VOID:
                    address = 0;
                    state = CommandArgRuntimeState.Value;
                    value = defaultValue;
                    break;
                case TypeCodes.PTR_REG:
                    state = CommandArgRuntimeState.RegisterRef;
                    address = (int) span[0];
                    var data = vm.dataRegisters[address];
                    typeCode = vm.typeRegisters[address];
                    var bytes = BitConverter.GetBytes(data);
                    value = MemoryMarshal.Read<T>(bytes);
                    break;
                case TypeCodes.PTR_GLOBAL_REG:
                    state = CommandArgRuntimeState.GlobalRegisterRef;
                    address = (int) span[0];
                    data = vm.globalScope.dataRegisters[address];
                    typeCode = vm.globalScope.typeRegisters[address];
                    bytes = BitConverter.GetBytes(data);
                    value = MemoryMarshal.Read<T>(bytes);
                    break;
                case TypeCodes.PTR_HEAP:
                    state = CommandArgRuntimeState.HeapRef;
                    address = MemoryMarshal.Read<int>(span);
                    
                    // the heap does not store type info, which means we need to assume the next value on the stack is the type code.
                    typeCode = vm.stack.Pop();

                    // if it is a string, then the de-dupe happens later, but if it is a string, then we need to actually go do the lookup
                    // if (typeCode != TypeCodes.STRING)
                    {
                        var size = TypeCodes.GetByteSize(typeCode);
                        // vm.heap.Read(address, size, out bytes);
                        vm.heap.ReadSpan(address, size, out span);
                    }
                    value = MemoryMarshal.Read<T>(span);

                    break;
                default:
                    state = CommandArgRuntimeState.Value;
                    address = 0;
                    value = MemoryMarshal.Read<T>(span);
                    break;
            }
        }
        
        
        public static void ReadValueAny(VirtualMachine vm, object defaultValue, out object value, out CommandArgRuntimeState state, out int address)
        {
            // peek the type code...
            var peekTypeCode = vm.stack.Peek();
            switch (peekTypeCode)
            {
                case TypeCodes.STRING:
                    ReadValueString(vm, defaultValue?.ToString(), out var strValue, out state, out address);
                    value = strValue;
                    break;
                case TypeCodes.INT:
                    ReadValue<int>(vm, default, out var intValue, out state, out address);
                    value = intValue;
                    break;
                case TypeCodes.REAL:
                    ReadValue<float>(vm, default, out var floatValue, out state, out address);
                    value = floatValue;
                    break;
                case TypeCodes.DFLOAT:
                    ReadValue<double>(vm, default, out var doubleValue, out state, out address);
                    value = doubleValue;
                    break;
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ReadValue<byte>(vm, default, out var byteValue, out state, out address);
                    value = byteValue;
                    break;
                case TypeCodes.WORD:
                    ReadValue<ushort>(vm, default, out var shortValue, out state, out address);
                    value = shortValue;
                    break;
                case TypeCodes.DWORD:
                    ReadValue<uint>(vm, default, out var dwordValue, out state, out address);
                    value = dwordValue;
                    break;
                case TypeCodes.DINT:
                    ReadValue<long>(vm, default, out var dintValue, out state, out address);
                    value = dintValue;
                    break;
                default:
                    throw new Exception("uh oh, the any type isn't supported for the actual read type");
            }
        }
        
        public static void WriteToHeap(ref FastStack<byte> stack, ref VmHeap heap, bool pushPtr)
        {
            // ReadAsInt(stack, out var writePtr);
            var typeCode = stack.Pop();
            if (typeCode != TypeCodes.INT)
            {
                throw new Exception("vm exception: expected an int for ptr");
            }
            
            ReadSpan(ref stack, TypeCodes.INT, out var aSpan);
                            
            // Read(stack, TypeCodes.INT, out var aBytes);
            var writePtr = BitConverter.ToInt32(aSpan.ToArray(), 0);
            
            ReadAsInt(ref stack, out var writeLength);

            // var bytes = new byte[writeLength];
            stack.PopArraySpan(writeLength, out var span);
            
            /*
             * In the old system, this just happened to work by accident.
             * but the issue is that the stack has 3,0,0,0 on it,
             * but the write-length is set to the size of the entry, which is 2.
             * so the write-length only finds 0,0 instead of 0,3
             *
             * Or I guess, we could CAST the type to the desired type...
             */
            
            // for (var w = 0; w < writeLength; w++)
            // {
            //     var b = stack.Pop();
            //     bytes[w] = b;
            // }
                            
            //heap.Write(writePtr, writeLength, span.ToArray());
            heap.WriteSpan(writePtr, writeLength, span);

            if (pushPtr)
            {
                PushSpan(ref stack, aSpan, TypeCodes.INT);
                // Push(stack, aBytes, TypeCodes.INT);
            }
        }
        
        public static void ReadAsInt(ref FastStack<byte> stack, out int result)
        {
            var typeCode = stack.Pop();
            if (typeCode != TypeCodes.INT)
            {
                throw new Exception("vm exception: expected an int");
            }
            ReadSpan(ref stack, TypeCodes.INT, out var span);
            result = BitConverter.ToInt32(span.ToArray(), 0);
        }

        public static void ReadAsTypeFormat(ref FastStack<byte> stack, out HeapTypeFormat format)
        {
            stack.PopArraySpan(HeapTypeFormat.SIZE, out var span);
            format.typeFlags = span[0];
            format.typeCode = span[1];
            format.typeId = BitConverter.ToInt32(span.ToArray(), 2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadSpan(ref FastStack<byte> stack, byte typeCode, out ReadOnlySpan<byte> value)
        {
            var size = TypeCodes.GetByteSize(typeCode);

            stack.PopArraySpan(size, out value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadSpan(ref FastStack<byte> stack, out byte typeCode, out ReadOnlySpan<byte> value)
        {
            typeCode = stack.Pop();
            ReadSpan(ref stack, typeCode, out value);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadSpanAsUInt(ref FastStack<byte> stack, out ulong value)
        {
            var typeCode = stack.Pop();
            var size = TypeCodes.GetByteSize(typeCode);

            switch (size)
            {
                case sizeof(int):
                    value = (ulong)BitConverter.ToInt32(stack.buffer, stack.ptr - size);
                    stack.ptr -= size;
                    break;
                default:
                    
                    var span = new ReadOnlySpan<byte>(stack.buffer, stack.ptr - size, size);
            
                    stack.ptr -= size;
                    byte[] toLongConversionUtil = new byte[8];

                    for (var i = 0; i < size; i++)
                    {
                        toLongConversionUtil[i] = 0;
                    }
                    for (var i = size - 1; i >= 0; i--)
                    {
                        toLongConversionUtil[i] = span[i];
                    }
                    value = BitConverter.ToUInt64(toLongConversionUtil, 0);
                    break;
            }
        }


        static void ConvertTwoBytes(byte[] aBytes, byte[] bBytes, out byte a, out byte b)
        {
            a = aBytes[0];
            b = bBytes[0];
        }
        static void ConvertTwoWords(byte[] aBytes, byte[] bBytes, out ushort a, out ushort b)
        {
            a = BitConverter.ToUInt16(aBytes, 0);
            b = BitConverter.ToUInt16(bBytes, 0);
        }
        static void ConvertTwoDInts(byte[] aBytes, byte[] bBytes, out long a, out long b)
        {
            a = BitConverter.ToInt64(aBytes, 0);
            b = BitConverter.ToInt64(bBytes, 0);
        }
        static void ConvertTwoDFloats(byte[] aBytes, byte[] bBytes, out double a, out double b)
        {
            a = BitConverter.ToDouble(aBytes, 0);
            b = BitConverter.ToDouble(bBytes, 0);
        }
        static void ConvertTwoInts(byte[] aBytes, byte[] bBytes, out int a, out int b)
        {
            a = BitConverter.ToInt32(aBytes, 0);
            b = BitConverter.ToInt32(bBytes, 0);
        }
        static void ConvertTwoDWords(byte[] aBytes, byte[] bBytes, out uint a, out uint b)
        {
            a = BitConverter.ToUInt32(aBytes, 0);
            b = BitConverter.ToUInt32(bBytes, 0);
        }
        
        static void ConvertTwoFloats(byte[] aBytes, byte[] bBytes, out float a, out float b)
        {
            a = BitConverter.ToSingle(aBytes, 0);
            b = BitConverter.ToSingle(bBytes, 0);
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Multiply(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float resultReal = (aReal * bReal);
                    c = BitConverter.GetBytes(resultReal);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int resultInt = (int)(aInt * bInt);
                    c = BitConverter.GetBytes(resultInt);
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDword, out var bDword);
                    uint resultDword = (uint)(aDword * bDword);
                    c = BitConverter.GetBytes(resultDword);
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aWord, out var bWord);
                    ushort resultWord = (ushort)(aWord * bWord);
                    c = BitConverter.GetBytes(resultWord);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aLong, out var bLong);
                    long resultLong = (long)(aLong * bLong);
                    c = BitConverter.GetBytes(resultLong);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDouble, out var bDouble);
                    double resultDouble = (double)(aDouble * bDouble);
                    c = BitConverter.GetBytes(resultDouble);
                    break;
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte byteResult = (byte)(aByte * bByte);
                    c = new byte[] { byteResult };
                    break;

                default:
                    throw new Exception("Unsupported multiply operation");
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Power(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c, out bool isInvalid)
        {
            isInvalid = false;
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte sumByte = (byte)Math.Pow(bByte, aByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aWord, out var bWord);
                    ushort sumShort = (ushort)Math.Pow(bWord, aWord);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDword, out var bDword);
                    uint dWordResult = (uint)Math.Pow(bDword, aDword);
                    c = BitConverter.GetBytes(dWordResult);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDFloat, out var bDFloat);
                    double dFloatResult = (double)Math.Pow(bDFloat, aDFloat);
                    c = BitConverter.GetBytes(dFloatResult);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int sumInt = (int)Math.Pow(bInt, aInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    long dblInt = (long)Math.Pow(bDInt, aDInt);
                    c = BitConverter.GetBytes(dblInt);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float sumReal =(float)Math.Pow((double)bReal,(double)aReal);
                    if (float.IsNaN(sumReal))
                    {
                        isInvalid = true;
                    }
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported power operation");
            }
        }
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Not(byte aTypeCode, ReadOnlySpan<byte> aSpan, out ReadOnlySpan<byte> c)
        {
            var a = aSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte sumByte = (byte)(aByte > 0 ? 0 : 1);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short sumShort = (short)(aShort >0 ? 0 : 1);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int sumInt = (int)(aInt > 0 ? 0 : 1);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float sumReal = (aReal > 0 ? 0 : 1);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported not operation");
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GreaterThan(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            var a = aSpan.ToArray();
            var b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte sumByte = (byte)(aByte > bByte ? 1 : 0);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aWord, out var bWord);
                    ushort sumShort = (ushort)(aWord > bWord ? 1 : 0);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDWord, out var bDWord);
                    uint dWordResult = (uint)(aDWord > bDWord ? 1 : 0);
                    c = BitConverter.GetBytes(dWordResult);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int sumInt = (int)(aInt > bInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    long sumDInt = (long)(aDInt > bDInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumDInt);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    double sumDReal = (aDReal > bDReal ? 1 : 0);
                    c = BitConverter.GetBytes(sumDReal);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float sumReal = (aReal > bReal ? 1 : 0);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void GreaterThanOrEqualTo(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            var a = aSpan.ToArray();
            var b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte sumByte = (byte)(aByte >= bByte ? 1 : 0);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aShort, out var bShort);
                    ushort sumShort = (ushort)(aShort >= bShort ? 1 : 0);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDShort, out var bDShort);
                    uint sumDShort = (uint)(aDShort >= bDShort ? 1 : 0);
                    c = BitConverter.GetBytes(sumDShort);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int sumInt = (int)(aInt >= bInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    long sumDInt = (long)(aDInt >= bDInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumDInt);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float sumReal = (aReal >= bReal ? 1 : 0);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    double sumDReal = (aDReal >= bDReal ? 1 : 0);
                    c = BitConverter.GetBytes(sumDReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EqualTo(ref FastStack<byte> stack, ref VmHeap heap, byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            var a = aSpan.ToArray();
            var b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                case TypeCodes.WORD:
                case TypeCodes.DWORD:
                case TypeCodes.DINT:
                case TypeCodes.INT:
                    var equal = 1;
                    for (var i = 0; i < a.Length; i++)
                    {
                        if (a[i] != b[i])
                        {
                            equal = 0;
                            break;
                        }
                    }
                    c = BitConverter.GetBytes(equal);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    int sumReal = ((Math.Abs(bReal - aReal) <= float.Epsilon) ? 1 : 0);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    double doubleResult = ((Math.Abs(bDReal - aDReal) <= double.Epsilon) ? 1 : 0);
                    c = BitConverter.GetBytes(doubleResult);
                    break;
                case TypeCodes.STRING:
                    c = new byte[] { 0 };
                    
                    // read the data at a
                    // VmUtil.ReadAsInt();

                    var aPtr = BitConverter.ToInt32(a, 0);
                    var bPtr = BitConverter.ToInt32(b, 0);
                    
                    heap.GetAllocationSize(aPtr, out var aSize);
                    heap.GetAllocationSize(bPtr, out var bSize);

                    if (aSize != bSize)
                    {
                        c = BitConverter.GetBytes(0);
                        break;
                    }
                    
                    heap.ReadSpan(aPtr, aSize, out var aMem);
                    heap.ReadSpan(bPtr, bSize, out var bMem);

                    c = BitConverter.GetBytes(1);
                    for (var i = 0; i < aSize; i++)
                    {
                        if (aMem[i] != bMem[i])
                        {
                            c = BitConverter.GetBytes(0);
                            break;
                        }
                    }

                    break;
                default:
                    throw new Exception("Unsupported equality operation");
            }
        }

        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Divide(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c, out bool isDivideByZero)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            c = a;
            isDivideByZero = false;
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    if (aByte == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    byte sumByte = (byte)(bByte / aByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDword, out var bDword);
                    if (aDword == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    uint resultDword = (uint)(bDword / aDword);
                    c = BitConverter.GetBytes(resultDword);
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aShort, out var bShort);
                    if (aShort == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    ushort sumShort = (ushort)(bShort / aShort);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    if (aInt == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    int sumInt = (int)(bInt / aInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    if (aDInt == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    long sumDInt = (long)(bDInt / aDInt);
                    c = BitConverter.GetBytes(sumDInt);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    if (aReal == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    float sumReal = (bReal / aReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    if (aDReal == 0)
                    {
                        isDivideByZero = true;
                        return;
                    }
                    double sumDReal = (bDReal / aDReal);
                    c = BitConverter.GetBytes(sumDReal);
                    break;
                default:
                    throw new Exception("Unsupported divide operation");
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Mod(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte sumByte = (byte)(bByte % aByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aWord, out var bWord);
                    ushort sumShort = (ushort)(bWord % aWord);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aDword, out var bDword);
                    uint dwordResult = (uint)(bDword % aDword);
                    c = BitConverter.GetBytes(dwordResult);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int sumInt = (int)(bInt % aInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    long sumDInt = (long)(bDInt % aDInt);
                    c = BitConverter.GetBytes(sumDInt);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float sumReal = (bReal % aReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    double sumDReal = (bDReal % aDReal);
                    c = BitConverter.GetBytes(sumDReal);
                    break;
                default:
                    throw new Exception("Unsupported mod operation");
            }
        }
        
        
        public static void Nand(ref VmHeap heap, byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int result = ~(aInt & bInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported NAND operation typecode=[{aTypeCode}]");
            }
        }
        
        public static void BitwiseAnd(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int result = (aInt & bInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise AND operation typecode=[{aTypeCode}]");
            }
        }
        
        public static void BitwiseOr(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int result = (aInt | bInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise Or operation typecode=[{aTypeCode}]");
            }
        }
        
        public static void BitwiseXor(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            int aInt = 0;
            int bInt = 0;
            int result = 0;
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    aInt = a[0];
                    bInt = b[0];
                    result = (aInt ^ bInt);
                    c = new ReadOnlySpan<byte>(new byte[] { (byte) result});
                    break;
                case TypeCodes.INT:
                    aInt = BitConverter.ToInt32(a, 0);
                    bInt = BitConverter.ToInt32(b, 0);
                    result = (aInt ^ bInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise XOR operation typecode=[{aTypeCode}]");
            }
        }
        
        
        public static void BitwiseLeftShift(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int result = (bInt << aInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise Leftshift operation typecode=[{aTypeCode}]");
            }
        }
        
        public static void BitwiseRightShift(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            ReadOnlySpan<byte> bSpan,
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int result = (bInt >> aInt);
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise Rightshift operation typecode=[{aTypeCode}]");
            }
        }

        
        public static void BitwiseNot(
            ref VmHeap heap, 
            byte aTypeCode, 
            ReadOnlySpan<byte> aSpan, 
            out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int result = ~aInt;
                    c = BitConverter.GetBytes(result);
                    break;
                default:
                    throw new Exception($"Unsupported Bitwise NOT operation typecode=[{aTypeCode}]");
            }
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(ref VmHeap heap, byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.STRING:
                    /*
                     * two pointers; we need to allocate a new string for the sum of the lengths;
                     * then array copy the two strings into their respective positions
                     */
                    var aPtr = BitConverter.ToInt32(a, 0);
                    var bPtr = BitConverter.ToInt32(b, 0);
                    heap.GetAllocationSize(aPtr, out var aLength);
                    heap.GetAllocationSize(bPtr, out var bLength);
                    var sumLength = aLength + bLength;
                    
                    heap.Allocate(ref HeapTypeFormat.STRING_FORMAT, sumLength, out var sumPtr);
                    
                    // now write the two string bytes into sumPtr
                    heap.Copy(bPtr, sumPtr, bLength);
                    heap.Copy(aPtr, sumPtr + bLength, aLength);

                    c = BitConverter.GetBytes(sumPtr);
                    break;
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    ConvertTwoBytes(a, b, out var aByte, out var bByte);
                    byte sumByte = (byte)(aByte + bByte);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.DWORD:
                    ConvertTwoDWords(a, b, out var aUint, out var bUint);
                    uint sunUInt = (uint)(aUint + bUint);
                    c = BitConverter.GetBytes(sunUInt);
                    break;
                case TypeCodes.WORD:
                    ConvertTwoWords(a, b, out var aShort, out var bShort);
                    ushort sumShort = (ushort)(aShort + bShort);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    ConvertTwoInts(a, b, out var aInt, out var bInt);
                    int sumInt = (int)(aInt + bInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.DINT:
                    ConvertTwoDInts(a, b, out var aDInt, out var bDInt);
                    long sumDInt = (long)(aDInt + bDInt);
                    c = BitConverter.GetBytes(sumDInt);
                    break;
                case TypeCodes.REAL:
                    ConvertTwoFloats(a, b, out var aReal, out var bReal);
                    float sumReal = (aReal + bReal);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                case TypeCodes.DFLOAT:
                    ConvertTwoDFloats(a, b, out var aDReal, out var bDReal);
                    double sumDReal = (aDReal + bDReal);
                    c = BitConverter.GetBytes(sumDReal);
                    break;
                default:
                    throw new Exception($"Unsupported add operation typecode=[{aTypeCode}]");
            }
        }

        public static void CastInlineSpan(ReadOnlySpan<byte> span, byte currentTypeCode, byte typeCode, ref ReadOnlySpan<byte> outputSpan)
        {
            
            if (currentTypeCode == typeCode)
            {
                outputSpan = span;
                return;
            }

            switch (currentTypeCode)
            {
                case TypeCodes.DINT:
                    var actualDInt = BitConverter.ToInt64(span.ToArray(), 0);
                    switch (typeCode)
                    {
                        case TypeCodes.REAL:
                            outputSpan = BitConverter.GetBytes((float)actualDInt);
                            break;
                        case TypeCodes.DFLOAT:
                            outputSpan = BitConverter.GetBytes((double)actualDInt);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((ushort)actualDInt);
                            break;
                        case TypeCodes.DWORD:
                            outputSpan = BitConverter.GetBytes((uint)actualDInt);
                            break;
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)actualDInt);
                            break;
                        case TypeCodes.BYTE:
                        case TypeCodes.BOOL:
                            outputSpan = BitConverter.GetBytes((byte)actualDInt);
                            break;
                        default:
                            throw new NotImplementedException($"cast from int to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.DFLOAT:
                    var actualDbl = BitConverter.ToDouble(span.ToArray(), 0);

                    switch (typeCode)
                    {
                        case TypeCodes.REAL:
                            outputSpan = BitConverter.GetBytes((float)actualDbl);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((ushort)actualDbl);
                            break;
                        case TypeCodes.DWORD:
                            outputSpan = BitConverter.GetBytes((uint)actualDbl);
                            break;
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)actualDbl);
                            break;
                        case TypeCodes.DINT:
                            outputSpan = BitConverter.GetBytes((long)actualDbl);
                            break;
                        case TypeCodes.BYTE:
                        case TypeCodes.BOOL:
                            outputSpan = BitConverter.GetBytes((byte)actualDbl);
                            break;
                        default:
                            throw new NotImplementedException($"cast from int to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.INT:
                    switch (typeCode)
                    {
                        case TypeCodes.DINT:
                            var intVersion = BitConverter.ToInt32(span.ToArray(), 0);
                            var castLong = (long)intVersion;
                            outputSpan = new ReadOnlySpan<byte>(BitConverter.GetBytes(castLong));
                            break;
                        case TypeCodes.DWORD:
                            var actualDword = BitConverter.ToUInt32(span.ToArray(), 0);
                            var castDword = (uint)actualDword;
                            outputSpan = new ReadOnlySpan<byte>(BitConverter.GetBytes(castDword));
                            break;
                        case TypeCodes.WORD:
                            outputSpan = span.Slice(0, 2);
                            // bytes = new byte[] { bytes[0], bytes[1] }; // take the last 2 parts of the int
                            break;
                        case TypeCodes.BOOL:
                        case TypeCodes.BYTE:
                            outputSpan = span.Slice(0, 1);
                            break;
                        case TypeCodes.REAL:
                            var actual = BitConverter.ToInt32(span.ToArray(), 0);
                            var castFloat = (float)actual;
                            outputSpan = new ReadOnlySpan<byte>(BitConverter.GetBytes(castFloat));
                            break;
                        case TypeCodes.DFLOAT:
                            var actualDFloat = BitConverter.ToInt32(span.ToArray(), 0);
                            var castDbl = (double)actualDFloat;
                            outputSpan = new ReadOnlySpan<byte>(BitConverter.GetBytes(castDbl));
                            break;
                        case TypeCodes.PTR_HEAP:
                        case TypeCodes.STRUCT:
                        case TypeCodes.STRING:
                            // a string type IS just an int ptr; so we don't need to convert anything!
                            outputSpan = span;
                            break;
                        default:
                            throw new NotImplementedException($"cast from int to typeCode=[{typeCode}] is not supported yet.");
                    }

                    break;
                case TypeCodes.REAL:
                    var actualFloat = BitConverter.ToSingle(span.ToArray(), 0);
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)actualFloat);
                            break;
                        case TypeCodes.DINT:
                            outputSpan = BitConverter.GetBytes((long)actualFloat);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((ushort)actualFloat);
                            break;
                        case TypeCodes.DWORD:
                            outputSpan = BitConverter.GetBytes((uint)actualFloat);
                            break;
                        case TypeCodes.DFLOAT:
                            outputSpan = BitConverter.GetBytes((double)actualFloat);
                            break;
                        case TypeCodes.BOOL:
                        case TypeCodes.BYTE:
                            outputSpan = BitConverter.GetBytes((byte)actualFloat);
                            break;
                        default:
                            throw new NotImplementedException($"cast from float to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.BOOL:
                case TypeCodes.BYTE:
                    switch (typeCode)
                    {
                        case TypeCodes.BYTE:
                        case TypeCodes.BOOL:
                            outputSpan = span;
                            break;
                        case TypeCodes.DFLOAT:
                            outputSpan = BitConverter.GetBytes((double)span[0]);
                            break;
                        case TypeCodes.REAL:
                            outputSpan = BitConverter.GetBytes((float)span[0]);
                            break;
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)span[0]);
                            break;
                        case TypeCodes.DINT:
                            outputSpan = BitConverter.GetBytes((long)span[0]);
                            break;
                        case TypeCodes.DWORD:
                            outputSpan = BitConverter.GetBytes((uint)span[0]);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((ushort)span[0]);
                            break;
                        default:
                            throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                    }

                    break;
                case TypeCodes.WORD:
                    ushort actualWord = BitConverter.ToUInt16(span.ToArray(), 0);
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)actualWord);
                            break;
                        case TypeCodes.DINT:
                            outputSpan = BitConverter.GetBytes((long)actualWord);
                            break;
                        case TypeCodes.DWORD:
                            outputSpan = BitConverter.GetBytes((uint)actualWord);
                            break;
                        case TypeCodes.DFLOAT:
                            outputSpan = BitConverter.GetBytes((double)actualWord);
                            break;
                        case TypeCodes.REAL:
                            outputSpan = BitConverter.GetBytes((float)actualWord);
                            break;
                        case TypeCodes.BOOL:
                        case TypeCodes.BYTE:
                            outputSpan = BitConverter.GetBytes((byte)actualWord);
                            break;
                        default:
                            throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.DWORD:
                    uint actualDWord = BitConverter.ToUInt32(span.ToArray(), 0);
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            outputSpan = BitConverter.GetBytes((int)actualDWord);
                            break;
                        case TypeCodes.DINT:
                            outputSpan = BitConverter.GetBytes((long)actualDWord);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((ushort)actualDWord);
                            break;
                        case TypeCodes.DFLOAT:
                            outputSpan = BitConverter.GetBytes((double)actualDWord);
                            break;
                        case TypeCodes.REAL:
                            outputSpan = BitConverter.GetBytes((float)actualDWord);
                            break;
                        case TypeCodes.BOOL:
                        case TypeCodes.BYTE:
                            outputSpan = BitConverter.GetBytes((byte)actualDWord);
                            break;
                        default:
                            throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.STRUCT:
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            outputSpan = span;
                            break;
                        default:
                            throw new NotImplementedException($"cast from struct ptr to typeCode=[{typeCode}] is not supported yet.");
                    }

                    break;
                default:
                    throw new NotImplementedException($"casts from typeCode=[{currentTypeCode}] types are not supported. target=[{typeCode}]");

            }
            
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Cast(ref FastStack<byte> stack, byte typeCode)
        {
            var currentTypeCode = stack.Peek();
            if (currentTypeCode == typeCode) return; // don't need to do anything!
            ReadSpan(ref stack, out currentTypeCode, out var span);
            
            CastInlineSpan(span, currentTypeCode, typeCode, ref span);
            
            PushSpan(ref stack, span, typeCode);
        }
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void PushSpan(ref FastStack<byte> stack, ReadOnlySpan<byte> span, byte typeCode)
        {
            var byteSize = TypeCodes.GetByteSize(typeCode);
            stack.PushSpanAndType(span, typeCode, byteSize);
        }
        

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadTwoValues(ref FastStack<byte> stack, out byte typeCode, out ReadOnlySpan<byte> aBytes, out ReadOnlySpan<byte> bBytes)
        {
            var aTypeCode = stack.Pop();
            ReadSpan(ref stack, aTypeCode, out aBytes);
                            
            var bTypeCode = stack.Pop();
            ReadSpan(ref stack, bTypeCode, out bBytes);

            // if the type codes are not the same, take the bigger one...

            var aOrder = TypeCodes.GetOrder(aTypeCode);
            var bOrder = TypeCodes.GetOrder(bTypeCode);

            if (aOrder > bOrder)
            {
                CastInlineSpan(bBytes, bTypeCode, aTypeCode, ref bBytes);
                typeCode = aTypeCode;
            }
            else
            {
                CastInlineSpan(aBytes, aTypeCode, bTypeCode, ref aBytes);
                typeCode = bTypeCode;
            }
        }

        public static void GetMinMax(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out bool needFlip)
        {
            var a = aSpan.ToArray();
            var b = bSpan.ToArray();
            switch (aTypeCode)
            {

                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    needFlip = aByte > bByte;
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    needFlip = aShort > bShort;
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    needFlip = aInt > bInt;
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    needFlip = aReal > bReal;
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }
    }
}