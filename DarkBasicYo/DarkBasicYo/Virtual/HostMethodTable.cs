using System;
using System.Collections.Generic;
using System.Reflection;

namespace DarkBasicYo.Virtual
{
    public class HostMethodTable
    {
        public HostMethod[] methods;

        public void FindMethod(int methodAddr, out HostMethod method)
        {
            method = methods[methodAddr];
        }
    }

    public static class HostMethodUtil
    {

        private static Dictionary<Type, byte> _typeToTypeCode;

        static HostMethodUtil()
        {
           // var x= typeof(IntPtr);
            _typeToTypeCode = new Dictionary<Type, byte>
            {
                [typeof(int)] = TypeCodes.INT,
                [typeof(void)] = TypeCodes.VOID,
                [typeof(string)] = TypeCodes.STRING
                // [typeof(IntPtr)] = TypeCodes.INT
            };
        }

        public static HostMethod BuildHostMethodViaReflection(MethodInfo method)
        {
            
            // we need to know what types of values to require on the stack,
            var parameters = method.GetParameters();
            var argTypeCodes = new List<byte>();
            // var argTypes = new List<Type>();
            var defaultArgValues = new object[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var parameterType = parameter.ParameterType;

                
                if (!_typeToTypeCode.TryGetValue(parameterType, out var parameterTypeCode))
                {
                    
                    if (parameterType.IsByRef)
                    {
                        var subType = parameterType.GetElementType();
                        if (!_typeToTypeCode.TryGetValue(subType, out parameterTypeCode))
                        {
                            throw new Exception("HostMethodBuilder: unsupported ptr arg type " + parameterType.Name);
                        }
                    }
                    else
                    {
                        throw new Exception("HostMethodBuilder: unsupported arg type " + parameterType.Name);
                    }
                    
                }

                if (parameter.IsOptional)
                {
                    defaultArgValues[i] = parameter.DefaultValue;
                }
                argTypeCodes.Add(parameterTypeCode);
                
                // argTypes.Add(parameterType);
            }

            // if (method.ReturnType != typeof(Void))
            
            if (!_typeToTypeCode.TryGetValue(method.ReturnType, out var returnTypeCode))
            {
                throw new Exception("HostMethodBuilder: unsupported return type " + method.ReturnType.Name);
            }
            
            
            
            return new HostMethod
            {
                function = method,
                returnTypeCode = returnTypeCode,
                argTypeCodes = argTypeCodes.ToArray(),
                defaultArgValues = defaultArgValues
                // argTypes = argTypes.ToArray()
            };
        }

