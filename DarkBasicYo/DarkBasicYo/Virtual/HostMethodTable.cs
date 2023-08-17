using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DarkBasicYo.Virtual
{
    public struct HostMethodTable
    {
        public CommandInfo[] methods;

        public void FindMethod(int methodAddr, out CommandInfo method)
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

        private static bool TryBuildExecutorCache(MethodInfo method, out Func<object[], object> executor)
        {
            executor = null;
            if (method.ReturnType != typeof(void))
            {
                return false; // TODO: return types are not cachable yet.
            }

            var parameters = method.GetParameters();
            var parameterTypes = parameters.Select(x => x.ParameterType).ToArray();
            
            switch (parameters.Length)
            {
                // case 1 when parameters[0].ParameterType == typeof(int):
                //
                //     // var type = typeof(Action<>).MakeGenericType(parameterTypes);
                //     var deleg = (Action<int>)Delegate.CreateDelegate(typeof(Action<int>), method);
                //     executor = p =>
                //     {
                //         deleg.Invoke((int)p[0]);
                //         return null;
                //     };
                //     break;
                case 1 when !parameterTypes[0].HasElementType:
                // case 1:
                
                    var capture = BuildConvert1Arg(parameterTypes[0], method);
                    executor = p => capture(p[0]);
                    break;
                default:
                    return false; // TODO: support caching for higher parameter count
            }

            
            
            return false;
        }

        private static Func<object, object> BuildConvert1Arg(Type type, MethodInfo captureMethod)
        {

            if (type.HasElementType)
            {
                type = type.GetElementType();
            }
            
            var bf = BindingFlags.Static | BindingFlags.NonPublic;
            var genMethod = typeof(HostMethodUtil).GetMethod(nameof(Convert1Arg), bf);
            var method = genMethod.MakeGenericMethod(type);
            var result = method.Invoke(null, new object[] { captureMethod });
            var castResult = (Action<object>)result;
            return x =>
            {
                castResult(x);
                return null;
            };
        }
        
        private static Action<object> Convert1Arg<T>(MethodInfo method)
        {
            try
            {
                var strong = (Action<T>)Delegate.CreateDelegate(typeof(Action<T>), method);
                Action<object> weak = (arg) => strong((T)arg);
                return weak;
            }
            catch (Exception ex)
            {
                // TODO: WHY CAN I NOT USE A REF TYPE AS A GENERIC ARG!?!?!?!?!!??
                Console.WriteLine(ex.Message);
                throw;
            }
        }
        
        private static bool TryBuildDelegateType(MethodInfo method, out Type type)
        {
            type = null;
            
            if (method.ReturnType != typeof(void))
            {
                return false; // TODO: return types are not cachable yet.
            }
        
            var parameters = method.GetParameters();
            var parameterTypes = parameters.Select(x => x.ParameterType).ToArray();
            switch (parameters.Length)
            {
                case 1:
        
                    type = typeof(Action<>).MakeGenericType(parameterTypes);
                    // var deleg = Delegate.CreateDelegate(type, method);
                    break;
                default:
                    return false; // TODO: support caching for higher parameter count
            }
        
            return type != null;
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
                    else if (typeof(VirtualMachine).IsAssignableFrom(parameterType))
                    {
                        // this is allowed, set 
                        parameterTypeCode = TypeCodes.VM;
                    }
                    else if (typeof(CommandArgObject) == parameterType)
                    {
                        parameterTypeCode = TypeCodes.ANY;
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


       
            var hostMethod = new HostMethod
            {
                function = method,
                returnTypeCode = returnTypeCode,
                argTypeCodes = argTypeCodes.ToArray(),
                defaultArgValues = defaultArgValues,
                // executor = instanceParams => method.Invoke(null, instanceParams)
                // argTypes = argTypes.ToArray()
            };
            // if (TryBuildExecutorCache(method, out var executor))
            {
                // hostMethod.executor = p => null;
            }
            // if (TryBuildDelegateType(method, out var dType))
            // {
            //     hostMethod.executor = (instanceParams) =>
            //     {
            //         
            //         return null;
            //     };
            //     var cached = Delegate.CreateDelegate(dType, method);
            //     // hostMethod.cachedFunction = (Func<object, object[]>)(parameters => cached);
            // }

            return hostMethod;

        }

        public static HostMethod BuildHostMethodViaReflection(Type clazz, string methodName)
        {
            var method = clazz.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public);
            return BuildHostMethodViaReflection(method);
        }

        public static void Execute(CommandInfo method, VirtualMachine machine)
        {
            method.executor(machine);
        }
        
        public static void Execute(HostMethod method, VirtualMachine machine)
        {
            //return;
            // if there are args, then we need to pull those values off the stack and cast them.

            var argInstances = new object[method.argTypeCodes.Length];
            var refRegisters = new int[method.argTypeCodes.Length];
            var refHeaps = new int[method.argTypeCodes.Length];
            var readTypeCodes = new int[method.argTypeCodes.Length];
            for (var i = method.argTypeCodes.Length - 1; i >= 0; i --)
            // for (var i = 0; i < method.argTypeCodes.Length; i++)
            {
                if (method.argTypeCodes[i] == TypeCodes.VM)
                {
                    argInstances[i] = machine;
                    continue;
                }

                VmUtil.ReadSpan(ref machine.stack, out var typeCode, out var span);
                var bytes = span.ToArray();
                readTypeCodes[i] = typeCode;
                
                
                if (method.argTypeCodes[i] == TypeCodes.ANY)
                {
                    argInstances[i] = new CommandArgObject
                    {
                        bytes = bytes,
                        typeCode = typeCode
                    };
                    continue;
                }

                
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
                if (typeCode == TypeCodes.PTR_HEAP)
                {
                    var heapPtr = BitConverter.ToInt32(bytes, 0);
                    refHeaps[i] = heapPtr;
                    
                    // the heap does not store type info, which means we need to assume the next value on the stack is the type code.
                    typeCode = machine.stack.Pop();

                    // if it is a string, then the de-dupe happens later, but if it is a string, then we need to actually go do the lookup
                    if (typeCode != TypeCodes.STRING)
                    {
                        var size = TypeCodes.GetByteSize(typeCode);
                        machine.heap.Read(heapPtr, size, out bytes);
                    }

                }

                if (typeCode == TypeCodes.VOID)
                {
                    // use the optional value...
                    argInstances[i] = method.defaultArgValues[i];
                    continue;
                }
                
                var expectedTypeCode = method.argTypeCodes[i];
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
                    if (machine.heap.TryGetAllocationSize(strPtr, out var strSize))
                    {
                        machine.heap.Read(strPtr, strSize, out var strBytes);
                        var str = VmConverter.ToString(strBytes);
                        argInstances[i] = str;
                    }
                    else
                    {
                        argInstances[i] = null;
                    }
                    
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
            // var result = method.executor(argInstances);
            
            // check for ref parameters that need to be restored...
            for (var i = 0; i < method.argTypeCodes.Length; i++)
            {
                var data = argInstances[i];
                if (readTypeCodes[i] == TypeCodes.PTR_HEAP)
                {
                    var heapAddr = refHeaps[i];
                    switch (method.argTypeCodes[i])
                    {
                        case TypeCodes.STRING:
                            // allocate the string, and assign its ptr to the heapAddr.
                            var castStr = (string)data;
                            
                            VmConverter.FromString(castStr, out var strBytes);
                            machine.heap.Allocate(strBytes.Length, out var strPtr);
                            machine.heap.Write(strPtr, strBytes.Length, strBytes);
                            machine.heap.Write(heapAddr, 4, BitConverter.GetBytes(strPtr));
                            break;
                        case TypeCodes.INT:
                            var castInt = (int)data;
                            var size = TypeCodes.GetByteSize(method.argTypeCodes[i]);
                            var intBytes = BitConverter.GetBytes(castInt);
                            machine.heap.Write(heapAddr, size, intBytes);
                            break;
                        default:
                            throw new NotImplementedException("cannot go from heap ptr to type code");
                    }
                } else if (readTypeCodes[i] == TypeCodes.PTR_REG)
                {
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
            }
            
            switch (method.returnTypeCode)
            {
                case TypeCodes.INT:
                    var resultInt = (int)result;
                    var bytes = BitConverter.GetBytes(resultInt);
                    VmUtil.PushSpan(ref machine.stack, bytes, method.returnTypeCode);
                    break;
                case TypeCodes.STRING:
                    var resultStr = (string)result;
                    VmConverter.FromString(resultStr, out bytes);
                    machine.heap.Allocate(bytes.Length, out var resultStrPtr);
                    machine.heap.Write(resultStrPtr, bytes.Length, bytes);
                    var ptrIntBytes = BitConverter.GetBytes(resultStrPtr);
                    VmUtil.PushSpan(ref machine.stack, ptrIntBytes, TypeCodes.STRING);
                    break;
                case TypeCodes.VOID:
                    // do nothing, since there is no return type
                    break;
                default:
                    throw new Exception("Unsupported return type code, " + method.returnTypeCode);
            }
            
        }
    }

    public struct HostMethod
    {
        // public string name;
        // TODO: args?

        public byte[] argTypeCodes;
        public byte returnTypeCode;
        public object[] defaultArgValues;
        public MethodInfo function;
        // public Func<object[], object> executor;
        
    }
}