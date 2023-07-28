using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Virtual
{
    public class CompiledVariable
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public string structType;
        public byte registerAddress;
    }

    public class CompiledArrayVariable
    {
        public int byteSize;
        public byte typeCode;
        public string name;
        public CompiledType structType;
        public byte registerAddress;
        public byte[] rankSizeRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the size of the rank
        public byte[] rankIndexScalerRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the multiplier factor for the rank's indexing
    }

    public class CompiledType
    {
        public int byteSize;
        public Dictionary<string, CompiledTypeMember> fields = new Dictionary<string, CompiledTypeMember>();
    }

    public struct CompiledTypeMember
    {
        public int Offset, Length;
        public byte TypeCode;
        public CompiledType Type;
    }

    public struct LabelReplacement
    {
        public int InstructionIndex;
        public string Label;
    }
    
    public class Compiler
    {
        public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;
        public int registerCount;

        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
            new Dictionary<string, CompiledArrayVariable>();

        private Dictionary<string, CompiledType> _types = new Dictionary<string, CompiledType>();

        public HostMethodTable methodTable;

        private Dictionary<CommandDescriptor, int> _commandToPtr = new Dictionary<CommandDescriptor, int>();

        private List<LabelReplacement> _labelReplacements = new List<LabelReplacement>();
        private Dictionary<string, int> _labelToInstructionIndex = new Dictionary<string, int>();
        public Compiler(CommandCollection commands)
        {
            this.commands = commands;

            var methods = new HostMethod[commands.Commands.Count];
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                methods[i] = HostMethodUtil.BuildHostMethodViaReflection(commands.Commands[i].method);
                _commandToPtr[commands.Commands[i]] = i;
            }
            methodTable = new HostMethodTable
            {
                methods = methods
            };
            
        }

        public void Compile(ProgramNode program)
        {

            foreach (var typeDef in program.typeDefinitions)
            {
                Compile(typeDef);
            }
            
            foreach (var statement in program.statements)
            {
                Compile(statement);
            }
            
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
                    _buffer[replacement.InstructionIndex + 2 + i] = locationBytes[(locationBytes.Length -1) - i];
                }
            }
        }

        public void Compile(TypeDefinitionStatement typeDefinition)
        {
            /*
             * compile all the type definitions first!
             * for each type, we need to pre-compute the offset _per_ field
             * and we need to calculate the total size for the struct
             */
            var typeName = typeDefinition.name.variableName;
            var type = new CompiledType();
            int totalSize = 0;
            foreach (var decl in typeDefinition.declarations)
            {
                var fieldOffset = totalSize;
                var typeMember = new CompiledTypeMember
                {
                    Offset = fieldOffset
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
        }

        public void Compile(IStatementNode statement)
        {
            switch (statement)
            {
                case DeclarationStatement declarationStatement:
                    Compile(declarationStatement);
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
                default:
                    throw new Exception("compiler exception: unhandled statement node");
            }
        }

        public void Compile(LabelDeclarationNode labelStatement)
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
                    if (!_varToReg.TryGetValue(refNode.variableName, out var variable))
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
                        Compile(fakeDeclStatement);
                        if (!_varToReg.TryGetValue(refNode.variableName, out variable))
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
                            AddPush(_buffer, new byte[]{regAddr}, TypeCodes.PTR_REG);
                            break;
                    }
               
                    break;
                
                case ArrayIndexReference indexReference:
                    // if we push the address, that isn't good enough, because it is not a register address...
                    // we need to indicate that the value stored in the stack is actually not a registry ptr, but a heap ptr
                    if (!_arrayVarToReg.TryGetValue(indexReference.variableName, out var compiledArrayVar))
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
                default:
                    throw new NotImplementedException("cannot use the address of this expression " + expression);
            }
        }

        public void Compile(CommandStatement commandStatement)
        { 
            // TODO: save local state?
            
            // put each expression on the stack.
            for (var i = 0; i < commandStatement.command.args.Count; i++)
            {
                if (i >= commandStatement.args.Count)
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
                
                
                var argExpr = commandStatement.args[i];
                var argDesc = commandStatement.command.args[i];
                if (argDesc.isRef)
                {
                    var argAddr = argExpr as AddressExpression;
                    
                    if (argAddr == null)
                        throw new Exception(
                            "Compiler exception: cannot use a ref parameter with an expr that isn't an address expr");

                    Compile(argAddr);
                }
                else
                {
                    Compile(argExpr);
                }
            }
            
            
            // find the address of the method
            if (!_commandToPtr.TryGetValue(commandStatement.command, out var commandAddress))
            {
                throw new Exception("compiler: could not find method address: " + commandStatement.command);
            }
            
            _buffer.Add(OpCodes.PUSH);
            _buffer.Add(TypeCodes.INT);
            var bytes = BitConverter.GetBytes(commandAddress);
            for (var i = bytes.Length -1; i >= 0; i--)
            {
                _buffer.Add(bytes[i]);
            }

            _buffer.Add(OpCodes.CALL_HOST);
        }
        
        public void Compile(DeclarationStatement declaration)
        {
            /*
             * the declaration tells us that we need a register
             */
            // then, we need to reserve a register for the variable.
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);

          
            
            if (declaration.ranks == null || declaration.ranks.Length == 0)
            {
                // this is a normal variable decl.
                var compiledVar = _varToReg[declaration.variable] = new CompiledVariable
                {
                    registerAddress = (byte)(registerCount++),
                    name = declaration.variable,
                    typeCode = tc,
                    byteSize = TypeCodes.GetByteSize(tc)
                };

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
                            
                            // call alloc, which expects to find the length on the stack, and the ptr is returned.
                            _buffer.Add(OpCodes.ALLOC);
                    
                            // cast the ptr to a struct type-code
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.STRUCT);
                            
                            // the ptr will be stored in the register for this variable
                            _buffer.Add(OpCodes.STORE);
                            _buffer.Add(compiledVar.registerAddress);
                            break;
                
                        default:
                            throw new Exception("compiler cannot handle non struct type ref ");
                    }
  
                }
            }
            else
            {
                // this is an array decl
                var arrayVar = _arrayVarToReg[declaration.variable] = new CompiledArrayVariable()
                {
                    registerAddress = (byte)(registerCount++),
                    rankSizeRegisterAddresses = new byte[declaration.ranks.Length],
                    rankIndexScalerRegisterAddresses = new byte[declaration.ranks.Length],
                    name = declaration.variable,
                    typeCode = tc,
                    byteSize = TypeCodes.GetByteSize(tc)
                };

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
                for (var i = declaration.ranks.Length -1; i >= 0; i--)
                {
                    // put the expression value onto the stack
                    var expr = declaration.ranks[i];
                    Compile(expr); 
                    
                    // reserve 2 registers for array rank metadata
                    arrayVar.rankSizeRegisterAddresses[i] = (byte)(registerCount++);
                    arrayVar.rankIndexScalerRegisterAddresses[i] = (byte)(registerCount++);
                    
                    // store the expression value (the length for this rank) in a register
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); 

                    if (i == declaration.ranks.Length - 1)
                    {
                        // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                        _buffer.Add(OpCodes.PUSH);
                        _buffer.Add(TypeCodes.INT);
                        _buffer.Add(0);
                        _buffer.Add(0);
                        _buffer.Add(0);
                        _buffer.Add(1);
                    }
                    else
                    {
                        // get the length of the right term
                        _buffer.Add(OpCodes.LOAD);
                        _buffer.Add(arrayVar.rankSizeRegisterAddresses[i + 1]); 
                        
                        // and get the multiplier factor of the right term
                        _buffer.Add(OpCodes.LOAD);
                        _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i + 1]); 

                        // and multiply those together...
                        _buffer.Add(OpCodes.MUL);
                    }
                    
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                }
                
                
                
                // now, we need to allocate enough memory for the entire thing
                _buffer.Add(OpCodes.PUSH); // push the value '1' onto the stack...
                _buffer.Add(TypeCodes.INT);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(1);
                
                for (var i = 0; i < declaration.ranks.Length; i++)
                {
                    _buffer.Add(OpCodes.LOAD);
                    _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                    _buffer.Add(OpCodes.MUL);
                }
                
                var sizeOfElement = arrayVar.byteSize;
                AddPushInt(_buffer, sizeOfElement);
                
                _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
                _buffer.Add(OpCodes.STORE);
                _buffer.Add(arrayVar.registerAddress);
            }
            
            
            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.
        }

        public void PushAddress(ArrayIndexReference arrayRefNode)
        {
            if (!_arrayVarToReg.TryGetValue(arrayRefNode.variableName, out var compiledArrayVar))
            {
                throw new Exception("Compiler: cannot access array since it not declared" +
                                    arrayRefNode.variableName);
            }

            var sizeOfElement = compiledArrayVar.byteSize;

   
            // load the size up
            // _buffer.Add(OpCodes.PUSH); // push the length
            // _buffer.Add(TypeCodes.INT);
            // _buffer.Add(0);
            // _buffer.Add(0);
            // _buffer.Add(0);
            // _buffer.Add(sizeOfElement);

            for (var i = 0; i < arrayRefNode.rankExpressions.Count; i++)
            {
                _buffer.Add(OpCodes.LOAD); // load the multiplier factor for the term
                _buffer.Add(compiledArrayVar.rankIndexScalerRegisterAddresses[i]);

                var expr = arrayRefNode.rankExpressions[i];
                Compile(expr); // load the expression index

                _buffer.Add(OpCodes.MUL);

                if (i > 0)
                {
                    _buffer.Add(OpCodes.ADD);
                }
            }

            // get the size of the element onto the stack
            AddPushInt(_buffer, sizeOfElement);
            // _buffer.Add(OpCodes.PUSH); // push the length
            // _buffer.Add(TypeCodes.INT);
            // _buffer.Add(0);
            // _buffer.Add(0);
            // _buffer.Add(0);
            // _buffer.Add(sizeOfElement);

            // multiply the size of the element, and the index, to get the offset into the memory
            _buffer.Add(OpCodes.MUL);

            // load the array's ptr onto the stack, this is for the math of the offset
            _buffer.Add(OpCodes.LOAD);
            _buffer.Add(compiledArrayVar.registerAddress);

            // add the offset to the original pointer to get the write location
            _buffer.Add(OpCodes.ADD);

        }

        public void Compile(AssignmentStatement assignmentStatement)
        {
            /*
             * in order to assign, we need to know what we are assigning two, and find the correct place to put the result.
             *
             * If it is a simple variable, then it lives on a local register.
             * If it is an array, then it lives in memory.
             */

            
            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);

            switch (assignmentStatement.variable)
            {
                case ArrayIndexReference arrayRefNode:
                    if (!_arrayVarToReg.TryGetValue(arrayRefNode.variableName, out var compiledArrayVar))
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
                    
                    if (!_varToReg.TryGetValue(variableRefNode.variableName, out var compiledVar))
                    {
                        // // ?
                        var tc = VmUtil.GetTypeCode(variableRefNode.DefaultTypeByName);
                        compiledVar = _varToReg[variableRefNode.variableName] = new CompiledVariable
                        {
                            registerAddress = (byte)(registerCount++),
                            name = variableRefNode.variableName,
                            typeCode = tc,
                            byteSize = TypeCodes.GetByteSize(tc)
                        };
                    }
            
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledVar.typeCode);
    
                    // store the value of the expression&cast in the desired register.
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(compiledVar.registerAddress);
                    break;
                case StructFieldReference fieldReferenceNode:

                    switch (fieldReferenceNode.left)
                    {
                        case ArrayIndexReference arrayRefNode:
                            
                            // we need to find the start index of the array element,
                            // and then add the offset for the field access part (the right side)
                            if (!_arrayVarToReg.TryGetValue(arrayRefNode.variableName, out var compiledLeftArrayVar))
                            {
                                throw new Exception("Compiler: cannot access array since it not declared" +
                                                    arrayRefNode.variableName);
                            }
                            
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

                            var rightType = compiledLeftArrayVar.structType;
                            ComputeStructOffsets(rightType, fieldReferenceNode.right, out var rightOffset, out var rightLength, out _);

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
                            if (!_varToReg.TryGetValue(variableRef.variableName, out compiledVar))
                            {
                                throw new Exception("compiler exception! the referenced variable has not been declared yet " +
                                                    variableRef.variableName);
                            }
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                            
                            // load up the compiled type info 
                            var type = _types[compiledVar.structType];
                            ComputeStructOffsets(type, fieldReferenceNode.right, out var offset, out var length, out _);

                            // push the length of the write segment
                            AddPushInt(_buffer, length);
                            
                            // load the base address of the variable
                            _buffer.Add(OpCodes.LOAD);
                            _buffer.Add(compiledVar.registerAddress);
                            
                            // load the offset of the right side
                            AddPushInt(_buffer, offset);
                            
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
        
        public void Compile(IExpressionNode expr)
        {
            // CompiledVariable compiledVar = null;
            switch (expr)
            {
                case CommandExpression commandExpr:
                    Compile(new CommandStatement
                    {
                        args = commandExpr.args,
                        command = commandExpr.command,
                        startToken = commandExpr.startToken,
                        endToken = commandExpr.endToken
                    });
                    break;
                case LiteralStringExpression literalString:
                    
                    // allocate some memory for a string...
                    var str = literalString.value;
                    var strSize = str.Length * TypeCodes.GetByteSize(TypeCodes.INT);
                    
                    // push the string data...
                    for (var i = str.Length - 1; i >= 0; i--)
                    {
                        var c = (uint)str[i];
                        AddPushUInt(_buffer, c, includeTypeCode:false);
                    }
                    
                    AddPushInt(_buffer, strSize); // SIZE, <Data>
                    
                    // this one will get used by the Write call
                    _buffer.Add(OpCodes.DUPE); // SIZE, SIZE, <Data>

                    // allocate a ptr to the stack
                    _buffer.Add(OpCodes.ALLOC); // PTR, SIZE, <Data>
                    
                    _buffer.Add(OpCodes.WRITE_PTR); // consume the ptr, then the length, then the data
                    
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(TypeCodes.STRING);
                    break;
                case LiteralRealExpression literalReal:
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.REAL);
                    var realValue = BitConverter.GetBytes(literalReal.value);
                    for (var i = realValue.Length - 1; i >= 0; i--)
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

                    if (!_arrayVarToReg.TryGetValue(arrayRef.variableName, out var arrayVar))
                    {
                        throw new Exception("compiler exception! the referenced array has not been declared yet " +
                                            arrayRef.variableName);
                    }
                    
                    var sizeOfElement = arrayVar.byteSize;

                    
                    // load the size up
                    AddPushInt(_buffer, sizeOfElement);

                    PushAddress(arrayRef);

                    // load the index onto the stack
                    // for (var i = 0; i < arrayRef.rankExpressions.Count ; i++)
                    // {
                    //     // load the multiplier factor for the term
                    //     _buffer.Add(OpCodes.LOAD); 
                    //     _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]);
                    //
                    //     // load the expression index
                    //     var subExpr = arrayRef.rankExpressions[i];
                    //     Compile(subExpr); 
                    //     
                    //     // multiply the expression index by the multiplier factor
                    //     _buffer.Add(OpCodes.MUL);
                    //
                    //     if (i > 0)
                    //     {
                    //         // and if this isn't our first, add it to the previous value
                    //         _buffer.Add(OpCodes.ADD);
                    //     }
                    // }
                    //
                    // // get the size of the element onto the stack
                    // AddPushInt(_buffer, sizeOfElement);
                    //
                    // // multiply the size of the element, and the index, to get the offset into the memory
                    // _buffer.Add(OpCodes.MUL);
                    //
                    // // load the array's ptr onto the stack, this is for the math of the offset
                    // _buffer.Add(OpCodes.LOAD); 
                    // _buffer.Add(arrayVar.registerAddress);
                    //
                    // // add the offset to the original pointer to get the write location
                    // _buffer.Add(OpCodes.ADD);
                    
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

                            if (!_varToReg.TryGetValue(variableRef.variableName, out var typeCompiledVar))
                            {
                                throw new Exception(
                                    "compiler exception! the referenced variable has not been declared yet " +
                                    variableRef.variableName);
                            }

                            if (!_types.TryGetValue(typeCompiledVar.structType, out var type))
                            {
                                throw new Exception("Unknown type reference " + type);
                            }

                            ComputeStructOffsets(type, structRef.right, out var readOffset, out var readLength, out var readTypeCode);
                            
                            // push the size of the read operation
                            AddPushInt(_buffer, readLength);
                            
                            // push the read offset, so that we can add it to the ptr
                            AddPushInt(_buffer, readOffset);
                            
                            // push the ptr of the variable, and cast it to an int for easy math
                            _buffer.Add(OpCodes.LOAD);
                            _buffer.Add(typeCompiledVar.registerAddress);
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.INT);
                            
                            // add those two op codes back together...
                            _buffer.Add(OpCodes.ADD);
                            
                            // read the summed ptr, then the length
                            _buffer.Add(OpCodes.READ);
                            
                            // we need to inject the type-code back into the stack, since it doesn't exist in heap
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(readTypeCode);
                            
                            break;
                        case ArrayIndexReference arrayRefNode:
                            if (!_arrayVarToReg.TryGetValue(arrayRefNode.variableName, out var leftArrayVar))
                            {
                                throw new Exception("compiler exception! the referenced array has not been declared yet " +
                                                    arrayRefNode.variableName);
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
                    // emit the read from register
                    if (!_varToReg.TryGetValue(variableRef.variableName, out var compiledVar))
                    {
                        throw new Exception("compiler exception! the referenced variable has not been declared yet " +
                                            variableRef.variableName);
                    }
                    
                    _buffer.Add(OpCodes.LOAD);
                    _buffer.Add(compiledVar.registerAddress);
                    
                    break;
                case BinaryOperandExpression op:
                    
                    // TODO: At this point, we could decide _which_ add/mul method to call given the type.
                    // if both lhs and rhs are ints, then we could emit an IADD, and don't need to emit the type codes ahead of time.
                    
                    Compile(op.lhs);
                    Compile(op.rhs);

                    switch (op.operationType)
                    {
                        case OperationType.Add:
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.Mult:
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case OperationType.Divide:
                            _buffer.Add(OpCodes.DIVIDE);
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
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
        }

        private static void AddPush(List<byte> buffer, byte[] value, byte typeCode)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(typeCode);
            for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
        private static void AddPushInt(List<byte> buffer, int x)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(TypeCodes.INT);
            var value = BitConverter.GetBytes(x);
            for (var i = value.Length - 1; i >= 0; i--)
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
            for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
    }
}