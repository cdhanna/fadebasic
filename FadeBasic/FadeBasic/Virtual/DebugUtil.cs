using System;
using System.Collections.Generic;
using FadeBasic.Launch;

namespace FadeBasic.Virtual
{
    public class DebugRuntimeVariable
    {
        public readonly string name;
        public readonly byte typeCode;
        public ulong rawValue; // could be a ptr.
        public readonly int scopeIndex;
        public readonly int regAddr;

        public readonly VmAllocation allocation;
        public readonly VirtualMachine vm;

        public DebugRuntimeVariable(VirtualMachine vm, string name, byte typeCode, ulong rawValue, ref VmAllocation allocation, int scopeIndex, int regAddr)
        {
            this.vm = vm;
            this.name = name;
            this.typeCode = typeCode;
            this.rawValue = rawValue;
            this.allocation = allocation;
            this.scopeIndex = scopeIndex;
            this.regAddr = regAddr;
            
        }
        public DebugRuntimeVariable(VirtualMachine vm, string name, byte typeCode, ulong rawValue, int scopeIndex, int regAddr)
        {
            
            this.vm = vm;
            this.name = name;
            this.typeCode = typeCode;
            this.rawValue = rawValue;
            this.scopeIndex = scopeIndex;
            this.regAddr = regAddr;

            // we know at this moment if the rawValue is a ptr or not...
            if (typeCode == TypeCodes.STRUCT || typeCode == TypeCodes.STRING)
            {
                if (!this.vm.heap.TryGetAllocation((int) rawValue, out allocation))
                {
                    throw new InvalidOperationException("There is no allocation for the struct reference. hh");
                }
            }

        }

        public string GetValueDisplay()
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
                case TypeCodes.STRUCT:
                    // LOOK UP IN HEAP? 
                    return "[" + GetTypeName() + "]";
                    