        public static HostMethod BuildHostMethodViaReflection(Type clazz, string methodName)
        {
            var method = clazz.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
            return BuildHostMethodViaReflection(method);
        }

        
        public static void Execute(HostMethod method, VirtualMachine machine)
        {
            // if there are args, then we need to pull those values off the stack and cast them.

            var argInstances = new object[method.argTypeCodes.Length];
            var refRegisters = new int[method.argTypeCodes.Length];
            var refHeaps = new int[method.argTypeCodes.Length];
            var readTypeCodes = new int[method.argTypeCodes.Length];
            for (var i = method.argTypeCodes.Length - 1; i >= 0; i --)
            // for (var i = 0; i < method.argTypeCodes.Length; i++)
            {
                
                
                VmUtil.Read(machine.stack, out var typeCode, out var bytes);
                readTypeCodes[i] = typeCode;
                if (typeCode == TypeCodes.PTR_REG)
                {
                    // resolve this before we continue...
                    var regAddr = (int)bytes[0];
                    refRegisters[i] = regAddr;
                    var data = machine.dataRegisters[regAddr];
                    typeCode = machine.typeRegisters[regAddr];
                    bytes = BitConverter.GetBytes(data);
                }
                //
                // if (typeCode == TypeCodes.PTR_HEAP)
                // {
                //     var heapPtr = BitConverter.ToInt32(bytes, 0);
                //     refHeaps[i] = heapPtr;
                // }

                if (typeCode == TypeCodes.VOID)
                {
                    // use the optional value...
                    argInstances[i] = method.defaultArgValues[i];
                    continue;
                }
                
                var expectedTypeCode = method.argTypeCodes[i];

                
                // if (typeCode == TypeCodes.PTR_HEAP)
                // {
                //     switch (expectedTypeCode)
                //     {
                //         case TypeCodes.STRING:
                //             // time to get our string out of the heap
                //             
                //             break;
                //         default:
                //             throw new InvalidOperationException("ptrHeap is on the stack, but the function needs a " +
                //                                                 expectedTypeCode);
                //     }
                // }

                
                if (expectedTypeCode == TypeCodes.STRING)
                {
                    if (typeCode != TypeCodes.INT && typeCode != TypeCodes.STRING 
                                                  // && typeCode != TypeCodes.PTR_HEAP
                                                  )
                    {
                        throw new Exception("Expected to find an int ptr to a string");
                    }
                    
                    // read the string
                    var strPtr = (int)BitConverter.ToUInt32(bytes, 0);
                    machine.heap.GetAllocationSize(strPtr, out var strSize);
                    machine.heap.Read(strPtr, strSize, out var strBytes);
                    var str = VmConverter.ToString(strBytes);
                    argInstances[i] = str;
                    continue;
                }
                
                if (expectedTypeCode != typeCode)
                {
                    throw new Exception($"Expected to find typeCode=[{expectedTypeCode}], but found=[{typeCode}]");
                }

                switch (expectedTypeCode)
                {
                    case TypeCodes.INT:
                        argInstances[i] = BitConverter.ToInt32(bytes, 0);
                        break;
                    default:
                        throw new Exception("Unexpected type code for calling host method. " + expectedTypeCode);
                }
            }
            
            var result = method.function?.Invoke(null, argInstances);
            
            // check for ref parameters that need to be restored...
            for (var i = 0; i < method.argTypeCodes.Length; i++)
            {
                if (readTypeCodes[i] == TypeCodes.PTR_REG)
                {
                    var data = argInstances[i];
                    var regAddr = refRegisters[i];
                    switch (method.argTypeCodes[i])
                    {
                        case TypeCodes.INT:
                            var castInt = (int)data;
                            machine.dataRegisters[regAddr] = BitConverter.ToUInt32(BitConverter.GetBytes(castInt), 0);
                            break;
                        case TypeCodes.STRING:
                            var castStr = (string)data;
                            
                            VmConverter.FromString(castStr, out var strBytes);
                            machine.heap.Allocate(strBytes.Length, out var strPtr);
                            machine.heap.Write(strPtr, strBytes.Length, strBytes);
                            machine.dataRegisters[regAddr] = BitConverter.ToUInt32(BitConverter.GetBytes(strPtr), 0);
                            
                            break;
                        default:
                            throw new NotImplementedException("cannot handle ref reserialization for type code " +
                                                              method.argTypeCodes[i]);
                    }
                } 
                // else if (readTypeCodes[i] == TypeCodes.PTR_HEAP)
                // {
                //
                //     var data = argInstances[i];
                //     switch (method.argTypeCodes[i])
                //     {
                //         case TypeCodes.STRING:
                //             var castStr = (string)data;
                //             break;
                //         
                //         default:
                //             throw new NotImplementedException("cannot handle ref reserialization for type code " +
                //                                               method.argTypeCodes[i]);
                //     }
                // }
            }
            
            switch (method.returnTypeCode)
            {
                case TypeCodes.INT:
                    var resultInt = (int)result;
                    var bytes = BitConverter.GetBytes(resultInt);
                    VmUtil.Push(machine.stack, bytes, method.returnTypeCode);
                    break;
                case TypeCodes.STRING:
                    var resultStr = (string)result;
                    VmConverter.FromString(resultStr, out bytes);
                    machine.heap.Allocate(bytes.Length, out var resultStrPtr);
                    machine.heap.Write(resultStrPtr, bytes.Length, bytes);
                    var ptrIntBytes = BitConverter.GetBytes(resultStrPtr);
                    VmUtil.Push(machine.stack, ptrIntBytes, TypeCodes.STRING);
                    break;
                case TypeCodes.VOID:
                    // do nothing, since there is no return type
                    break;
                default:
                    throw new Exception("Unsupported return type code, " + method.returnTypeCode);
            }
            
        }
    }

    public class HostMethod
    {
        // public string name;
        // TODO: args?

        public byte[] argTypeCodes;
        public byte returnTypeCode;
        public object[] defaultArgValues;
        public MethodInfo function;
    }
}