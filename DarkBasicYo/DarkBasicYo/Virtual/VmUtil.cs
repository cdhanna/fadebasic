using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
                case VariableType.String:
                    return TypeCodes.STRING;
                case VariableType.Struct:
                    return TypeCodes.STRUCT;
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
                case CommandArgRuntimeState.RegisterRef:
                    GetBytes(value, out var regBytes);
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
                    vm.heap.Allocate(strBytes.Length, out strPtr);
                    vm.heap.Write(strPtr, strBytes.Length, strBytes);
                    vm.dataRegisters[address] = BitConverter.ToUInt32(BitConverter.GetBytes(strPtr), 0);
                    break;
                case CommandArgRuntimeState.HeapRef:
                    
                    VmConverter.FromString(value, out strBytes);
                    vm.heap.Allocate(strBytes.Length, out strPtr);
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
                    // refRegisters[i] = regAddr;
                    var data = vm.dataRegisters[address];
                    typeCode = vm.typeRegisters[address];
                    // TODO: we could validate that typeCode is equal to the given typeCode for <T>
                    var bytes = BitConverter.GetBytes(data);
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
                default:
                    throw new Exception("uh oh, the any type isn't supported for the actual read type");
            }
        }
        
        public static void WriteToHeap(ref FastStack<byte> stack, VmHeap heap, bool pushPtr)
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
            // Read(stack, TypeCodes.INT, out var aBytes);
            result = BitConverter.ToInt32(span.ToArray(), 0);
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
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Multiply(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
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
        public static void Abs(byte aTypeCode, byte[] a, out byte[] c)
        {
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                    // for things that are "unsigned", there is no point
                    c = a;
                    break;
                
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int sumInt = Math.Abs(aInt);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float sumReal = Math.Abs(aReal);
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
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    byte sumByte = (byte)(aByte > bByte ? 1 : 0);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    short sumShort = (short)(aShort > bShort ? 1 : 0);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int sumInt = (int)(aInt > bInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = (bReal > aReal ? 1 : 0);
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
                case TypeCodes.BYTE:
                    byte aByte = a[0];
                    byte bByte = b[0];
                    byte sumByte = (byte)(aByte >= bByte ? 1 : 0);
                    c = new byte[] { sumByte };
                    break;
                case TypeCodes.WORD:
                    short aShort = BitConverter.ToInt16(a, 0);
                    short bShort = BitConverter.ToInt16(b, 0);
                    short sumShort = (short)(aShort >= bShort ? 1 : 0);
                    c = BitConverter.GetBytes(sumShort);
                    break;
                case TypeCodes.INT:
                    int aInt = BitConverter.ToInt32(a, 0);
                    int bInt = BitConverter.ToInt32(b, 0);
                    int sumInt = (int)(aInt >= bInt ? 1 : 0);
                    c = BitConverter.GetBytes(sumInt);
                    break;
                case TypeCodes.REAL:
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = (bReal >= aReal ? 1 : 0);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }
        
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EqualTo(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            var a = aSpan.ToArray();
            var b = bSpan.ToArray();
            switch (aTypeCode)
            {
                case TypeCodes.BYTE:
                case TypeCodes.WORD:
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
                    float aReal = BitConverter.ToSingle(a, 0);
                    float bReal = BitConverter.ToSingle(b, 0);
                    float sumReal = ((Math.Abs(bReal - aReal) <= float.Epsilon) ? 1 : 0);
                    c = BitConverter.GetBytes(sumReal);
                    break;
                default:
                    throw new Exception("Unsupported add operation");
            }
        }

        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Divide(byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
        {
            byte[] a = aSpan.ToArray();
            byte[] b = bSpan.ToArray();
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
        public static void Add(VmHeap heap, byte aTypeCode, ReadOnlySpan<byte> aSpan, ReadOnlySpan<byte> bSpan, out ReadOnlySpan<byte> c)
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
                    heap.Allocate(sumLength, out var sumPtr);
                    
                    // now write the two string bytes into sumPtr
                    heap.Copy(bPtr, sumPtr, bLength);
                    heap.Copy(aPtr, sumPtr + bLength, aLength);

                    c = BitConverter.GetBytes(sumPtr);
                    break;
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
                case TypeCodes.INT:
                    switch (typeCode)
                    {
                        case TypeCodes.WORD:
                            outputSpan = span.Slice(0, 2);
                            // bytes = new byte[] { bytes[0], bytes[1] }; // take the last 2 parts of the int
                            break;
                        case TypeCodes.BYTE:
                            // bytes = new byte[] { bytes[0] };
                            outputSpan = span.Slice(0, 1);
                            break;
                        case TypeCodes.REAL:
                            var actual = BitConverter.ToInt32(span.ToArray(), 0);
                            var castFloat = (float)actual;
                            outputSpan = new ReadOnlySpan<byte>(BitConverter.GetBytes(castFloat));
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
                        case TypeCodes.WORD:
                            // var actual = BitConverter.ToSingle(bytes, 0);
                            var castWord = (ushort)actualFloat;
                            outputSpan = BitConverter.GetBytes(castWord);
                            break;
                        default:
                            throw new NotImplementedException($"cast from float to typeCode=[{typeCode}] is not supported yet.");
                    }
                    break;
                case TypeCodes.BYTE:
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            var castInt = (int)span[0];
                            outputSpan = BitConverter.GetBytes(castInt);
                            break;
                        case TypeCodes.WORD:
                            outputSpan = BitConverter.GetBytes((short)span[0]);
                            break;
                        default:
                            throw new NotImplementedException($"cast from byte to typeCode=[{typeCode}] is not supported yet.");
                    }

                    break;
                case TypeCodes.WORD:
                    short actualWord = BitConverter.ToInt16(span.ToArray(), 0);
                    switch (typeCode)
                    {
                        case TypeCodes.INT:
                            var castInt = (int)actualWord;
                            outputSpan = BitConverter.GetBytes(castInt);
                            break;
                        case TypeCodes.BYTE:
                            outputSpan = BitConverter.GetBytes((byte)actualWord);
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