                    break;
                default:
                    return rawValue.ToString();
            }
        
        }

        public InternedType GetInternedType()
        {
            if (typeCode != TypeCodes.STRUCT)
                throw new NotSupportedException("Cannot get the interned type on a field that is not a struct");
            
            var type = vm.typeTable[allocation.format.typeId];
            return type;
        }
        
        public int GetFieldCount()
        {
            if (typeCode == TypeCodes.STRUCT)
                return GetInternedType().fields.Count;
            return 0;
        }

        public int arrayRankIndex = 0;

        public int GetElementCount()
        {
            return GetElementCount(out _);
        }
        public int GetElementCount(out byte rankCount)
        {
            return GetElementCount(out rankCount, out _);
        }
        public int GetElementCount(out byte rankCount, out bool isArray)
        {
            isArray = false;
            if (!allocation.format.IsArray(out rankCount))
            {
                return 0;
            }

            isArray = true;
            /*
             * If you know the register address of the base variable..
             * and we know the number of ranks in the array.
             *
             * Then the data-registers starting at the variable-address-reg + 1, are the sizes of inner arrays, alternating with the stride multipler. 
             */

            var scope = vm.scopeStack.buffer[scopeIndex];

            /*
             * The math here is confusing.
             * Start at the regAddr, which the base address of the array.
             * Then, arrays have 2 registers for each rank, 1 for the element count in the rank, and 1 for stride length of the elements.
             * But they are organized so that the first (leftmost) rank is the last one in the register space.
             * So start by moving all the way to the end of the register space ( + rankCount*2)
             * Then move backwards by our current rank index (-arrayRankIndex*2)
             * And then of the two registers, the element size is 1 back. 
             */
            var elementRegAddr = regAddr + rankCount * 2 - (arrayRankIndex * 2 ) - 1;
            var elementCount = scope.dataRegisters[elementRegAddr];
            
            return (int)elementCount;
            // if (allocation.format.typeFlags
        }
        
        // public InternedType GetInternedStructType()
        // {
        //     if (_vm.heap.TryGetAllocation((int) rawValue, out var allocation))
        //     {
        //         var typeId = allocation.format.typeId;
        //         var type = _vm.typeTable[typeId];
        //         return type;
        //     }
        //
        //     throw new InvalidOperationException("There is no allocation for the struct reference. hh");
        // }

        public string GetTypeName()
        {
            if (typeCode == TypeCodes.STRUCT)
            {
                // the rawValue is a ptr into the heap...
                
                // if (vm.heap.TryGetAllocation((int) rawValue, out var ))
                {
                    var typeId = allocation.format.typeId;
                    var type = vm.typeTable[typeId];
                    return type.name;
                }
            }
            
            if (!VmUtil.TryGetVariableTypeDisplay(typeCode, out var typeName))
            {
                return "UNKNOWN";
            }
            return typeName;
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


    public class DebugVariableDatabase
    {
        private readonly VirtualMachine _vm;
        private readonly DebugData _dbg;
        private readonly IDebugLogger _logger;

        private int _variableIdCounter;
        
        // public List<DebugRuntimeVariable> variables = new List<DebugRuntimeVariable>();

        private Dictionary<int, DebugScope> frameToLocals = new Dictionary<int, DebugScope>();
        private Dictionary<int, DebugScope> frameToGlobals = new Dictionary<int, DebugScope>();

        private Dictionary<int, (DebugScope, DebugRuntimeVariable)> idToTopLevelVariable = new Dictionary<int, (DebugScope, DebugRuntimeVariable)>();

        private Dictionary<int, DebugScope> idToScope = new Dictionary<int, DebugScope>();

        private Dictionary<string, int> evalNameToId = new Dictionary<string, int>();
        
        public DebugVariableDatabase(VirtualMachine vm, DebugData dbgData, IDebugLogger logger)
        {
            _vm = vm;
            _dbg = dbgData;
            _logger = logger;
            ClearLifetime();
        }

        public bool TryGetRuntimeVariable(int id, out DebugRuntimeVariable variable)
        {
            variable = null;
            if (!idToTopLevelVariable.TryGetValue(id, out var t))
            {
                return false;
            }

            variable = t.Item2;
            return true;
        }
        
        public void ClearLifetime()
        {
            _variableIdCounter = 1;
            frameToLocals.Clear();
            frameToGlobals.Clear();
            idToTopLevelVariable.Clear();
            idToScope.Clear();
            evalNameToId.Clear();
            // variables.Clear();
        }

        public int NextId()
        {
            return _variableIdCounter++;
        }

        public DebugRuntimeVariable GetRuntimeVariable(Launch.DebugVariable variable)
        {
            return idToTopLevelVariable[variable.id].Item2;
        }
        
        private Launch.DebugVariable CreateVariableView(DebugScope scope, DebugRuntimeVariable variable)
        {
            var v = new Launch.DebugVariable
            {
                id = _variableIdCounter++,
                name = variable.name,
                value = variable.GetValueDisplay(),
                type = variable.GetTypeName(),
                evalName = variable.name
            };

            idToTopLevelVariable[v.id] = (scope, variable);

            v.fieldCount = variable.GetFieldCount();
            v.elementCount = variable.GetElementCount();
            if (v.elementCount > 0)
            {
                v.value = $"({v.elementCount})";
                v.evalName = null;
            }

            if (v.evalName != null)
            {
                evalNameToId[v.evalName] = v.id;
            }

            return v;
        }
        
        private DebugScope GetVariablesForFrame(Dictionary<int, DebugScope> section, int frameIndex, string scopeName, bool global)
        {
            if (section.TryGetValue(frameIndex, out var scope))
            {
                return scope;
            }
            
            var dict = DebugUtil.LookupVariables(_vm, _dbg, frameIndex, global: global);
            scope = new DebugScope
            {
                scopeName = scopeName,
                id = NextId()
            };
            foreach (var kvp in dict)
            {
                var v = CreateVariableView(scope, kvp.Value);
                scope.variables.Add(v);
            }

            section[frameIndex] = scope;
            return scope;
        }

        public DebugScope GetGlobalVariablesForFrame(int frameIndex)
        {
            return GetVariablesForFrame(frameToGlobals, frameIndex, "Globals", true);
        }
        
        public DebugScope GetLocalVariablesForFrame(int frameIndex)
        {
            return GetVariablesForFrame(frameToLocals, frameIndex, "Locals", false);
        }

        public DebugScope Expand(int variableRequestVariableId)
        {
            if (idToScope.TryGetValue(variableRequestVariableId, out var existingScope))
            {
                // the variable has already been expanded in the past, and the scope is available already.
                return existingScope;
            } 
            else if (idToTopLevelVariable.TryGetValue(variableRequestVariableId, out var top))
            {
                // this is a top level variable. It could be a struct, or an array, or whatever... 

                var (parentScope, variable) = top;

                var elementCount = variable.GetElementCount(out var arrayRanks);
                if (elementCount > 0)
                {
                    var arrayScope = new DebugScope
                    {
                        id = NextId(),
                        scopeName = "arrayscope",
                        variables = new List<Launch.DebugVariable>()
                    };

                    var nextArrayIndex = variable.arrayRankIndex + 1;
                    
                    
                    for (var i = 0; i < elementCount; i ++)
                    {
                        // TODO: it might be a nested array... 
                       
                        

                        var elementTypeCode = variable.allocation.format.typeCode;
                        
                        // TODO: replace this with the stide in the dataRegisters...
                        var elementRegAddr = variable.regAddr + arrayRanks * 2 - (variable.arrayRankIndex * 2 );
                        var strideLength = (int)_vm.scopeStack.buffer[variable.scopeIndex].dataRegisters[elementRegAddr];
                        _logger.Log($"found stride length=[{strideLength}] at reg=[{elementRegAddr}]");
                        var elementSize = (int)TypeCodes.GetByteSize(elementTypeCode);
                        
                        if (elementTypeCode == TypeCodes.STRUCT)
                        {
                            var internedType = _vm.typeTable[variable.allocation.format.typeId];
                            elementSize = internedType.byteSize;

                            if (nextArrayIndex < arrayRanks)
                            {
                                //nested array
                                var ptr = variable.rawValue + (ulong)(elementSize * i * strideLength);
                                var alloc = variable.allocation;
                                var v = new DebugRuntimeVariable(_vm, $"{i}", elementTypeCode, ptr, ref alloc,
                                    variable.scopeIndex,
                                    variable.regAddr)
                                {
                                    arrayRankIndex = nextArrayIndex
                                };

                                var elementVariable = new Launch.DebugVariable
                                {
                                    id = NextId(),
                                    name = v.name,
                                    type = v.GetTypeName(),
                                    elementCount = v.GetElementCount(),
                                    value = ""
                                };

                                idToTopLevelVariable.Add(elementVariable.id, (parentScope, v));
                                arrayScope.variables.Add(elementVariable);
                            }
                            else
                            {
                                //terminal point of the array!
                                var ptr = variable.rawValue + (ulong)(elementSize * i * strideLength);
                                var alloc = new VmAllocation
                                {
                                    ptr = (int)ptr,
                                    length = elementSize,
                                    format = new HeapTypeFormat
                                    {
                                        typeCode = elementTypeCode,
                                        typeId = internedType.typeId,
                                        // typeFlags = variable.allocation.format.typeFlags
                                    }
                                };
                                
                                var v = new DebugRuntimeVariable(_vm, $"{i}", elementTypeCode, ptr, ref alloc,
                                    variable.scopeIndex,
                                    variable.regAddr)
                                {
                                    arrayRankIndex = nextArrayIndex
                                };
                                
                                var elementVariable = new Launch.DebugVariable
                                {
                                    id = NextId(),
                                    name = v.name,
                                    type = v.GetTypeName(),
                                    fieldCount = internedType.fields.Count,
                                    value = v.GetValueDisplay()
                                };

                                idToTopLevelVariable.Add(elementVariable.id, (parentScope, v));
                                arrayScope.variables.Add(elementVariable);
                            }
                            
                        }
                        else
                        {
                            
                            var subVariable = new Launch.DebugVariable
                            {
                                id = NextId(),
                                name = $"{i}",
                                fieldCount = 0
                            };
                            VmUtil.TryGetVariableTypeDisplay(elementTypeCode, out subVariable.type);

                            if (nextArrayIndex < arrayRanks)
                            {
                                var ogAlloc = variable.allocation;
                                var ptr = variable.rawValue + (ulong)(elementSize * i * strideLength);

                                var v = new DebugRuntimeVariable(_vm, $"{i}", elementTypeCode, ptr, ref ogAlloc, 
                                    variable.scopeIndex,
                                    variable.regAddr)
                                {
                                    arrayRankIndex = nextArrayIndex
                                };
                                subVariable.elementCount = v.GetElementCount();

                                idToTopLevelVariable.Add(subVariable.id, (parentScope, v));
                                subVariable.value = "";
                            }
                            else
                            {
                                _vm.heap.ReadSpan((int)variable.rawValue + elementSize * i, elementSize,
                                    out var fieldSpan);
                                subVariable.value =
                                    VmUtil.ConvertValueToDisplayString(elementTypeCode, _vm, ref fieldSpan);
                            }

                            arrayScope.variables.Add(subVariable);
                        }
                        
                    }
                    
                    idToScope[arrayScope.id] = arrayScope;
                    return arrayScope;
                }
                
                
                switch (variable.typeCode)
                {
                    case TypeCodes.STRUCT:

                        var subScope = new DebugScope
                        {
                            id = NextId(),
                            scopeName = "na",
                            variables = new List<Launch.DebugVariable>()
                        };
                        var structType = variable.GetInternedType();
                        foreach (var fieldKvp in structType.fields)
                        {
                            var fieldName = fieldKvp.Key;
                            var field = fieldKvp.Value;

                            var ptr = variable.rawValue + (ulong)field.offset;
                            var alloc = new VmAllocation
                            {
                                ptr = (int)ptr,
                                length = field.length,
                                format = new HeapTypeFormat
                                {
                                    typeCode = field.typeCode,
                                    typeId = field.typeId
                                }
                            };
                            switch (field.typeCode)
                            {
                                case TypeCodes.STRUCT:

                                    var fieldType = _vm.typeTable[field.typeId];

                                   
                                    
                                    var v = new DebugRuntimeVariable(_vm, fieldName, field.typeCode, ptr, ref alloc, 
                                        variable.scopeIndex,
                                        variable.regAddr);
                                    var fieldVariable = new Launch.DebugVariable
                                    {
                                        id = NextId(),
                                        name = fieldName,
                                        fieldCount = fieldType.fields.Count,
                                        type = v.GetTypeName(),
                                        value = v.GetValueDisplay()
                                    };

                                    idToTopLevelVariable.Add(fieldVariable.id, (parentScope, v));
                                    subScope.variables.Add(fieldVariable);

                                    break;
                                default:

                                    var subVariable = new Launch.DebugVariable
                                    {
                                        id = NextId(),
                                        name = fieldName,
                                        fieldCount = 0,
                                        evalName = variable.name + "." + fieldName
                                    };
                                    
                                    evalNameToId[subVariable.evalName] = subVariable.id;
                                    _vm.heap.ReadSpan((int)variable.rawValue + field.offset, field.length, out var fieldSpan);
                                    VmUtil.TryGetVariableTypeDisplay(field.typeCode, out subVariable.type);
                                    subVariable.value =
                                        VmUtil.ConvertValueToDisplayString(field.typeCode, _vm, ref fieldSpan);
                                    
                                    subScope.variables.Add(subVariable);
                                    
                                    // var v2 = new DebugRuntimeVariable(_vm, fieldName, field.typeCode, variable.rawValue + (ulong)field.offset, ref alloc, 
                                    //     variable.scopeIndex,
                                    //     variable.regAddr);
                                    // idToTopLevelVariable[subVariable.id] = (subScope, v2);
                                    break;
                            }
                            
                        }

                        idToScope[subScope.id] = subScope;
                        return subScope;
                        
                        break;
                    default:
                        throw new InvalidOperationException("cannot expand a non struct variable");
                }

            }
            else
            {
                throw new InvalidOperationException("invalid variable id request given. ");
            }
            
        }

        public DebugEvalResult AddWatchedExpression(DebugRuntimeVariable runtimeSynth, CompiledVariable variable)
        {
            var res = new DebugEvalResult
            {
                type = runtimeSynth.GetTypeName(),
                value = runtimeSynth.GetValueDisplay(),
                fieldCount = runtimeSynth.GetFieldCount(),
                elementCount = runtimeSynth.GetElementCount()
            };
            
            if (res.fieldCount > 0 || res.elementCount > 0)
            {
                res.id = NextId();
                idToTopLevelVariable.Add(res.id, (null, runtimeSynth));
                var scope = Expand(res.id); // TODO: do a recursive expand.
                res.scope = scope;
            }
            return res;
        }

        public DebugEvalResult ResetVariableByEvalname(string evalName)
        {
            // var lex = new Lexer();
            // var lexRes = lex.TokenizeWithErrors(evalName + " = 0");
            // var parser = new Parser(lexRes.stream, new CommandCollection());
            // var prog = parser.ParseProgram(new ParseOptions
            // {
            //     ignoreChecks = true
            // });
            //
            // idto
            if (!evalNameToId.TryGetValue(evalName, out var id))
            {
                throw new NotSupportedException($"no variable exists for the given eval name=[{evalName}]");
            }
            
            if (!idToTopLevelVariable.TryGetValue(id, out var tuple))
            {
                throw new NotSupportedException($"no variable exists from evalName=[{evalName}] with found id=[{id}]");
            }

            // foreach (var kvp in idToTopLevelVariable)
            // {
                // var tuple = kvp.Value;
                // var id = kvp.Key;

                var variable = tuple.Item2;
                // variable.rawValue = variable.vm.scopeStack.buffer[variable.scopeIndex].dataRegisters[variable.regAddr];

                var alloc = variable.allocation;
                var resetVariable = new DebugRuntimeVariable(
                    variable.vm,
                    variable.name,
                    variable.typeCode,
                    variable.vm.scopeStack.buffer[variable.scopeIndex].dataRegisters[variable.regAddr],
                    ref alloc,
                    variable.scopeIndex,
                    variable.regAddr
                );

                idToTopLevelVariable[id] = (tuple.Item1, resetVariable);

                var res = new DebugEvalResult
                {
                    type = resetVariable.GetTypeName(),
                    value = resetVariable.GetValueDisplay(),
                    fieldCount = resetVariable.GetFieldCount(),
                    elementCount = resetVariable.GetElementCount()
                };

                // just re-assign the raw value? 
                if (res.fieldCount > 0 || res.elementCount > 0)
                {
                    res.id = id;
                    // idToTopLevelVariable.Add(res.id, (null, resetVariable));
                    var scope = Expand(id); // TODO: do a recursive expand.
                    res.scope = scope;
                }

            // }

            return res;
        }

        public void ResetValue(int variableId, DebugEvalResult exprResult)
        {
            // the job here is to find the given variable id... 
        }
    }

    public static class DebugUtil
    {
        public static Dictionary<string, DebugRuntimeVariable> LookupVariablesFromScope(VirtualMachine vm, Dictionary<string, DebugRuntimeVariable> results, DebugData dbg, int vmScopeIndex, bool global)
        {
            var scope = vm.scopeStack.buffer[vmScopeIndex];
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

                if (variable.isPtr > 0)
                {
                    if (!vm.heap.TryGetAllocation((int)scope.dataRegisters[scopeIndex], out var allocation))
                    {
                        throw new InvalidOperationException($"Could not get allocation for pointer based variable=[{variable.name}]");
                    }
                    results[variable.name] = new DebugRuntimeVariable(vm, 
                        variable.name, 
                        scope.typeRegisters[scopeIndex],
                        scope.dataRegisters[scopeIndex], ref allocation, vmScopeIndex, scopeIndex);
                }
                else
                {
                    
                    results[variable.name] = new DebugRuntimeVariable(vm, 
                        variable.name, 
                        scope.typeRegisters[scopeIndex],
                        scope.dataRegisters[scopeIndex], vmScopeIndex, scopeIndex);
                }
                

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
                LookupVariablesFromScope(vm, results, dbg, reverseIndex, global);
            }
            
            return results;

        }
    }
}