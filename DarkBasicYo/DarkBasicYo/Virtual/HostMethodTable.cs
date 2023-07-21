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
            _typeToTypeCode = new Dictionary<Type, byte>
            {
                [typeof(int)] = TypeCodes.INT,
                [typeof(void)] = TypeCodes.VOID
            };
        }

        public static HostMethod BuildHostMethodViaReflection(MethodInfo method)
        {
            
            // we need to know what types of values to require on the stack,
            var parameters = method.GetParameters();
            var argTypeCodes = new List<byte>();
            // var argTypes = new List<Type>();
            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var parameterType = parameter.ParameterType;

                if (!_typeToTypeCode.TryGetValue(parameterType, out var parameterTypeCode))
                {
                    throw new Exception("HostMethodBuilder: unsupported arg type " + parameterType.Name);
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
            for (var i = 0; i < method.argTypeCodes.Length; i++)
            {
                VmUtil.Read(machine.stack, out var typeCode, out var bytes);

                var expectedTypeCode = method.argTypeCodes[i];
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
            switch (method.returnTypeCode)
            {
                case TypeCodes.INT:
                    var resultInt = (int)result;
                    var bytes = BitConverter.GetBytes(resultInt);
                    VmUtil.Push(machine.stack, bytes, method.returnTypeCode);
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
        // public Type[] argTypes;
        public MethodInfo function;
    }
}