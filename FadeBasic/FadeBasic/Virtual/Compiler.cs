using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FadeBasic.Ast;
using FadeBasic.Json;

namespace FadeBasic.Virtual
{
    public class CompiledVariable : IJsonable, IJsonableSerializationCallbacks
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public string structType;
        public ulong registerAddress;

        private string registerAddressSerializer;
        public bool isGlobal;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(structType), ref structType);
            op.IncludeField(nameof(registerAddressSerializer), ref registerAddressSerializer);
            op.IncludeField(nameof(isGlobal), ref isGlobal);
        }

        public void OnAfterDeserialized()
        {
            registerAddress = ulong.Parse(registerAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            registerAddressSerializer = registerAddress.ToString();
        }
    }

    public class CompiledArrayVariable : IJsonable, IJsonableSerializationCallbacks
    {
        public int byteSize;
        public byte typeCode;
        public string name;
        public CompiledType structType;
        public ulong registerAddress;

        private string registerAddressSerializer;
        
        public bool isGlobal;
        public byte[] rankSizeRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the size of the rank
        public byte[] rankIndexScalerRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the multiplier factor for the rank's indexing
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(structType), ref structType);
            op.IncludeField(nameof(registerAddressSerializer), ref registerAddressSerializer);
            op.IncludeField(nameof(isGlobal), ref isGlobal);
            op.IncludeField(nameof(rankSizeRegisterAddresses), ref rankSizeRegisterAddresses);
            op.IncludeField(nameof(rankIndexScalerRegisterAddresses), ref rankIndexScalerRegisterAddresses);
        }

        public void OnAfterDeserialized()
        {
            registerAddress = ulong.Parse(registerAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            registerAddressSerializer = registerAddress.ToString();
        }
    }

    public class CompiledType : IJsonable
    {
        public string typeName;
        public int typeId;
        public int byteSize;
        public Dictionary<string, CompiledTypeMember> fields = new Dictionary<string, CompiledTypeMember>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(typeName), ref typeName);
            op.IncludeField(nameof(typeId), ref typeId);
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(fields), ref fields);
        }
    }

    public struct CompiledTypeMember : IJsonable
    {
        public int Offset, Length;
        public byte TypeCode;
        public CompiledType Type;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(Offset), ref Offset);
            op.IncludeField(nameof(Length), ref Length);
            op.IncludeField(nameof(TypeCode), ref TypeCode);
            op.IncludeField(nameof(Type), ref Type);
        }
    }

    public struct LabelReplacement
    {
        public int InstructionIndex;
        public string Label;
    }

    public struct FunctionCallReplacement
    {
        public int InstructionIndex;
        public string FunctionName;
    }

    public class CompileScope
    {
        public ulong registerCount;
        
        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
            new Dictionary<string, CompiledArrayVariable>();

        public CompileScope()
        {
            
        }

        public CompileScope(Dictionary<string, CompiledVariable> varToReg, Dictionary<string, CompiledArrayVariable> arrayVarToReg)
        {
            _varToReg = varToReg;
            _arrayVarToReg = arrayVarToReg;
            ulong highestAddress = 0;
            foreach (var kvp in _varToReg)
            {
                if (kvp.Value.registerAddress > highestAddress)
                {
                    highestAddress = kvp.Value.registerAddress;
                }
            }
            foreach (var kvp in _arrayVarToReg)
            {
                if (kvp.Value.registerAddress > highestAddress)
                {
                    highestAddress = kvp.Value.registerAddress;
                }

                foreach (var n in kvp.Value.rankSizeRegisterAddresses)
                {
                    if (n > highestAddress) highestAddress = n;
                }
                foreach (var n in kvp.Value.rankIndexScalerRegisterAddresses)
                {
                    if (n > highestAddress) highestAddress = n;
                }
            }

            registerCount = highestAddress + 1;
        }
        
        public bool TryGetVariable(string name, out CompiledVariable variable)
        {
            return _varToReg.TryGetValue(name, out variable);
        }

        public bool TryGetArray(string name, out CompiledArrayVariable arrayVariable)
        {
            return _arrayVarToReg.TryGetValue(name, out arrayVariable);
        }
        
        public CompiledVariable Create(string name, byte typeCode, bool isGlobal, byte regOffset=0)
        {
            var compileVar = new CompiledVariable
            {
                registerAddress = (regOffset + registerCount++),
                name = name,
                typeCode = typeCode,
                byteSize = TypeCodes.GetByteSize(typeCode),
                isGlobal = isGlobal
            };  

            _varToReg[name] = compileVar;
            
            return compileVar;
        }

        public CompiledArrayVariable CreateArray(string declarationVariable, int rankLength, byte typeCode, bool isGlobal)
        {
            var compileArrayVar = new CompiledArrayVariable()
            {
                registerAddress = (byte)(registerCount++),
                rankSizeRegisterAddresses = new byte[rankLength],
                rankIndexScalerRegisterAddresses = new byte[rankLength],
                name = declarationVariable,
                typeCode = typeCode,
                byteSize = TypeCodes.GetByteSize(typeCode),
                isGlobal = isGlobal
            };
            _arrayVarToReg[declarationVariable] = compileArrayVar;
            return compileArrayVar;
        }

        public byte AllocateRegister()
        {
            return (byte)(registerCount++);
        }
    }

    public class CompilerOptions
    {
        public bool GenerateDebugData = false;
        public bool InternStrings = true;

        public static readonly CompilerOptions Default = new CompilerOptions
        {
            GenerateDebugData = false,
            InternStrings = true
        };
    }

    public class InternedScopeMetadata : IJsonableSerializationCallbacks
    {
        public int scopeIndex;
        public ulong maxRegisterSize;
        private string maxRegisterSizeSerializer;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(scopeIndex), ref scopeIndex);
            op.IncludeField(nameof(maxRegisterSizeSerializer), ref maxRegisterSizeSerializer);
        }

        public void OnAfterDeserialized()
        {
            maxRegisterSize = ulong.Parse(maxRegisterSizeSerializer);
        }

        public void OnBeforeSerialize()
        {
            maxRegisterSizeSerializer = maxRegisterSize.ToString();
        }
    }
    
    public class InternedData : IJsonable, IJsonableSerializationCallbacks
    {
        public Dictionary<string, InternedType> types;
        public Dictionary<string, InternedFunction> functions = new Dictionary<string, InternedFunction>();
        public List<InternedString> strings = new List<InternedString>();
        // public Dictionary<int, InternedScopeMetadata> scopeMetaDatas = new Dictionary<int, InternedScopeMetadata>();
        public ulong maxRegisterAddress;
        private string maxRegisterAddressSerializer;
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(types), ref types);
            op.IncludeField(nameof(functions), ref functions);
            op.IncludeField(nameof(strings), ref strings);
            op.IncludeField(nameof(maxRegisterAddressSerializer), ref maxRegisterAddressSerializer);
        }

        public void OnAfterDeserialized()
        {
            maxRegisterAddress = ulong.Parse(maxRegisterAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            maxRegisterAddressSerializer = maxRegisterAddress.ToString();
        }
    }
    
    public class InternedString : IJsonable
    {
        public string value;
        public int[] indexReferences;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(value), ref value);
            op.IncludeField(nameof(indexReferences), ref indexReferences);
        }
    }

    public class InternedFunction : IJsonable
    {
        public string name;
        public int insIndex;
        
        public int typeCode;
        public int typeId;
        public List<InternedFunctionParameter> parameters = new List<InternedFunctionParameter>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(insIndex), ref insIndex);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeId), ref typeId);
     
            op.IncludeField(nameof(parameters), ref parameters);
        }
    }

    public class InternedFunctionParameter : IJsonable
    {
        public string name;
        public int index;
        public int typeCode;
        public int typeId;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(index), ref index);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeId), ref typeId);
        }
    }

    public class InternedType : IJsonable
    {
        public string name;
        public int byteSize;
        public int typeId;
        public Dictionary<string, InternedField> fields = new Dictionary<string, InternedField>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(typeId), ref typeId);
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(fields), ref fields);
        }
    }

    public class InternedField : IJsonable
    {
        public int offset, length;
        public byte typeCode;
        public string typeName;
        public int typeId;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(offset), ref offset);
            op.IncludeField(nameof(length), ref length);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeName), ref typeName);
            op.IncludeField(nameof(typeId), ref typeId);
        }
    }
    
    
    public class Compiler
    {
        // public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();
        private DebugData _dbg;
        public DebugData DebugData => _dbg;
        public List<byte> Program => _buffer;
        // public int registerCount;

        public CompileScope globalScope;
        public Stack<CompileScope> scopeStack;

        private CompileScope scope => scopeStack.Peek();
        
        private Dictionary<string, CompiledType> _types = new Dictionary<string, CompiledType>();
        public Dictionary<int, CompiledType> _typeTable = new Dictionary<int, CompiledType>();

        public HostMethodTable methodTable;

        private Dictionary<string, int> _commandToPtr = new Dictionary<string, int>();
        private Stack<List<int>> _exitInstructionIndexes = new Stack<List<int>>();
        private Stack<List<int>> _skipInstructionIndexes = new Stack<List<int>>();

        private List<LabelReplacement> _labelReplacements = new List<LabelReplacement>();
        private Dictionary<string, int> _labelToInstructionIndex = new Dictionary<string, int>();

        private List<FunctionCallReplacement> _functionCallReplacements = new List<FunctionCallReplacement>();
        private Dictionary<string, int> _functionTable = new Dictionary<string, int>();
        
        private InternedData data = new InternedData();
        
        public Dictionary<string, HashSet<int>> stringToCallingInstructionIndexes =
            new Dictionary<string, HashSet<int>>();

        private CompilerOptions _options;


        public Compiler(CommandCollection commands, CompilerOptions options=null, CompileScope givenGlobalScope=null)
        {
            _options = options;
            _options ??= CompilerOptions.Default;
            if (_options.GenerateDebugData)
            {
                _dbg = new DebugData();
            }

            var methods = new CommandInfo[commands.Commands.Count];
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                methods[i] = commands.Commands[i];
                _commandToPtr[commands.Commands[i].UniqueName] = i;
            }
            methodTable = new HostMethodTable
            {
                methods = methods
            };

            scopeStack = new Stack<CompileScope>();
            globalScope = givenGlobalScope ?? new CompileScope();
            scopeStack.Push(globalScope);
        }

        public void AddType(CompiledType type)
        {
            _types.Add(type.typeName, type);
            _typeTable.Add(type.typeId, type);
        }

        public void AddFunction(string functionName, int insIndex)
        {
            _functionTable.Add(functionName, insIndex);
        }
        

        public void Compile(ProgramNode program)
        {

            // push a temporary value that will be replaced later.
            //  this value represents the ins-ptr where the interned-data lives.
            var value = BitConverter.GetBytes(0);
            for (var i = 0 ; i < value.Length; i ++)
            {
                _buffer.Add(value[i]);
            }
            
            foreach (var typeDef in program.typeDefinitions)
            {
                Compile(typeDef);
            }
            
            foreach (var statement in program.statements)
            {
            
                Compile(statement);
            }

            // prevent the execution from ever going to the functions. GOTO statements _should_ be illegal to jump into a function's scope. 
            CompileEnd();
            
            foreach (var function in program.functions)
            {
                Compile(function);
            }

            { // handle interned data
                { // replace the jump ptr at index=0 to tell us where the data lives. 
                    var internLocationBytes = BitConverter.GetBytes(_buffer.Count);
                    for (var i = 0; i < internLocationBytes.Length; i++)
                    {
                        _buffer[0 + i] = internLocationBytes[i];
                    }
                }

                PushInternedData();
            }
            CompileJumpReplacements();
        }

        public void CompileJumpReplacements()
        {
            
            // replace all label instructions...
            foreach (var replacement in _labelReplacements)
            {
                // TODO: look up in labelTable
                if (!_labelToInstructionIndex.TryGetValue(replacement.Label, out var location))
                {
                    throw new Exception("Compiler: unknown label location " + replacement.Label);
                }

                var locationBytes = BitConverter.GetBytes(location);
                for (var i = 0; i < locationBytes.Length; i++)
                {
                    // offset by 2, because of the opcode, and the type code
                    _buffer[replacement.InstructionIndex + 2 + i] = locationBytes[i];
                }
            }
            
            // replace all function instrunctions
            foreach (var replacement in _functionCallReplacements)
            {
                if (!_functionTable.TryGetValue(replacement.FunctionName, out var location))
                {
                    throw new Exception("Compiler: unknown function location " + replacement.FunctionName);
                }

                var locationBytes = BitConverter.GetBytes(location);
                for (var i = 0; i < locationBytes.Length; i++)
                {
                    _buffer[replacement.InstructionIndex + 2 + i] = locationBytes[i];
                }
            }
        }

        public void PushInternedData()
        {

            { // handle the strings
                data.strings = new List<InternedString>();
                foreach (var kvp in stringToCallingInstructionIndexes)
                {
                    var internedString = new InternedString
                    {
                        value = kvp.Key,
                        indexReferences = kvp.Value.ToArray()
                    };
                    data.strings.Add(internedString);
                }
            }
            
            // the type table will be the JSONified
            data.types = new Dictionary<string, InternedType>();
            foreach (var kvp in _types)
            {
                var type = new InternedType
                {
                    typeId = kvp.Value.typeId,
                    name = kvp.Key,
                    byteSize = kvp.Value.byteSize,
                };

                foreach (var fieldKvp in kvp.Value.fields)
                {
                    var field = new InternedField
                    {
                        length = fieldKvp.Value.Length,
                        offset = fieldKvp.Value.Offset,
                        typeCode = fieldKvp.Value.TypeCode,
                        typeName = fieldKvp.Value.Type?.typeName,
                        typeId = fieldKvp.Value.Type?.typeId ?? 0
                    };
                    type.fields.Add(fieldKvp.Key, field);
                }
                
                data.types.Add(type.name, type);
            }

            data.maxRegisterAddress = scopeStack.Peek().registerCount;
            data.maxRegisterAddress += 1; // an extra 1 for debugging room.
            var json = data.Jsonify();
            var jsonBytes = Encoding.Default.GetBytes(json);
            _buffer.AddRange(jsonBytes);
        }

        public void Compile(TypeDefinitionStatement typeDefinition)
        {
            /*
             * compile all the type definitions first!
             * for each type, we need to pre-compute the offset _per_ field
             * and we need to calculate the total size for the struct
             */
            var typeName = typeDefinition.name.variableName;
            var type = new CompiledType
            {
                typeId = _typeTable.Count + 1, // include the +1 to imply that a typeId of 0 is invalid (if you see 0, its implies a bug happened)
                typeName = typeName
            };
            
            int totalSize = 0;
            foreach (var decl in typeDefinition.declarations)
            {
                var fieldOffset = totalSize;
                var typeMember = new CompiledTypeMember
                {
                    Offset = fieldOffset,
                };

                int size = 0;
                switch (decl.type)
                {
                    case TypeReferenceNode typeRef:
                        var tc = VmUtil.GetTypeCode(typeRef.variableType);
                        size = TypeCodes.GetByteSize(tc);
                        typeMember.Length = size;
                        totalSize += size;
                        typeMember.TypeCode = tc;
                        break;
                    case StructTypeReferenceNode structTypeRef:
                        if (!_types.TryGetValue(structTypeRef.variableNode.variableName, out var structType))
                        {
                            throw new Exception("Referencing type that does not exist yet. " + structTypeRef.variableNode);
                        }

                        size = structType.byteSize;
                        typeMember.Length = size;
                        totalSize += size;
                        typeMember.TypeCode = TypeCodes.STRUCT;
                        typeMember.Type = structType;
                        break;
                }
                
                type.fields.Add(decl.name.variableName, typeMember);
            }

            type.byteSize = totalSize;
            
            _types[typeName] = type;
            _typeTable[type.typeId] = type;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddDebugToken(Token token, int insIndex=-1)
        {
            if (_dbg == null) return; // no-op if we are not generating debugger info.
            if (insIndex < 0) 
                insIndex = _buffer.Count;
            _dbg.AddStatementDebugToken(insIndex, token);
        }

        
        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // void AddStartDebugToken(Token token)
        // {
        //     if (_dbg == null) return; // no-op if we are not generating debugger info.
        //     _dbg.AddStartToken(_buffer.Count, token); // this happens BEFORE the byte code is emitted. 
        // }
        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // void AddStopDebugToken(Token token)
        // {
        //     if (_dbg == null) return; // no-op if we are not generating debugger info.
        //     _dbg.AddStopToken(_buffer.Count - 1, token); // this happens AFTER the byte code is emitted. 
        // }

        public void Compile(IStatementNode statement)
        {
            /*
             * every statement can have a breakpoint.
             *  You can step OVER a statement, which we should capture at the end of this function.
             *  That isn't quite right, because if you step over an IF statement, you don't jump over the entire branch...
             */
            switch (statement)
            {
                case CommentStatement _:
                    break;
                default:
                    AddDebugToken(statement.StartToken);
                    break;
            }
            
            
            switch (statement)
            {
                case CommentStatement _:
                    // ignore comments
                    break;
                case DeclarationStatement declarationStatement:
                    Compile(declarationStatement);
                    break;
                case RedimStatement redimStatement:
                    Compile(redimStatement);
                    break;
                case AssignmentStatement assignmentStatement:
                    Compile(assignmentStatement);
                    break;
                case CommandStatement commandStatement:
                    Compile(commandStatement);
                    break;
                case LabelDeclarationNode labelStatement:
                    Compile(labelStatement);
                    break;
                case GotoStatement gotoStatement:
                    Compile(gotoStatement);
                    break;
                case GoSubStatement goSubStatement:
                    Compile(goSubStatement);
                    break;
                case ReturnStatement returnStatement:
                    Compile(returnStatement);
                    break;
                case EndProgramStatement endProgramStatement:
                    Compile(endProgramStatement);
                    break;
                case IfStatement ifStatement:
                    Compile(ifStatement);
                    break;
                case ExitLoopStatement exitStatement:
                    Compile(exitStatement);
                    break;
                case SkipLoopStatement skipStatement:
                    Compile(skipStatement);
                    break;
                case WhileStatement whileStatement:
                    Compile(whileStatement);
                    break;
                case RepeatUntilStatement repeatUntilStatement:
                    Compile(repeatUntilStatement);
                    break;
                case ForStatement forStatement:
                    Compile(forStatement);
                    break;
                case DoLoopStatement doLoopStatement:
                    Compile(doLoopStatement);
                    break;
                case SwitchStatement switchStatement:
                    Compile(switchStatement);
                    break;
                case FunctionStatement _:
                    // functions should be compiled at the end... ignoring for now.
                    break;
                case FunctionReturnStatement returnStatement:
                    Compile(returnStatement);
                    break;
                case ExpressionStatement expressionStatement:
                    Compile(expressionStatement);
                    break;
                    
                default:
                    throw new Exception("compiler exception: unhandled statement node " + statement);
            }
        }

        void CompileAsInvocation(ArrayIndexReference expr)
        {
            // need to push values onto stack
            foreach (var argExpr in expr.rankExpressions)
            {
                Compile(argExpr);
            }
            
            _functionCallReplacements.Add(new FunctionCallReplacement()
            {
                InstructionIndex = _buffer.Count,
                FunctionName = expr.variableName
            });
            AddPushInt(_buffer, int.MaxValue); // temp ptr value, will be replaced by function location later.
            _buffer.Add(OpCodes.JUMP_HISTORY);
        }

        
        private void Compile(FunctionReturnStatement returnStatement)
        {
            // put the return value onto the stack
            if (returnStatement.returnExpression != null)
            {
                Compile(returnStatement.returnExpression);
            }

            // pop a scope
            _buffer.Add(OpCodes.POP_SCOPE);
            
            // and then jump home
            _buffer.Add(OpCodes.RETURN);
        }

        private void Compile(FunctionStatement functionStatement)
        {
            // well, first, if we come across one of these, we should throw an exception...
            _buffer.Add(OpCodes.EXPLODE); // TODO: add a jump-over-function feature
            
            // functions are global
            var ptr = _buffer.Count;
            _functionTable[functionStatement.name] = ptr; // TODO: what about duplicate function names?

            var internedFunction = new InternedFunction
            {
                name = functionStatement.name,
                insIndex = ptr,
            };
            if (functionStatement.hasNoReturnExpression || functionStatement.ParsedType.type == VariableType.Void)
            {
                internedFunction.typeId = -1;
            }
            else
            {
                var tc = VmUtil.GetTypeCode(functionStatement.ParsedType.type);
                internedFunction.typeCode = tc;
                if (tc == TypeCodes.STRUCT)
                {
                    internedFunction.typeId = _types[functionStatement.ParsedType.structName].typeId;
                }
            }
            
            data.functions.Add(functionStatement.name, internedFunction);
            
            // at the insIndex, take note of the name for the debug data. Later, the index that has the 
            _dbg?.AddFunction(ptr, functionStatement.nameToken);

            // push a new scope
            _buffer.Add(OpCodes.PUSH_SCOPE);
            
            // now, we need to pull values off the stack and put them into variable declarations...
            // foreach (var arg in functionStatement.parameters)
            for (var i = functionStatement.parameters.Count - 1; i >= 0; i --) // read in reverse order due to stack
            {
                var arg = functionStatement.parameters[i];
                
                // compile up a fake declaration for the input
                var fakeDecl = new DeclarationStatement
                {
                    variable = arg.variable.variableName,
                    scopeType = DeclarationScopeType.Local,
                    type = arg.type
                };
                var parameterTc = VmUtil.GetTypeCode(arg.type.variableType);
                
                var internedParameter = new InternedFunctionParameter
                {
                    name = fakeDecl.variable,
                    index = i,
                    typeCode = parameterTc
                };
                if (fakeDecl.type is StructTypeReferenceNode structType)
                {
                    internedParameter.typeId = _types[structType.variableNode.variableName].typeId;
                }
                
                internedFunction.parameters.Add(internedParameter);
                Compile(fakeDecl);
                
                // and now compile up the assignment
                _buffer.Add(OpCodes.CAST);

                var tc = VmUtil.GetTypeCode(arg.type.variableType);
                _buffer.Add(tc);
                CompileAssignmentLeftHandSide(arg.variable);

            }
            
           
            // compile all the statements...
            foreach (var statement in functionStatement.statements)
            {
                Compile(statement);
            }
            
            // at the end of the function, we need to jump home
            // pop a scope
            _buffer.Add(OpCodes.POP_SCOPE);
            
            // and then jump home
            _buffer.Add(OpCodes.RETURN);
            
        }
        
        private void Compile(ExitLoopStatement exitLoopStatement)
        {
            // immediately jump to the exit...
            _exitInstructionIndexes.Peek().Add(_buffer.Count);

            // and then jump to the exit block
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(SkipLoopStatement skipLoopStatement)
        {
            // immediately jump to the start of the loop...
            _skipInstructionIndexes.Peek().Add(_buffer.Count);

            // and then jump to the beginning of the loop (this will be replaced later)
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }

        private void Compile(SwitchStatement switchStatement)
        {
            // first, compile the switch expression
            Compile(switchStatement.expression);

            // then, push the address of the default case (fill it in later)
            var defaultInsIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, push the switch-value and address for each case
            var pairInsIndexes = new List<int>[switchStatement.cases.Count];
            var caseCount = 0;
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                var caseStatement = switchStatement.cases[i];

                pairInsIndexes[i] = new List<int>();
                foreach (var literal in caseStatement.values)
                {
                    caseCount++; // keep track of how many ACTUAL cases there are
                    pairInsIndexes[i].Add(_buffer.Count); // later, we'll update the pushed int to be the address of the case
                    AddPushInt(_buffer, int.MaxValue);
                    
                    // compile the switch-value (this must be a literal, enforced by the parser)
                    Compile(literal);
                }
            }
            
            // push the total number of cases
            AddPushInt(_buffer, caseCount);
            
            // then, actually put the jump table.... It will jump to the right case, or default!
            _buffer.Add(OpCodes.JUMP_TABLE);
            
            // keep track of the actual address values for each case statement
            var caseAddrValues = new int[switchStatement.cases.Count];
            
            // keep track of the instruction indexes that point to exit-address, that need to be patched later
            var exitInsIndexes = new int[switchStatement.cases.Count];

            // compile each case block
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                // the start of this case statement.
                var caseStatement = switchStatement.cases[i];
                caseAddrValues[i] = _buffer.Count; 
                
                // compile the actual statements...
                foreach (var statement in caseStatement.statements)
                {
                    Compile(statement);
                }
                
                // now that we are done with the case, jump to the end. (no "fall-through")
                exitInsIndexes[i] = _buffer.Count;
                AddPushInt(_buffer, int.MaxValue); // later, this will get changed to be the exit address
                _buffer.Add(OpCodes.JUMP);
            }
            
            // compile the default case
            var defaultAddr = _buffer.Count; // this is where the default case lives
            if (switchStatement.defaultCase != null)
            {
                foreach (var statement in switchStatement.defaultCase.statements)
                {
                    Compile(statement);
                }
            }

            // now at the end of the default block, we are done! so this is the exit address.
            var exitAddr = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);

            // now do all the address replacements....
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                var indexes = pairInsIndexes[i];
                var caseAddr = caseAddrValues[i];
                var caseAddrBytes = BitConverter.GetBytes(caseAddr);
                foreach (var index in indexes)
                {
                    for (var j = 0; j < caseAddrBytes.Length; j++)
                    {
                        _buffer[index + 2 + j] = caseAddrBytes[j];
                    }
                }
            }
            
            // replace the default address at the start of the function
            var defaultAddrBytes = BitConverter.GetBytes(defaultAddr);
            for (var i = 0; i < defaultAddrBytes.Length; i++)
            {
                _buffer[defaultInsIndex + 2 + i] = defaultAddrBytes[i];
            }

            // replace all the individual case statement's references to the jump exit
            var exitAddrBytes = BitConverter.GetBytes(exitAddr);
            foreach (var exitIns in exitInsIndexes)
            {
                for (var i = 0; i < exitAddrBytes.Length; i++)
                {
                    _buffer[exitIns + 2 + i] = exitAddrBytes[i];
                }
            }
        }
        
        private void Compile(ForStatement forStatement)
        {
            // for later, we'll need a statement that adds the step expr
            var stepAssignment = new AssignmentStatement
            {
                expression = new BinaryOperandExpression
                {
                    operationType = OperationType.Add,
                    lhs = forStatement.variableNode,
                    rhs = forStatement.stepValueExpression
                },
                variable = forStatement.variableNode
            };
            
            // first, set the iterator variable to the start value
            var fakeAssignment = new AssignmentStatement
            {
                expression = forStatement.startValueExpression,
                variable = forStatement.variableNode
            };
            Compile(fakeAssignment);

            // then, keep track of the start of the for-loop, this is where we'll come back to
            var forLoopValue = _buffer.Count;
            
            // push min
            Compile(forStatement.startValueExpression); // TODO: we are accessing the start twice- that may mean a function gets called twice
            
            // push max
            Compile(forStatement.endValueExpression);

            // we don't actually know if the min is min, and the max is max; so use an op code to sort the previous two stack entries
            _buffer.Add(OpCodes.MIN_MAX_PUSH);
            
            // push x again
            Compile(forStatement.variableNode);
            
            // is x less than max?
            _buffer.Add(OpCodes.LTE);
            
            // if this is a zero, then we have failed, and we can exit...
            // push the address we want to go if failed
            var lteExitJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            // and maybe jump
            _buffer.Add(OpCodes.JUMP_ZERO);
            
            // push x again
            Compile(forStatement.variableNode);
            
            // is x greater than min?
            _buffer.Add(OpCodes.GTE);
            
            // then, put a fake value in for the for-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load exit the for-loop.
            // Just take note of this buffer index, and we'll update it later
            var exitJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(new List<int>());
            _skipInstructionIndexes.Push(new List<int>());
            foreach (var successStatement in forStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();
            
            // This is the location where Step updates and evaluation happens 
            // (important as skip should jump here, not to the very start)
            var stepLoopValue = _buffer.Count;
            
            // now to update the value of x, we need to add the stepExpr to it.
            Compile(stepAssignment); // NOTE: there could be a bug here, because we are looping on a deterministic math operation, but simulating the interpolated variable
            
            // jump back to the start
            AddPushInt(_buffer, forLoopValue);
            _buffer.Add(OpCodes.JUMP);
            
            var endJumpValue = _buffer.Count;
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            var stepLoopBytes = BitConverter.GetBytes(stepLoopValue);
            
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                _buffer[successJumpIndex + 2 + i] = successJumpBytes[i];
                _buffer[exitJumpIndex + 2 + i] = endJumpBytes[i];
                _buffer[lteExitJumpIndex + 2 + i] = endJumpBytes[i];
                
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[i];
                }
                
                // Update skip instructions to jump to the increment/evaluation part
                foreach (var index in skipStatementIndexes)
                {
                    _buffer[index + 2 + i] = stepLoopBytes[i];
                }
            }
        }
         
        
        private void Compile(DoLoopStatement doLoopStatement)
        {
            // first, keep track of the start of the while loop
            var whileLoopValue = _buffer.Count;
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(new List<int>());
            _skipInstructionIndexes.Push(new List<int>());
            foreach (var successStatement in doLoopStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();

            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            var whileLoopBytes = BitConverter.GetBytes(whileLoopValue);
            
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[i];
                }
                
                // Update skip instructions to jump back to the beginning of the loop
                foreach (var index in skipStatementIndexes)
                {
                    _buffer[index + 2 + i] = whileLoopBytes[i];
                }
            }
        }
        
        
        private void Compile(RepeatUntilStatement repeatStatement)
        {
            // first, keep track of the start of the while loop
            var startValue = _buffer.Count;
            
            _exitInstructionIndexes.Push(new List<int>());
            _skipInstructionIndexes.Push(new List<int>());
            
            foreach (var successStatement in repeatStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();
            
            // keep track of where the skip should go
            var skipJumpValue = _buffer.Count;
            
            // compile the condition expression
            Compile(repeatStatement.condition);
            // cast the expression to an int
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            
            // the semantics of the word, "until", mean we flip the condition value
            _buffer.Add(OpCodes.NOT);
            
            // then, insert the starting address
            AddPushInt(_buffer, startValue);
            
            // then, maybe jump to the start?
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we are done!
           
            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the jump addresses
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            var skipValueBytes = BitConverter.GetBytes(skipJumpValue);
            
            for (var i = 0; i < endJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[i];
                }
                
                // Update skip instructions to jump back to the beginning of the loop
                foreach (var index in skipStatementIndexes)
                {
                    _buffer[index + 2 + i] = skipValueBytes[i];
                }
            }
        }
        
        
        private void Compile(WhileStatement whileStatement)
        {
            /*
             * Loop:
             * <condition>
             * PUSH addr of success
             * JUMP_GT_ZERO
             * PUSH addr of exit
             * JUMP
             * Success:
             *  positive-statements
             *  JUMP Loop:
             * Exit:
             */
            
            // first, keep track of the start of the while loop
            var whileLoopValue = _buffer.Count;
            
            // compile the condition expression
            Compile(whileStatement.condition);

            // cast the expression to an int
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            
            // then, put a fake value in for the while-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load exit the while loop
    
            var exitJumpIndex = _buffer.Count;
            // and then jump to the exit block
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(new List<int>());
            _skipInstructionIndexes.Push(new List<int>());
            foreach (var successStatement in whileStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();
            
            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            var whileLoopBytes = BitConverter.GetBytes(whileLoopValue);
            
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                _buffer[successJumpIndex + 2 + i] = successJumpBytes[i];
                _buffer[exitJumpIndex + 2 + i] = endJumpBytes[i];
                
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[i];
                }
                
                // Update skip instructions to jump back to the beginning of the loop
                foreach (var index in skipStatementIndexes)
                {
                    _buffer[index + 2 + i] = whileLoopBytes[i];
                }
            }
        }
        
        private void Compile(IfStatement ifStatement)
        {
            /*
             * <condition value>
             * PUSH addr of Success:
             * JUMP_GT_ZERO
             * PUSH addr of Else
             * JUMP
             * Success:
             *  positive-if-statements
             *  JUMP Final:
             * Else:
             *  else-if-statements
             * Final:
             */
            
            // first, compile the evaluation of the condition
            Compile(ifStatement.condition);
            
            // cast the expression to an int
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            
            // then, put a fake value in for the if-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load up the ELSE block
            var elseJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);

            // and then jump to the else block
            _buffer.Add(OpCodes.JUMP);

            // now it is time to start compiling the actual statements...
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;

            foreach (var successStatement in ifStatement.positiveStatements)
            {
                Compile(successStatement);
            }

            _dbg?.AddFakeDebugToken(_buffer.Count - 1, ifStatement.endToken);
            
            // at the end of the successful statements, we need to jump to the end
            var endJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);


            // this is where the else statements begin
            var elseJumpValue = _buffer.Count;
            // _buffer.Add(OpCodes.NOOP);
            foreach (var elseStatement in ifStatement.negativeStatements)
            {
                Compile(elseStatement);
            }

            // _dbg?.AddFakeDebugToken(_buffer.Count - 1, ifStatement.endToken);

            var endJumpValue = _buffer.Count;
            // _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var elseJumpBytes = BitConverter.GetBytes(elseJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                //(successJumpBytes.Length -1) - i
                _buffer[successJumpIndex + 2 + i] = successJumpBytes[i];
                _buffer[elseJumpIndex + 2 + i] = elseJumpBytes[i];
                _buffer[endJumpIndex + 2 + i] = endJumpBytes[i];
            }
        }
        
        private void Compile(LabelDeclarationNode labelStatement)
        {
            // take note of instruction number... 
            _labelToInstructionIndex[labelStatement.label] = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
        }

        private void Compile(ReturnStatement returnStatement)
        {
            _buffer.Add(OpCodes.RETURN);
        }

        private void Compile(EndProgramStatement endProgramStatement)
        {
            CompileEnd();
        }

        private void CompileEnd()
        {
            // jump to the end of the instruction pointer space, a hack?
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(GoSubStatement goSubStatement)
        {

            _labelReplacements.Add(new LabelReplacement
            {
                InstructionIndex = _buffer.Count,
                Label = goSubStatement.label
            });
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP_HISTORY);
            
        }
        
        private void Compile(GotoStatement gotoStatement)
        {
            // identify the instruction ID of the label
            _labelReplacements.Add(new LabelReplacement
            {
                InstructionIndex = _buffer.Count,
                Label = gotoStatement.label
            });
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(AddressExpression expression)
        {
            // we need to find the address of the given expression... 
            switch (expression.variableNode)
            {
                case VariableRefNode refNode:

                    // this is a register address...
                    if (!scope.TryGetVariable(refNode.variableName, out var variable))
                    {
                        var fakeDeclStatement = new DeclarationStatement
                        {
                            startToken = expression.startToken,
                            endToken = expression.endToken,
                            ranks = null,
                            scopeType = DeclarationScopeType.Local,
                            variable = refNode.variableName,
                            type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
                        };
                        Compile(fakeDeclStatement, includeDefaultInitializer: true);
                        if (!scope.TryGetVariable(refNode.variableName, out variable))
                        {
                            throw new Exception(
                                "Compiler exception: cannot use reference to a variable that does not exist " + refNode);
                        }
                    }

                    switch (variable.typeCode)
                    {
                        default:
                            // anything else is a registry ptr!
                            var regAddr = variable.registerAddress;
                            _buffer.Add(OpCodes.PUSH);
                            _buffer.Add(variable.isGlobal ? TypeCodes.PTR_GLOBAL_REG : TypeCodes.PTR_REG);
                            AddPushULongNoTypeCode(_buffer, regAddr);
                            break;
                    }
               
                    break;
                
                case ArrayIndexReference indexReference:
                    // if we push the address, that isn't good enough, because it is not a register address...
                    // we need to indicate that the value stored in the stack is actually not a registry ptr, but a heap ptr
                    if (!scope.TryGetArray(indexReference.variableName, out var compiledArrayVar))
                    {
                        throw new Exception("Compiler: cannot access array since it not declared" +
                                            indexReference.variableName);
                    }
                    _buffer.Add(OpCodes.BPUSH);
                    _buffer.Add(compiledArrayVar.typeCode);
                    PushAddress(indexReference);
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(TypeCodes.PTR_HEAP);
                    break;
                
                case StructFieldReference fieldRef:

                    switch (fieldRef.left)
                    {
                        case VariableRefNode leftVariable:

                            if (!scope.TryGetVariable(leftVariable.variableName, out var typeCompiledVar))
                            {
                                FakeDeclare(leftVariable, out typeCompiledVar);
                            }

                            if (!_types.TryGetValue(typeCompiledVar.structType, out var type))
                            {
                                throw new Exception("Unknown type reference " + type);
                            }

                            ComputeStructOffsets(type, fieldRef.right, out var readOffset, out var readLength,
                                out var readTypeCode);

                            // push the type-code of the element
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(readTypeCode);

                            // push the offset
                            {
                                // first, load up the base address 
                                PushLoad(_buffer, typeCompiledVar.registerAddress, typeCompiledVar.isGlobal);
                                
                                // then insert the offset
                                AddPushInt(_buffer, readOffset); // SIZE, <Data>

                                // and add them up
                                _buffer.Add(OpCodes.ADD);
                            }
                            
                            // convert the address to a ptr
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.PTR_HEAP);
                            break;
                        case ArrayIndexReference leftArray:

                            // we need to find the address to the array, then add on the offset
                            if (!scope.TryGetArray(leftArray.variableName, out compiledArrayVar))
                            {
                                throw new Exception(
                                    "Compiler: cannot access array since it not declared (left-struct)" +
                                    leftArray.variableName);
                            }
                            
                            ComputeStructOffsets(compiledArrayVar.structType, fieldRef.right, out readOffset, out readLength,
                                out readTypeCode);

                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(compiledArrayVar.typeCode);
                            

                            // push the offset
                            {
                                // first, load up the base address of the array
                                PushAddress(leftArray);
                                
                                // then insert the offset
                                AddPushInt(_buffer, readOffset); // SIZE, <Data>

                                // and add them up
                                _buffer.Add(OpCodes.ADD);
                            }

                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.PTR_HEAP);
                            break;
                        default:
                            throw new NotImplementedException("structref left- cannot use the address of this expression " + expression);
                            break;
                    }
                    
                    break;
                default:
                    throw new NotImplementedException("cannot use the address of this expression " + expression);
            }
        }

        public void Compile(CommandStatement commandStatement)
        { 
            // TODO: save local state?
            // put each expression on the stack.
            var argCounter = 0;
            for (var i = 0; i < commandStatement.command.args.Length; i++)
            {
                if (commandStatement.command.args[i].isVmArg) continue;

                if (commandStatement.command.args[i].isParams)
                {
                    
                    // and then, compile the rest of the args
                    for (var j = commandStatement.args.Count - 1; j >= argCounter; j --)
                    {
                        var argExpr2 = commandStatement.args[j];
                        Compile(argExpr2);
                    }
                    
                    // first, we need to tell the program how many arguments there are left in the set
                    // , which of course, is args - i.
                    AddPushInt(_buffer, commandStatement.args.Count - argCounter);
                    break;
                }
                
                if (argCounter >= commandStatement.args.Count)
                {
                    if (commandStatement.command.args[i].isOptional)
                    {
                        AddPush(_buffer, new byte[]{}, TypeCodes.VOID);
                        continue;
                    }
                    else
                    {
                        throw new Exception("Compiler: not enough arg expressions to meet the needs of the function");
                    }
                }
                
                var argExpr = commandStatement.args[argCounter];
                var argDesc = commandStatement.command.args[i];
                if (argDesc.isRef)
                {
                    var argAddr = argExpr as AddressExpression;

                    if (argAddr == null)
                    {

                        if (argExpr is IVariableNode v)
                        {
                            argAddr = new AddressExpression(v, argExpr.StartToken);
                        }
                        else
                        {
                            throw new Exception(
                                "Compiler exception: cannot use a ref parameter with an expr that isn't an address expr");
                        }
                        
                    }
                       
                    Compile(argAddr);
                }
                else
                {
                    Compile(argExpr);

                    var destinationTypeCode =
                        commandStatement.command.args[commandStatement.argMap[argCounter]].typeCode;
                    if (destinationTypeCode != TypeCodes.ANY)
                    {
                        // only cast the type if it isn't the catch-all "any"
                        CompileCast(destinationTypeCode);
                    }
                }
                argCounter++;
            }
            
            
            // find the address of the method
            if (!_commandToPtr.TryGetValue(commandStatement.command.UniqueName, out var commandAddress))
            {
                throw new Exception("compiler: could not find method address: " + commandStatement.command);
            }
            
            _buffer.Add(OpCodes.PUSH);
            _buffer.Add(TypeCodes.INT);
            var bytes = BitConverter.GetBytes(commandAddress);
            for (var i = 0 ; i < bytes.Length; i ++)
            // for (var i = bytes.Length -1; i >= 0; i--)
            {
                _buffer.Add(bytes[i]);
            }

            _buffer.Add(OpCodes.CALL_HOST);

        }

        public void Compile(RedimStatement redimStatement)
        {
            if (!scope.TryGetArray(redimStatement.variable.variableName, out var arrayVar))
            {
                throw new Exception("invalid array to redim");
            }

            // need to reset the registers
            for (var i = redimStatement.ranks.Length - 1; i >= 0; i--)
            {
                // put the expression value onto the stack
                var expr = redimStatement.ranks[i];
                Compile(expr);

                // store the expression value (the length for this rank) in a register
                PushStore(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);


                if (i == redimStatement.ranks.Length - 1)
                {
                    // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                    AddPushInt(_buffer, 1);
                }
                else
                {
                    // get the length of the right term
                    PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i + 1], arrayVar.isGlobal);

                    // and get the multiplier factor of the right term
                    PushLoad(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i + 1], arrayVar.isGlobal);

                    // and multiply those together...
                    _buffer.Add(OpCodes.MUL);
                }

                // _buffer.Add(OpCodes.STORE);
                // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                PushStore(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i], arrayVar.isGlobal);

                // need to clear the data
                
            }
            
            
                
            // now, we need to allocate enough memory for the entire thing
            AddPushInt(_buffer, 1);
                
            for (var i = 0; i < redimStatement.ranks.Length; i++)
            {
                // _buffer.Add(OpCodes.LOAD);
                // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                _buffer.Add(OpCodes.MUL);
            }
                
            var sizeOfElement = arrayVar.byteSize;
            AddPushInt(_buffer, sizeOfElement);
                
            _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                
            // inject the type format.
            var tf = new HeapTypeFormat
            {
                typeCode = arrayVar.typeCode,
                typeId = arrayVar.structType?.typeId ?? 0,
                typeFlags = HeapTypeFormat.CreateArrayFlag(redimStatement.ranks.Length)
            };
            AddPushTypeFormat(_buffer, ref tf);
                
            _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
            // _buffer.Add(OpCodes.STORE);
            // _buffer.Add(arrayVar.registerAddress);
            PushStorePtr(_buffer, arrayVar.registerAddress, arrayVar.isGlobal);
        }

        public void Compile(DeclarationStatement declaration, bool includeDefaultInitializer=false)
        {
            /*
             * the declaration tells us that we need a register
             */
            // then, we need to reserve a register for the variable.
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);

          
            
            if (declaration.ranks == null || declaration.ranks.Length == 0)
            {
                // this is a normal variable decl.
                // scope.Create(declaration.variable, tc);
                var compiledVar = scope.Create(declaration.variable, tc, declaration.scopeType == DeclarationScopeType.Global);
                
                if (tc == TypeCodes.STRUCT)
                {

                    switch (declaration.type)
                    {
                        case StructTypeReferenceNode structTypeNode:

                            if (!_types.TryGetValue(structTypeNode.variableNode.variableName, out var structType))
                            {
                                throw new Exception("Compiler: unknown type ref " + structTypeNode.variableNode);
                            }

                            // save the type information on the variable, for lookup later.
                            compiledVar.structType = structTypeNode.variableNode.variableName;

                            // we need to allocate some memory for this instance!
                            AddPushInt(_buffer, structType.byteSize);
                            
                            // create the type-format for the allocation
                            var tf = new HeapTypeFormat
                            {
                                typeCode = TypeCodes.STRUCT,
                                typeFlags = 0,
                                typeId = _types[compiledVar.structType].typeId
                            };
                            AddPushTypeFormat(_buffer, ref tf);
                            
                            // call alloc, which expects to find the length on the stack, and the ptr is returned.
                            _buffer.Add(OpCodes.ALLOC);
                    
                            // cast the ptr to a struct type-code
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.STRUCT);
                            
                            // the ptr will be stored in the register for this variable
                            PushStorePtr(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            // PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            _dbg?.AddVariable(_buffer.Count - 1, compiledVar);

                            break;
                
                        default:
                            throw new Exception("compiler cannot handle non struct type ref ");
                    }
  
                }
            }
            else
            {
                // this is an array decl

                var arrayVar = scope.CreateArray(declaration.variable, declaration.ranks.Length, tc, declaration.scopeType == DeclarationScopeType.Global);

                if (tc == TypeCodes.STRUCT)
                {
                    // ah, the byteSize is _NOT_ just the size of the element, it is the size of the struct!
                    var arrayStructRefNode = declaration.type as StructTypeReferenceNode;
                    if (arrayStructRefNode == null) throw new Exception("array struct needs correct node type");
                    var typeName = arrayStructRefNode.variableNode.variableName;
                    if (!_types.TryGetValue(typeName, out var structType))
                    {
                        throw new Exception("Compiler: unknown type ref " + typeName);
                    }

                    arrayVar.byteSize = structType.byteSize;
                    arrayVar.structType = structType;
                }
                
                // this is an array! we need to save each rank's length
                // for (var i = 0; i < declaration.ranks.Length; i++)
                for (var i = declaration.ranks.Length -1; i >= 0; i--)
                {
                    // put the expression value onto the stack
                    var expr = declaration.ranks[i];
                    Compile(expr); 
                    
                    // reserve 2 registers for array rank metadata
                    arrayVar.rankSizeRegisterAddresses[i] = scope.AllocateRegister(); // (byte)(registerCount++);
                    arrayVar.rankIndexScalerRegisterAddresses[i] = scope.AllocateRegister(); //(byte)(registerCount++);
                    
                    // store the expression value (the length for this rank) in a register
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]);
                    PushStore(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                    if (i == declaration.ranks.Length - 1)
                    {
                        // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                        AddPushInt(_buffer, 1);
                    }
                    else
                    {
                        // get the length of the right term
                        // _buffer.Add(OpCodes.LOAD);
                        // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i + 1]); 
                        PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i + 1], arrayVar.isGlobal);
                        
                        // and get the multiplier factor of the right term
                        // _buffer.Add(OpCodes.LOAD);
                        // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i + 1]); 
                        PushLoad(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i + 1], arrayVar.isGlobal);


                        // and multiply those together...
                        _buffer.Add(OpCodes.MUL);
                    }
                    
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                    PushStore(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i], arrayVar.isGlobal);
                }
                
                
                
                // now, we need to allocate enough memory for the entire thing
                AddPushInt(_buffer, 1);
                
                for (var i = 0; i < declaration.ranks.Length; i++)
                {
                    // _buffer.Add(OpCodes.LOAD);
                    // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                    PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                    _buffer.Add(OpCodes.MUL);
                }
                
                var sizeOfElement = arrayVar.byteSize;
                AddPushInt(_buffer, sizeOfElement);
                
                _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                
                // inject the type format.
                var tf = new HeapTypeFormat
                {
                    typeCode = arrayVar.typeCode,
                    typeId = arrayVar.structType?.typeId ?? 0,
                    typeFlags = HeapTypeFormat.CreateArrayFlag(declaration.ranks.Length)
                };
                AddPushTypeFormat(_buffer, ref tf);
                
                _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
                // _buffer.Add(OpCodes.STORE);
                // _buffer.Add(arrayVar.registerAddress);
                PushStorePtr(_buffer, arrayVar.registerAddress, arrayVar.isGlobal);
                _dbg?.AddVariable(_buffer.Count - 1, arrayVar);

            }
            
            
            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.

            if (declaration.initializerExpression != null)
            {
                // ah, there is an implicit assignment!
                // we can fake this by creating a fake-assignment node
                var fakeAssignment = new AssignmentStatement
                {
                    expression = declaration.initializerExpression,
                    variable = new VariableRefNode(declaration.startToken, declaration.variable)
                };
                Compile(fakeAssignment);
            }
            else if (includeDefaultInitializer)
            {

                if (declaration.ranks == null)
                {
                    switch (tc)
                    {
                        case TypeCodes.STRUCT:
                            // TODO: it isn't possible to pass structs as refs atm, but if it was, this would be a problem. 
                            break;
                        case TypeCodes.STRING: // TODO: handle the empty string?
                            // if the variable is a string, then always assign it to the empty string, which will get interned. 
                            var fadeStrAssignment = new AssignmentStatement
                            {
                                expression = new LiteralStringExpression(declaration.startToken, ""),
                                variable = new VariableRefNode(declaration.startToken, declaration.variable)
                            };
                            Compile(fadeStrAssignment);
                            break;
                        default:
                            // if the variable is a primitive, then always assign it to a default value.
                            var fakeAssignment = new AssignmentStatement
                            {
                                expression = new LiteralIntExpression(declaration.startToken, 0),
                                variable = new VariableRefNode(declaration.startToken, declaration.variable)
                            };
                            Compile(fakeAssignment);
                            break;
                    }
                    
                }
            }
        }

        public void PushAddress(ArrayIndexReference arrayRefNode)
        {
            if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledArrayVar))
            {
                throw new Exception("Compiler: cannot access array since it not declared" +
                                    arrayRefNode.variableName);
            }

            var sizeOfElement = compiledArrayVar.byteSize;

            for (var i = 0; i < arrayRefNode.rankExpressions.Count; i++)
            {
                // load the multiplier factor for the term
                PushLoad(_buffer, compiledArrayVar.rankIndexScalerRegisterAddresses[i], compiledArrayVar.isGlobal);
                var expr = arrayRefNode.rankExpressions[i];
                Compile(expr); // load the expression index
                
                // duplicate the actual number so it can be used later in the math
                _buffer.Add(OpCodes.DUPE);
                
                // load up the max size for this rank of the array, 
                PushLoad(_buffer, compiledArrayVar.rankSizeRegisterAddresses[i], compiledArrayVar.isGlobal);
                
                // this will pull off the max-rank, then the dupe'd index value
                _buffer.Add(OpCodes.BOUNDS_CHECK);
                
                _buffer.Add(OpCodes.MUL);

                if (i > 0)
                {
                    _buffer.Add(OpCodes.ADD);
                }
            }

            // get the size of the element onto the stack
            AddPushInt(_buffer, sizeOfElement);
            
            // multiply the size of the element, and the index, to get the offset into the memory
            _buffer.Add(OpCodes.MUL);

            // load the array's ptr onto the stack, this is for the math of the offset
            PushLoadPtr(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
            
            // add the offset to the original pointer to get the write location
            _buffer.Add(OpCodes.ADD);

        }

        static void PushStorePtr(List<byte> buffer, ulong regAddr, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.STORE_PTR_GLOBAL : OpCodes.STORE_PTR);
            // buffer.Add(regAddr);
            AddPushULongNoTypeCode(buffer, regAddr);
        }
        static void PushLoadPtr(List<byte> buffer, ulong regAddr, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.LOAD_PTR_GLOBAL : OpCodes.LOAD_PTR);
            // buffer.Add(regAddr);
            AddPushULongNoTypeCode(buffer, regAddr);
        }

        static void PushStore(List<byte> buffer, ulong registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.STORE_GLOBAL : OpCodes.STORE);
            AddPushULongNoTypeCode(buffer, registerAddress);

        }
        static void PushLoad(List<byte> buffer, ulong registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.LOAD_GLOBAL : OpCodes.LOAD);
            AddPushULongNoTypeCode(buffer, registerAddress);
        }

        void CompileStructData(CompiledVariable compiledVar, bool ignoreType=true)
        {
            if (!_types.TryGetValue(compiledVar.structType, out var structType))
            {
                throw new Exception("Referencing type that does not exist yet. In assignment." + compiledVar.name + " and " + compiledVar.structType);
            }
            
            if (ignoreType)
                _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

            // push the size of the write operation- it is the size of the struct we happen to have!
            AddPushInt(_buffer, structType.byteSize);
                        
            // now, push the pointer where to write the data to- which, we know is the register address
            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
            
            _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
        }

        void CastToInt()
        {
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
        }

        void CompileCast(byte typeCode)
        {
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(typeCode);
        }
        
        void CompileAssignmentLeftHandSide(IVariableNode variable)
        {
            switch (variable)
            {
                case ArrayIndexReference arrayRefNode:
                    if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledArrayVar))
                    {
                        throw new Exception("Compiler: cannot access array since it not declared" +
                                            arrayRefNode.variableName);
                    }
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledArrayVar.typeCode);
                    _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                    
                    var sizeOfElement = compiledArrayVar.byteSize;
                    AddPushInt(_buffer, sizeOfElement);

                    PushAddress(arrayRefNode);
                    // write! It'll find the ptr, then the size, and then the data itself
                    _buffer.Add(OpCodes.WRITE);
                    
                    break;
                case VariableRefNode variableRefNode:


                    if (scope.TryGetArray(variableRefNode.variableName, out var compiledArrayVariable))
                    {
                        // hopefully the rhs compilation pushed a ptr onto the stack, 
                        //  so the job here is to allocate some new memory, copy the memory, and then write the 
                        //  pointer to the register address for the array
                        
                        _buffer.Add(OpCodes.COPY_HEAP_MEM);
                        
                        // save the resulting pointer
                        PushStorePtr(_buffer, compiledArrayVariable.registerAddress, compiledArrayVariable.isGlobal);
                        
                        // // push the size of the write operation- it is the size of the struct we happen to have!
                        // // AddPushInt(_buffer, structType.byteSize);
                        // // AddPushInt(_buffer, 10);
                        // AddPushInt(_buffer, compiledArrayVariable.byteSize * 5); // the 5 is hardcoded for a test
                        //
                        // // now, push the pointer where to write the data to- which, we know is the register address
                        // // PushLoad(_buffer, comp.registerAddress, compiledVar.isGlobal);
                        // PushAddress(new ArrayIndexReference
                        // {
                        //     variableName = variableRefNode.variableName,
                        //     rankExpressions = new List<IExpressionNode>
                        //     {
                        //         new LiteralIntExpression(Token.Blank, 0)
                        //     }
                        // });
                        //
                        // _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
                        //
                        //
                        // _buffer.Add(OpCodes.CAST);
                        // _buffer.Add(compiledArrayVariable.typeCode);
                        //
                        // PushStorePtr(_buffer, compiledArrayVariable.registerAddress, compiledArrayVariable.isGlobal);

                        break;
                    }
                    
                    if (!scope.TryGetVariable(variableRefNode.variableName, out var compiledVar))
                    {
                        var tc = VmUtil.GetTypeCode(variableRefNode.DefaultTypeByName);
                        compiledVar = scope.Create(variableRefNode.variableName, tc, false);
                    }
                    
                    // wait wait, if the rhs is a pointer, and the lhs is a struct, then we actually need to COPY the pointer data...
                    if (compiledVar.typeCode == TypeCodes.STRUCT)
                    {
                        CompileStructData(compiledVar);
                        /*
                         * when this is getting set to an array- the entire struct data is sitting on the stack. The array-expression reads  it from the heap
                         * If we just just cast to struct, we'll just be capturing some random part of the memory...
                         * instead, we need to assume that the stack contains the right length amount of valid bytes to write into memory...
                         *
                         * we know the struct data here, or rather, we can...
                         */
                        // if (!_types.TryGetValue(compiledVar.structType, out var structType))
                        // {
                        //     throw new Exception("Referencing type that does not exist yet. In assignment." + compiledVar.name + " and " + compiledVar.structType);
                        // }
                        //
                        // _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                        //
                        // // push the size of the write operation- it is the size of the struct we happen to have!
                        // AddPushInt(_buffer, structType.byteSize);
                        //
                        // // now, push the pointer where to write the data to- which, we know is the register address
                        // PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        //
                        // // the address is a struct ref, not an int, so for the write-command to work, we need to cast the struct to an int
                        // _buffer.Add(OpCodes.CAST);
                        // _buffer.Add(TypeCodes.INT);
                        //
                        // _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
                        break;
                    }
                    
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledVar.typeCode);
    
                    // store the value of the expression&cast in the desired register.
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(compiledVar.registerAddress);
                    if (compiledVar.typeCode == TypeCodes.STRING)
                    {
                        PushStorePtr(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    }
                    else
                    {
                        PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    }
                    _dbg?.AddVariable(_buffer.Count - 1, compiledVar);
                    break;
                case StructFieldReference fieldReferenceNode:

                    switch (fieldReferenceNode.left)
                    {
                        case ArrayIndexReference arrayRefNode:
                            
                            // we need to find the start index of the array element,
                            // and then add the offset for the field access part (the right side)
                            if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledLeftArrayVar))
                            {
                                throw new Exception("Compiler: cannot access array since it not declared" +
                                                    arrayRefNode.variableName);
                            }
                            
                            

                            var rightType = compiledLeftArrayVar.structType;
                            ComputeStructOffsets(rightType, fieldReferenceNode.right, out var rightOffset, out var rightLength, out var rightTypeCode);

                            // cast the value to the right type
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(rightTypeCode);
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

                            // load the write-length
                            AddPushInt(_buffer, rightLength);
                            
                            // load the offset of the right side
                            AddPushInt(_buffer, rightOffset);
                            
                            // load the array pointer
                            PushAddress(arrayRefNode);
                            
                            // add the pointer and the offset together
                            _buffer.Add(OpCodes.ADD);
                            
                            // write the data at the array index, by the offset, 
                            _buffer.Add(OpCodes.WRITE);
                            break;
                        
                        case VariableRefNode variableRef:
                            if (!scope.TryGetVariable(variableRef.variableName, out compiledVar))
                            {
                                FakeDeclare(variableRef, out compiledVar);
                            }

                            if (compiledVar.typeCode == TypeCodes.STRUCT)
                            {
                                
                            }
                            
                       
                            // load up the compiled type info 
                            var type = _types[compiledVar.structType];
                            ComputeStructOffsets(type, fieldReferenceNode.right, out var offset, out var length, out rightTypeCode);

                            // always cast the expression to the correct type code; slightly wasteful, could be better.
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(rightTypeCode);
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                            
                            // push the length of the write segment
                            AddPushInt(_buffer, length);
                            
                            // load the base address of the variable
                            // _buffer.Add(OpCodes.LOAD);
                            // _buffer.Add(compiledVar.registerAddress);
                            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            
                            // load the offset of the right side
                            // AddPushInt(_buffer, offset);
                            AddPushPtr(_buffer, new VmPtr(){memoryPtr = offset}, TypeCodes.PTR_HEAP);
                            
                            // sum them, then the result is the ptr on the stack
                            _buffer.Add(OpCodes.ADD);
                            
                            // pull the ptr, length, then data, and return the ptr.
                            _buffer.Add(OpCodes.WRITE);
                            
                            break;
                        default:
                            throw new NotImplementedException("unhandled left side of operation");
                    }
                    break;
                default:
                    throw new NotImplementedException("Unsupported reference assignment");
            }
        }
        
        
        public void Compile(AssignmentStatement assignmentStatement)
        {
            /*
             * in order to assign, we need to know what we are assigning two, and find the correct place to put the result.
             *
             * If it is a simple variable, then it lives on a local register.
             * If it is an array, then it lives in memory.
             */


            if (assignmentStatement.variable is VariableRefNode leftRef &&
                scope.TryGetVariable(leftRef.variableName, out var leftVar) && leftVar.typeCode == TypeCodes.STRUCT)
            {
                // _buffer.Add(OpCodes.BREAKPOINT);
            }
            
            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);
            CompileAssignmentLeftHandSide(assignmentStatement.variable);

        }

        public void ComputeStructOffsets(CompiledType baseType, IVariableNode right, out int offset, out int writeLength, out byte typeCode)
        {
            writeLength = 0;
            offset = 0;

            switch (right)
            {
                case VariableRefNode variableRefNode:
                    var name = variableRefNode.variableName;
                    if (!baseType.fields.TryGetValue(name, out var member))
                    {
                        throw new Exception("Compiler: unknown member access " + name);
                    }

                    writeLength = member.Length;
                    offset += member.Offset;
                    typeCode = member.TypeCode;
                    break;
                case StructFieldReference structRef:

                    switch (structRef.left)
                    {
                        case VariableRefNode leftVariableRefNode:
                            var leftName = leftVariableRefNode.variableName;
                            if (!baseType.fields.TryGetValue(leftName, out var leftMember))
                            {
                                throw new Exception("Compiler: unknown member access " + leftName);
                            }
                            
                            ComputeStructOffsets(leftMember.Type, structRef.right, out offset, out writeLength, out typeCode);
                            offset += leftMember.Offset;
                            
                            break;
                        default:
                            throw new NotImplementedException("Cannot compute offsets for left");
                    }
                    // look up the field member of the left side, and then recursively call this function on the right.
                    // if (!baseType.fields.TryGetValue(name, out var leftMember))
                    // {
                    //     throw new Exception("Compiler: unknown member access " + name);
                    // }
                    
                    break;
                default:
                    throw new NotImplementedException("Cannot compute offsets");
            }
        }


        public void Compile(ExpressionStatement statement)
        {
            Compile(statement.expression);
            
            
            // nothing happens with the expression, because it isn't being assigned to anything...
            // but we don't know how big the result of the previous expression was... 
            _buffer.Add(OpCodes.DISCARD_TYPED);
            
            //
            
        }
        
        public void Compile(IExpressionNode expr)
        {

            // CompiledVariable compiledVar = null;
            switch (expr)
            {
                case DefaultValueExpression defExpr:
                    // the default value for any type is just zeros, right?

                    switch (defExpr.ParsedType.type)
                    {
                        case VariableType.String:
                            Compile(new LiteralStringExpression(defExpr.startToken, ""));
                            break;
                        case VariableType.Struct:
                            
                            if (_types.TryGetValue(defExpr.ParsedType.structName, out var typeInfo))
                            {
                                AddPushZeros(_buffer, TypeCodes.STRUCT, typeInfo.byteSize);
                            }
                            else
                            {
                                throw new Exception("unknown type reference" + defExpr.ParsedType.structName);
                            }
                            break;
                        default:
                            // push the type-code for this def-expr
                            var tc = VmUtil.GetTypeCode(defExpr.ParsedType.type);

                            // everything else is an empty zero block
                            AddPushZeros(_buffer, tc, TypeCodes.GetByteSize(tc));
                            break;
                    }
                    break;
                case CommandExpression commandExpr:
                    Compile(new CommandStatement
                    {
                        args = commandExpr.args,
                        command = commandExpr.command,
                        startToken = commandExpr.startToken,
                        endToken = commandExpr.endToken,
                        argMap = commandExpr.argMap
                    });
                    break;
                case LiteralStringExpression literalString:
                    // allocate some memory for a string...
                    var str = literalString.value;
                    var strSize = str.Length * TypeCodes.GetByteSize(TypeCodes.INT);

                    if (_options.InternStrings)
                    {
                        
                        if (!stringToCallingInstructionIndexes.TryGetValue(str, out var indexes))
                        {
                            // capture this string as something that we need to intern... 
                            stringToCallingInstructionIndexes[str] = indexes = new HashSet<int>();
                        }

                        // take note that this index needs to be mapped back to the original string.
                        indexes.Add(_buffer.Count);
                    
                        // push a fake pointer onto the stack... The value gets replaced 
                        //  at RUNTIME as the machine is allocating the interned strings. 
                        // AddPushUInt(_buffer, int.MaxValue, includeTypeCode: false);
                        // AddPushInt(_buffer, int.MaxValue);
                        AddPushPtr(_buffer, VmPtr.TEMP, TypeCodes.PTR_HEAP);
                    }
                    else
                    {
                        // push the string data...
                        for (var i = 0 ; i < str.Length; i ++)
                        {
                            // push the string into the interned data, and then remember the pointer to that. 
                            var c = (uint)str[i];
                            AddPushUInt(_buffer, c, includeTypeCode:false);
                        }
                        
                        AddPushInt(_buffer, strSize); // SIZE, <Data>
                        
                        // this one will get used by the Write call
                        _buffer.Add(OpCodes.DUPE); // SIZE, SIZE, <Data>
                        
                        // add in the type-format
                        AddPushTypeFormat(_buffer, ref HeapTypeFormat.STRING_FORMAT);
                        
                        // allocate a ptr to the stack
                        _buffer.Add(OpCodes.ALLOC); // PTR, SIZE, <Data>
                        
                        _buffer.Add(OpCodes.WRITE_PTR); // consume the ptr, then the length, then the data
                    }
                    
                    
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(TypeCodes.STRING);
                    break;
                case LiteralRealExpression literalReal:
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.REAL);
                    var realValue = BitConverter.GetBytes(literalReal.value);
                    // for (var i = realValue.Length - 1; i >= 0; i--)
                    for (var i = 0 ; i < realValue.Length; i ++)
                    {
                        _buffer.Add(realValue[i]);
                    }
                    break;
                case LiteralIntExpression literalInt:
                    // push the literal value
                    AddPushInt(_buffer, literalInt.value);
                    break;
                case ArrayIndexReference arrayRef:
                    // need to fetch the value from the array...

                    if (!scope.TryGetArray(arrayRef.variableName, out var arrayVar))
                    {
                        CompileAsInvocation(arrayRef);
                        break;
                        // if (_functionTable.TryGetValue(arrayRef.variableName, out var func))
                        // {
                        //     break;
                        // }
                        // throw new Exception("compiler exception! the referenced array has not been declared yet " +
                        //                     arrayRef.variableName);
                    }
                    
                    var sizeOfElement = arrayVar.byteSize;

                    
                    // load the size up
                    AddPushInt(_buffer, sizeOfElement);

                    PushAddress(arrayRef);

                    // read, it'll find the ptr, size, and then place the data onto the stack
                    _buffer.Add(OpCodes.READ);
                    
                    // we need to inject the type-code back into the stack, since it doesn't exist in heap
                    _buffer.Add(OpCodes.BPUSH);
                    _buffer.Add(arrayVar.typeCode);

                    break;
                case StructFieldReference structRef:
                    // we need to load up the pointer, and read from the address...
                    switch (structRef.left)
                    {
                        case VariableRefNode variableRef:

                            if (!scope.TryGetVariable(variableRef.variableName, out var typeCompiledVar))
                            {
                                FakeDeclare(variableRef, out typeCompiledVar);
                            }

                            if (!_types.TryGetValue(typeCompiledVar.structType, out var type))
                            {
                                throw new Exception("Unknown type reference " + type);
                            }

                            ComputeStructOffsets(type, structRef.right, out var readOffset, out var readLength, out var readTypeCode);
                            
                            // push the size of the read operation
                            AddPushInt(_buffer, readLength);
                            
                            // push the read offset, so that we can add it to the ptr
                            // AddPushInt(_buffer, readOffset);
                            AddPushPtr(_buffer, new VmPtr{memoryPtr = readOffset}, TypeCodes.PTR_HEAP);
                            
                            // push the ptr of the variable, and cast it to an int for easy math
                            PushLoad(_buffer, typeCompiledVar.registerAddress, typeCompiledVar.isGlobal);
                            
                            // TODO: I removed these as part of the PTR refactor?
                            // _buffer.Add(OpCodes.CAST);
                            // _buffer.Add(TypeCodes.INT);
                            
                            // add those two op codes back together...
                            _buffer.Add(OpCodes.ADD);
                            
                            // read the summed ptr, then the length
                            _buffer.Add(OpCodes.READ);
                            
                            // we need to inject the type-code back into the stack, since it doesn't exist in heap
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(readTypeCode);
                            
                            break;
                        case ArrayIndexReference arrayRefNode:
                            if (!scope.TryGetArray(arrayRefNode.variableName, out var leftArrayVar))
                            {
                                throw new CompilerException("compiler exception (3)! the referenced array has not been declared yet " +
                                                    arrayRefNode.variableName, arrayRefNode);
                            }
                            
                            var rightType = leftArrayVar.structType;
                            ComputeStructOffsets(rightType, structRef.right, out var rightOffset, out var rightLength, out var rightTypeCode);

                            // load the write-length
                            AddPushInt(_buffer, rightLength);
                            
                            // load the offset of the right side
                            AddPushInt(_buffer, rightOffset);
                            
                            // load the array pointer
                            PushAddress(arrayRefNode);
                            
                            // add the pointer and the offset together
                            _buffer.Add(OpCodes.ADD);
                            
                            // write the data at the array index, by the offset, 
                            _buffer.Add(OpCodes.READ);
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(rightTypeCode);
                            break;
                        default:
                            throw new NotImplementedException("Cannot eval left based nested struct pointer");
                    }
                    break;
                case VariableRefNode variableRef:
                    
                    // maybe this is an array?
                    if (scope.TryGetArray(variableRef.variableName, out var compiledArrayVar))
                    {
                        // compile the pointer to this array?
                        PushLoadPtr(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
                        
                        // // ah, the entire memory needs to get pushed 
                        // // load the size up
                        // AddPushInt(_buffer, compiledArrayVar.byteSize * 5); // the 5 is hardcoded for a test
                        //
                        // PushAddress(new ArrayIndexReference
                        // {
                        //     variableName = variableRef.variableName,
                        //     rankExpressions = new List<IExpressionNode>
                        //     {
                        //         new LiteralIntExpression(Token.Blank, 0)
                        //     }
                        // });
                        // // PushLoad(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
                        //
                        // // read, it'll find the ptr, size, and then place the data onto the stack
                        // _buffer.Add(OpCodes.READ);
                        //
                        // // inject a type-code onto the stack
                        // _buffer.Add(OpCodes.BPUSH);
                        // _buffer.Add(compiledArrayVar.typeCode);

                        break;
                    }
                    
                    
                    // emit the read from register
                    if (!scope.TryGetVariable(variableRef.variableName, out var compiledVar))
                    {
                        // can we auto declare it?
                        FakeDeclare(variableRef, out compiledVar);
                    }

                    if (compiledVar.typeCode == TypeCodes.STRUCT)
                    {
                        // ah, if this is a struct, then we should push the entire contents of the memory pointer on the stack.
                        // CompileStructData(compiledVar, false);
                        // we don't want to WRITE- we need to READ it from memory
                        if (!_types.TryGetValue(compiledVar.structType, out var structType))
                        {
                            throw new Exception("Referencing type that does not exist yet. In value." + compiledVar.name + " and " + compiledVar.structType);
                        }
                        // load the size up
                        AddPushInt(_buffer, structType.byteSize);

                        PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        
                        // read, it'll find the ptr, size, and then place the data onto the stack
                        _buffer.Add(OpCodes.READ);
                    
                        // we need to inject the type-code back into the stack, since it doesn't exist in heap
                        _buffer.Add(OpCodes.BPUSH);
                        _buffer.Add(TypeCodes.STRUCT);
                        break;
                    }
                    
                    PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    break;
                case UnaryOperationExpression unary:
                    Compile(unary.rhs);
                    switch (unary.operationType)
                    {
                        case UnaryOperationType.Not:
                            _buffer.Add(OpCodes.NOT);
                            break;
                        case UnaryOperationType.Negate:
                            AddPushInt(_buffer, -1);
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case UnaryOperationType.BitwiseNot:
                            _buffer.Add(OpCodes.BITWISE_NOT);
                            break;
                        default:
                            throw new Exception("Compiler: unsupported unary operaton " + unary.operationType);
                    }

                    break;
                case BinaryOperandExpression op:
                    
                    // TODO: At this point, we could decide _which_ add/mul method to call given the type.
                    // if both lhs and rhs are ints, then we could emit an IADD, and don't need to emit the type codes ahead of time.
                    
                    
                    // if the op is a short-circuitable operation, then we may not jump over parts of the compilation pass. 
                    /*
                     * a OR b ---- b only executes if !a
                     * a AND b --- b only executes if a
                     *
                     */
                    
                    /*
                     * Do we need to cast either side? For example, if we had
                     * 3.2 * 4, then the whole expression should be cast to a float
                     */
                    
                    Compile(op.lhs);
                    int jumpIndex = -1;
                    
                    switch (op.operationType)
                    {
                        case OperationType.Or:
                            // we need to jump based on the value, but also need the value to return 
                            _buffer.Add(OpCodes.DUPE);
                            CastToInt();
                        
                            // then, put a fake value in for the end of the rhs success jump... We'll fix it later.
                            jumpIndex = _buffer.Count;
                            AddPushInt(_buffer, int.MaxValue);
                    
                            // then, do the jump-gt-zero (because an or is happy if the value on the left is truthy)
                            _buffer.Add(OpCodes.JUMP_GT_ZERO);
                            break;
                        case OperationType.And:
                            // we need to jump based on the value, but also need the value to return 
                            _buffer.Add(OpCodes.DUPE);
                            CastToInt();

                            // then, put a fake value in for the end of the rhs success jump... We'll fix it later.
                            jumpIndex = _buffer.Count;
                            AddPushInt(_buffer, int.MaxValue);
                            
                            // then, jump if the value is 0, because if the lhs is false, then there is no point in checking the rhs; the whole AND expression will be false
                            _buffer.Add(OpCodes.JUMP_ZERO);
                            break;
                    }

                    switch (op.operationType)
                    {
                        // in DarkBasic, the not operator required a RHS even though it was ignored. 
                        //  for parity's sake, I'll allow it and just never compile the RHS for
                        //  bitwise nots. 
                        case OperationType.Bitwise_Not:
                            break;
                        default:
                            Compile(op.rhs);
                            break;
                    }

                    switch (op.operationType)
                    {
                        case OperationType.Add:
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.Mult:
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case OperationType.RaisePower:
                            _buffer.Add(OpCodes.POWER);
                            break;
                        case OperationType.Divide:
                            _buffer.Add(OpCodes.DIVIDE);
                            break;
                        case OperationType.Mod:
                            _buffer.Add(OpCodes.MOD);
                            break;
                        case OperationType.Subtract:
                            // negate the second value, and add.
                            AddPushInt(_buffer, -1);
                            _buffer.Add(OpCodes.MUL);
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.GreaterThan:
                            _buffer.Add(OpCodes.GT);
                            break;
                        case OperationType.LessThan:
                            _buffer.Add(OpCodes.LT);
                            break;
                        case OperationType.GreaterThanOrEqualTo:
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.LessThanOrEqualTo:
                            _buffer.Add(OpCodes.LTE);
                            break;
                        case OperationType.EqualTo:
                            _buffer.Add(OpCodes.EQ);
                            break;
                        case OperationType.NotEqualTo:
                            _buffer.Add(OpCodes.EQ);
                            _buffer.Add(OpCodes.NOT);
                            break;
                        case OperationType.Xor:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.BITWISE_XOR);
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.And:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.MUL);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.Or:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.ADD);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.Bitwise_And:
                            _buffer.Add(OpCodes.BITWISE_AND);
                            break;
                        case OperationType.Bitwise_Or:
                            _buffer.Add(OpCodes.BITWISE_OR);
                            break;
                        case OperationType.Bitwise_Xor:
                            _buffer.Add(OpCodes.BITWISE_XOR);
                            break;
                        case OperationType.Bitwise_Not:
                            _buffer.Add(OpCodes.BITWISE_NOT);
                            break;
                        case OperationType.Bitwise_LeftShift:
                            _buffer.Add(OpCodes.BITWISE_LEFTSHIFT);
                            break;
                        case OperationType.Bitwise_RightShift:
                            _buffer.Add(OpCodes.BITWISE_RIGHTSHIFT);
                            break;
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
                    }
                    
                    var endJumpValue = _buffer.Count;
                    if (jumpIndex > 0)
                    {
                        // ignore the type code for the jump...
                        _buffer.Add(OpCodes.NOOP); 
                        var successJumpBytes = BitConverter.GetBytes(endJumpValue);
                        for (var i = 0; i < successJumpBytes.Length; i++)
                        {
                            // offset by 2, because of the opcode, and the type code
                            _buffer[jumpIndex + 2 + i] = successJumpBytes[i];
                        }
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
            

        }

        
        void FakeDeclare(VariableRefNode refNode, out CompiledVariable compiledVar)
        {
            var fakeDeclStatement = new DeclarationStatement
            {
                startToken = refNode.startToken,
                endToken = refNode.endToken,
                ranks = null,
                scopeType = DeclarationScopeType.Local,
                variable = refNode.variableName,
                type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
            };
            Compile(fakeDeclStatement);
            if (!scope.TryGetVariable(refNode.variableName, out compiledVar))
            {
                throw new CompilerException("compiler exception (5)! the referenced variable has not been declared yet " +
                                            refNode.variableName, refNode);
            }
        }

        private static void AddPush(List<byte> buffer, byte[] value, byte typeCode)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(typeCode);
            for (var i = 0 ; i < value.Length; i ++)
            // for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }

        private static void AddPushTypeFormat(List<byte> buffer, ref HeapTypeFormat format)
        {
            buffer.Add(OpCodes.PUSH_TYPE_FORMAT);
            HeapTypeFormat.AddToBuffer(ref format, buffer);
        }

        private static void AddPushPtr(List<byte> buffer, VmPtr ptr, byte typeCode)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(typeCode);
            var value = VmPtr.GetBytes(ref ptr);
            for (var i = 0; i < value.Length; i++)
                // for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
        
        private static void AddPushInt(List<byte> buffer, int x)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(TypeCodes.INT);
            var value = BitConverter.GetBytes(x);
            for (var i = 0; i < value.Length; i++)
            // for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }

        
        private static void AddPushZeros(List<byte> buffer, byte typeCode, int howManyBytesOfZero)
        {
            buffer.Add(OpCodes.PUSH_ZEROS);
            buffer.Add(typeCode);    
            var value = BitConverter.GetBytes(howManyBytesOfZero);
            for (var i = 0; i < value.Length; i++)
            {
                buffer.Add(value[i]);
            }
        }
        
        private static void AddPushULongNoTypeCode(List<byte> buffer, ulong x)
        {
            var value = BitConverter.GetBytes(x);
            for (var i = 0 ; i < value.Length; i ++)
            {
                buffer.Add(value[i]);
            }
        }
        
        private static void AddPushUInt(List<byte> buffer, uint x, bool includeTypeCode=true)
        {
            if (includeTypeCode)
            {
                buffer.Add(OpCodes.PUSH);
            }
            else
            {
                buffer.Add(OpCodes.PUSH_TYPELESS);
            }
            buffer.Add(TypeCodes.INT);

            var value = BitConverter.GetBytes(x);
            // for (var i = value.Length - 1; i >= 0; i--)
            for (var i = 0 ; i < value.Length; i ++)
            {
                buffer.Add(value[i]);
            }
        }
    }
}