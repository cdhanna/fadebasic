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
        public readonly ulong regAddr;

        public readonly VmAllocation allocation;
        public readonly VirtualMachine vm;

        public DebugRuntimeVariable(VirtualMachine vm, string name, byte typeCode, ulong rawValue, ref VmAllocation allocation, int scopeIndex, ulong regAddr)
        {
            this.vm = vm;
            this.name = name;
            this.typeCode = typeCode;
            this.rawValue = rawValue;
            this.allocation = allocation;
            this.scopeIndex = scopeIndex;
            this.regAddr = regAddr;
            
        }
        public DebugRuntimeVariable(VirtualMachine vm, string name, byte typeCode, ulong rawValue, int scopeIndex, ulong regAddr)
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
                if (!this.vm.heap.TryGetAllocation(VmPtr.FromRaw(rawValue), out allocation))
                {
                    throw new InvalidOperationException("There is no allocation for the struct reference. hh");
                }
            }

        }

        public string GetValueDisplay()
        {
            switch (typeCode)
            {
                case TypeCodes.INT:
                    return VmUtil.ConvertToInt(rawValue).ToString();
                case TypeCodes.DINT:
                    return VmUtil.ConvertToDInt(rawValue).ToString();
                case TypeCodes.REAL:
                    return VmUtil.ConvertToFloat(rawValue).ToString();
                case TypeCodes.DFLOAT:
                    return VmUtil.ConvertToDFloat(rawValue).ToString();
                case TypeCodes.WORD:
                    return VmUtil.ConvertToWord(rawValue).ToString();
                case TypeCodes.DWORD:
                    return VmUtil.ConvertToDWord(rawValue).ToString();
                case TypeCodes.BYTE:
                    return VmUtil.ConvertToByte(rawValue).ToString();
                case TypeCodes.STRING:
                    var address = VmPtr.FromRaw(rawValue);
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
            ulong elementRegAddr = regAddr + (ulong)rankCount * 2 - ((ulong)arrayRankIndex * 2 ) - 1;
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
        private Dictionary<int, Launch.DebugVariable> idToVariable = new Dictionary<int, Launch.DebugVariable>();

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
        
        // REPL variables: survive ClearLifetime so they persist across steps.
        private readonly List<(string name, byte typeCode, ulong regAddr, int vmScopeIndex)> _replVarDefs
            = new List<(string, byte, ulong, int)>();

        public void AddReplVar(string name, byte typeCode, ulong regAddr, int vmScopeIndex)
        {
            _replVarDefs.RemoveAll(v => v.name == name); // re-declaration replaces the old entry
            _replVarDefs.Add((name, typeCode, regAddr, vmScopeIndex));
        }

        /// <summary>
        /// Evicts the cached local scope for <paramref name="frameIndex"/> so the next
        /// <see cref="GetLocalVariablesForFrame"/> call rebuilds it (picking up newly added REPL vars).
        /// </summary>
        public void InvalidateLocalScope(int frameIndex)
        {
            frameToLocals.Remove(frameIndex);
        }

        public void ClearLifetime()
        {
            _variableIdCounter = 1;
            frameToLocals.Clear();
            frameToGlobals.Clear();
            idToTopLevelVariable.Clear();
            idToScope.Clear();
            evalNameToId.Clear();
            idToVariable.Clear();
            // _replVarDefs is intentionally NOT cleared — REPL variables persist across steps.
        }

        public int NextId()
        {
            return _variableIdCounter++;
        }

        /// <summary>
        /// Given a raw hover word from the editor (which may be truncated — e.g. "y" instead of
        /// "y$", or "e" instead of "c.e"), attempts to find the best matching known eval-name.
        /// Returns <c>true</c> and sets <paramref name="resolved"/> when a unique match is found.
        /// </summary>
        public bool TryResolveHoverExpression(string word, out string resolved)
        {
            resolved = word;

            // 1. Exact match — already correct.
            if (evalNameToId.ContainsKey(word))
                return false;

            // 2. String-variable suffix: try "word$".
            var withDollar = word + "$";
            if (evalNameToId.ContainsKey(withDollar))
            {
                resolved = withDollar;
                return true;
            }

            // 3. Struct-field suffix: search for an eval-name whose last segment equals word.
            //    e.g. word="e" matches "c.e".  Only resolve if there is exactly one candidate.
            var suffix = "." + word;
            string candidate = null;
            var ambiguous = false;
            foreach (var key in evalNameToId.Keys)
            {
                if (key.EndsWith(suffix, System.StringComparison.Ordinal))
                {
                    if (candidate == null)
                        candidate = key;
                    else
                    {
                        ambiguous = true;
                        break;
                    }
                }
            }
            if (candidate != null && !ambiguous)
            {
                resolved = candidate;
                return true;
            }

            return false;
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
                idToVariable[v.id] = v;
                scope.variables.Add(v);
            }

            // Inject REPL-created variables into the local scope of frame 0.
            if (!global && frameIndex == 0 && _replVarDefs.Count > 0)
            {
                foreach (var (name, typeCode, regAddr, vmScopeIndex) in _replVarDefs)
                {
                    if (vmScopeIndex >= _vm.scopeStack.Count) continue; // scope was popped
                    var liveRegs = _vm.scopeStack.buffer[vmScopeIndex].dataRegisters;
                    if ((int)regAddr >= liveRegs.Length) continue; // register not in scope

                    var rawValue = liveRegs[regAddr];
                    DebugRuntimeVariable rtVar;
                    try
                    {
                        if ((typeCode == TypeCodes.STRING || typeCode == TypeCodes.STRUCT) && rawValue == 0)
                        {
                            var emptyAlloc = default(VmAllocation);
                            rtVar = new DebugRuntimeVariable(_vm, name, typeCode, rawValue, ref emptyAlloc, vmScopeIndex, regAddr);
                        }
                        else
                        {
                            rtVar = new DebugRuntimeVariable(_vm, name, typeCode, rawValue, vmScopeIndex, regAddr);
                        }
                    }
                    catch
                    {
                        continue; // skip variables that can't be read safely
                    }

                    var dv = CreateVariableView(scope, rtVar);
                    idToVariable[dv.id] = dv;
                    scope.variables.Add(dv);
                }
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
                        
                        var elementTypeCode = variable.allocation.format.typeCode;
                        
                        // TODO: replace this with the stide in the dataRegisters...
                        var elementRegAddr = variable.regAddr + (ulong)arrayRanks * 2 - ((ulong)variable.arrayRankIndex * 2 );
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
                                var ptr = VmPtr.FromRaw(variable.rawValue) + (elementSize * i * strideLength);
                                var alloc = new VmAllocation
                                {
                                    ptr = ptr,
                                    length = elementSize,
                                    format = new HeapTypeFormat
                                    {
                                        typeCode = elementTypeCode,
                                        typeId = internedType.typeId,
                                        // typeFlags = variable.allocation.format.typeFlags
                                    }
                                };
                                _logger.Debug($"putting sub-array pointer i=[{i}] at ptr=[{alloc.ptr}], raw=[{VmPtr.FromRaw(variable.rawValue)}] offset=[{(ulong)(elementSize * i * strideLength)}]");
                                
                                var v = new DebugRuntimeVariable(_vm, $"{i}", elementTypeCode, VmPtr.GetRaw(ref ptr), ref alloc,
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
                                _vm.heap.ReadSpan(VmPtr.FromRaw(variable.rawValue) + elementSize * i, elementSize,
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

                            var fieldVmPtr = VmPtr.FromRaw(variable.rawValue) + field.offset;
                            var alloc = new VmAllocation
                            {
                                ptr = fieldVmPtr,
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

                                   
                                    
                                    var fieldPtrRaw = VmPtr.GetRaw(ref fieldVmPtr);
                                    var v = new DebugRuntimeVariable(_vm, fieldName, field.typeCode, fieldPtrRaw, ref alloc,
                                        variable.scopeIndex,
                                        variable.regAddr);
                                    var fieldVariable = new Launch.DebugVariable
                                    {
                                        id = NextId(),
                                        name = fieldName,
                                        fieldCount = fieldType.fields.Count,
                                        type = v.GetTypeName(),
                                        value = v.GetValueDisplay(),
                                        runtimeVariable = v
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
                                    var fieldPtr = VmPtr.FromRaw(variable.rawValue) + field.offset;
                                    _vm.heap.ReadSpan(fieldPtr, field.length, out var fieldSpan);
                                    
                                    VmUtil.TryGetVariableTypeDisplay(field.typeCode, out subVariable.type);
                                    subVariable.value =
                                        VmUtil.ConvertValueToDisplayString(field.typeCode, _vm, ref fieldSpan);
                                    
                                    subScope.variables.Add(subVariable);
                                    idToVariable[subVariable.id] = subVariable;
                                    var fieldRaw = VmPtr.GetRaw(ref fieldVmPtr);
                                    var v2 = new DebugRuntimeVariable(_vm, subVariable.evalName, field.typeCode, fieldRaw, ref alloc,
                                        variable.scopeIndex,
                                        variable.regAddr);
                                    subVariable.runtimeVariable = v2;
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
                // Stale ID — VS Code may request expansion of a variable that existed before ClearLifetime().
                // Return an empty scope rather than crashing the debug session.
                _logger.Log($"Expand: unknown variable id={variableRequestVariableId}, returning empty scope");
                return new DebugScope
                {
                    id = NextId(),
                    scopeName = "na",
                    variables = new List<Launch.DebugVariable>()
                };
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

        public bool TryGetTypeCodeForVariableId(int variableId, out byte typeCode)
        {
            typeCode = TypeCodes.ANY;
            if (idToVariable.TryGetValue(variableId, out var dbgVar) && dbgVar.runtimeVariable != null)
            {
                typeCode = dbgVar.runtimeVariable.typeCode;
                return true;
            } else if (idToTopLevelVariable.TryGetValue(variableId, out var tuple))
            {
                typeCode = tuple.Item2.typeCode;
                return true;
            }

            return false;
        }

        public bool TrySetValue(int variableId, int valueId, out string error)
        {
            if (idToTopLevelVariable.TryGetValue(valueId, out var tuple))
            {
                return TrySetValue(variableId, tuple.Item2, out error);
            } else if (idToVariable.TryGetValue(valueId, out var dbgVar) && dbgVar.runtimeVariable != null)
            {
                return TrySetValue(variableId, dbgVar.runtimeVariable, out error);
            }
            else
            {
                throw new NotImplementedException();
            }
        }
        public bool TrySetValue(int variableId, DebugRuntimeVariable value, out string error)
        {
            error = "";
            // the job here is to find the given variable id... 
            if (!idToVariable.TryGetValue(variableId, out var debugVar))
            {
                throw new NotSupportedException("no variable for given id");
            }

            var runtimeVar = debugVar.runtimeVariable;
            var isTop = false;
            
            if (runtimeVar == null && idToTopLevelVariable.TryGetValue(variableId, out var tuple))
            {
                runtimeVar = tuple.Item2;
                isTop = true;
            }

            if (runtimeVar == null)
            {
                throw new NotSupportedException("no runtime variable for given id");
            }

            if (runtimeVar.typeCode != value.typeCode)
            {
                error = "types do not match";
                return false;
            }

            if (isTop && runtimeVar.typeCode != TypeCodes.STRUCT)
            {
                // take the reg value from the new variable, and jam it into the old one. 
                _vm.scopeStack.buffer[runtimeVar.scopeIndex].dataRegisters[runtimeVar.regAddr] = value.rawValue;
            }
            else
            {
                // in this case, we need to copy memory into the heap at the given location...
                var destinationPointer = runtimeVar.allocation.ptr;
                var destinationLength = runtimeVar.allocation.length;
                
                // TODO: the value could be heap or stack...

                byte[] bytes;
                if (value.typeCode == TypeCodes.STRUCT)
                {
                    _vm.heap.Read(value.allocation.ptr, value.allocation.length, out bytes);
                }
                else
                {
                    bytes = BitConverter.GetBytes(value.rawValue);
                }
                
                
                
                _vm.heap.Write(destinationPointer, destinationLength, bytes);
                
            }

            return true;
        }
    }

    public static class DebugUtil
    {
        public static Dictionary<string, DebugRuntimeVariable> LookupVariablesFromScope(VirtualMachine vm, Dictionary<string, DebugRuntimeVariable> results, DebugData dbg, int vmScopeIndex, bool global)
        {
            var scope = vm.scopeStack.buffer[vmScopeIndex];
            for (ulong scopeIndex = 0; scopeIndex < (ulong)scope.dataRegisters.LongLength; scopeIndex++)
            {
                var insIndex = scope.insIndexes[scopeIndex];
                if (!dbg.insToVariable.TryGetValue(insIndex, out var variable))
                {
                    // TODO: feels like there should be a better way than searching the entire space. 
                    continue;
                }


                var isVariableGlobal = VirtualScope.IsGlobal(scope.flags[scopeIndex]);
                if (global && !isVariableGlobal) continue; 
                if (!global && isVariableGlobal) continue;

                if (variable.isPtr > 0)
                {
                    if (!vm.heap.TryGetAllocation(VmPtr.FromRaw(scope.dataRegisters[scopeIndex]), out var allocation))
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