using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual
{
    public class DebugRuntimeVariable
    {
        public string name;
        public byte typeCode;
        public ulong rawValue;
        public int stackIndex;

        public string GetValueDisplay(VirtualMachine vm)
        {
            switch (typeCode)
            {
                case TypeCodes.REAL:
                    return FloatValue.ToString();
                case TypeCodes.STRING:
                    throw new NotImplementedException("need to look in heap");
                case TypeCodes.BOOL:
                    return rawValue == 0 ? "false" : "true";
                default:
                    return rawValue.ToString();
            }
        
        }

        public string TypeName
        {
            get
            {
                if (!VmUtil.TryGetVariableTypeDisplay(typeCode, out var typeName))
                {
                    return "UNKNOWN";
                }

                return typeName;
            }
        }

        public float FloatValue
        {
            get
            {
                if (typeCode != TypeCodes.REAL) return 0;
                var outputRegisterBytes = BitConverter.GetBytes(rawValue);
                float output = BitConverter.ToSingle(outputRegisterBytes, 0);
                return output;
            }
        }
    }

    public static class DebugUtil
    {
        
        public static Dictionary<string, DebugRuntimeVariable> LookupVariables(VirtualMachine vm, DebugData dbg, int index=-1)
        {
            var results = new Dictionary<string, DebugRuntimeVariable>();
            for (var i = 0; i < vm.scopeStack.ptr; i++)
            {
                if (index != -1 && i != index) continue;
                
                var scope = vm.scopeStack.buffer[i];

                for (var scopeIndex = 0; scopeIndex < scope.dataRegisters.Length; scopeIndex++)
                {
                    var insIndex = scope.insIndexes[scopeIndex];
                    if (!dbg.insToVariable.TryGetValue(insIndex, out var variable))
                    {
                        // as soon as we do not have a variable, then there are no more variables to be known!
                        break;
                    }

                    results[variable.name] = new DebugRuntimeVariable
                    {
                        name = variable.name,
                        stackIndex = i,
                        typeCode = scope.typeRegisters[scopeIndex],
                        rawValue = scope.dataRegisters[scopeIndex]
                    };


                }
            }

            return results;

        }
    }
}