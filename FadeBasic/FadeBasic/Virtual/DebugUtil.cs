using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual
{
    public class DebugRuntimeVariable
    {
        public string name;
        public byte typeCode;
        public ulong rawValue;

        public string GetValueDisplay(VirtualMachine vm)
        {
            switch (typeCode)
            {
                case TypeCodes.REAL:
                    return FloatValue.ToString();
                case TypeCodes.STRING:
                    var address = (int)rawValue;
                    if (vm.heap.TryGetAllocationSize(address, out var strSize))
                    {
                        vm.heap.Read(address, strSize, out var strBytes);
                        return  VmConverter.ToString(strBytes);
                    }
                    else
                    {
                        return "<?>";
                    }
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
        public static Dictionary<string, DebugRuntimeVariable> LookupVariablesFromScope( Dictionary<string, DebugRuntimeVariable> results, DebugData dbg, ref VirtualScope scope, bool global)
        {
            for (var scopeIndex = 0; scopeIndex < scope.dataRegisters.Length; scopeIndex++)
            {
                var insIndex = scope.insIndexes[scopeIndex];
                if (!dbg.insToVariable.TryGetValue(insIndex, out var variable))
                {
                    // TODO: feels like there should be a better way than searching the entire space. 
                    continue;
                }

                var isVariableGlobal = scope.globalFlag[scopeIndex] == 1;
                if (global && !isVariableGlobal) continue; 
                if (!global && isVariableGlobal) continue; 
                
                results[variable.name] = new DebugRuntimeVariable
                {
                    name = variable.name,
                    typeCode = scope.typeRegisters[scopeIndex],
                    rawValue = scope.dataRegisters[scopeIndex]
                };
            }

            return results;
        }
        
        
        public static Dictionary<string, DebugRuntimeVariable> LookupVariables(VirtualMachine vm, DebugData dbg, int index=-1, bool global=false)
        {
            var results = new Dictionary<string, DebugRuntimeVariable>();
            for (var i = 0; i < vm.scopeStack.Count; i++)
            {
                
                if (!global && index != -1 && i != index) continue;
                var reverseIndex = vm.scopeStack.Count - (i + 1);
                var scope = vm.scopeStack.buffer[reverseIndex];

                LookupVariablesFromScope(results, dbg, ref scope, global);
            }
            
            

            return results;

        }
    }
}