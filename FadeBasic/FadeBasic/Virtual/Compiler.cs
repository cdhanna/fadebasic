using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using FadeBasic.Ast;

namespace FadeBasic.Virtual
{
    public class CompiledVariable
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public string structType;
        public byte registerAddress;
        public bool isGlobal;
    }

    public class CompiledArrayVariable
    {
        public int byteSize;
        public byte typeCode;
        public string name;
        public CompiledType structType;
        public byte registerAddress;
        public bool isGlobal;
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

    public struct FunctionCallReplacement
    {
        public int InstructionIndex;
        public string FunctionName;
    }

    public class CompileScope
    {
        public int registerCount;
        
        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
            new Dictionary<string, CompiledArrayVariable>();

        public bool TryGetVariable(string name, out CompiledVariable variable)
        {
            return _varToReg.TryGetValue(name, out variable);
        }

        public bool TryGetArray(string name, out CompiledArrayVariable arrayVariable)
        {
            return _arrayVarToReg.TryGetValue(name, out arrayVariable);
        }
        
        public CompiledVariable Create(string name, byte typeCode, bool isGlobal)
        {
            var compileVar = new CompiledVariable
            {
                registerAddress = (byte)(registerCount++),
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
            // registerCount++;

            var x = (byte)registerCount;
            return (byte)(registerCount++);
        }
    }
    
    public class Compiler
    {
        // public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;
        // public int registerCount;

        private CompileScope globalScope;
        private Stack<CompileScope> scopeStack;

        private CompileScope scope => scopeStack.Peek();
        
        // private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        // private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
        //     new Dictionary<string, CompiledArrayVariable>();

        private Dictionary<string, CompiledType> _types = new Dictionary<string, CompiledType>();

        public HostMethodTable methodTable;

        private Dictionary<string, int> _commandToPtr = new Dictionary<string, int>();
        private Stack<List<int>> _exitInstructionIndexes = new Stack<List<int>>();

        private List<LabelReplacement> _labelReplacements = new List<LabelReplacement>();
        private Dictionary<string, int> _labelToInstructionIndex = new Dictionary<string, int>();

        private List<FunctionCallReplacement> _functionCallReplacements = new List<FunctionCallReplacement>();
        private Dictionary<string, int> _functionTable = new Dictionary<string, int>();
        
        
        public Compiler(CommandCollection commands)
        {
            // this.commands = commands;

            var methods = new CommandInfo[commands.Commands.Count];
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                // methods[i] = HostMethodUtil.BuildHostMethodViaReflection(commands.Commands[i].method);
                methods[i] = commands.Commands[i];
                _commandToPtr[commands.Commands[i].UniqueName] = i;
            }
            methodTable = new HostMethodTable
            {
                methods = methods
            };

            scopeStack = new Stack<CompileScope>();
            globalScope = new CompileScope();
            scopeStack.Push(globalScope);

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

            // TODO: inject 'end'? 
            foreach (var function in program.functions)
            {
                Compile(function);
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
                case CommentStatement _:
                    // ignore comments
                    break;
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
                case IfStatement ifStatement:
                    Compile(ifStatement);
                    break;
                case ExitLoopStatement exitStatement:
                    Compile(exitStatement);
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
                case FunctionStatement functionStatement:
                    Compile(functionStatement);
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
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP_HISTORY);
        }

        
        private void Compile(FunctionReturnStatement returnStatement)
        {
            // put the return value onto the stack
            Compile(returnStatement.returnExpression);
            
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
                Compile(fakeDecl);
                
                // and now compile up the assignment
                // var a = new AssignmentStatement();
                // Compile(a);
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
            foreach (var successStatement in forStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();

            // now to update the value of x, we need to add the stepExpr to it.
            Compile(stepAssignment); // NOTE: there could be a bug here, because we are looping on a deterministic math operation, but simulating the interpolated variable
            
            // jump back to the start
            AddPushInt(_buffer, forLoopValue);
            _buffer.Add(OpCodes.JUMP);
            
            var endJumpValue = _buffer.Count;
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
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
            }
            
        }
         
        
        private void Compile(DoLoopStatement doLoopStatement)
        {

            // first, keep track of the start of the while loop
            var whileLoopValue = _buffer.Count;
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(new List<int>());
            foreach (var successStatement in doLoopStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();

            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[i];
                }
                
            }
        }
        
        
        private void Compile(RepeatUntilStatement repeatStatement)
        {
            
            // first, keep track of the start of the while loop
            var startValue = _buffer.Count;
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(new List<int>());
            foreach (var successStatement in repeatStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            
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
            
            // now go back and fill in the success ptr
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            for (var i = 0; i < endJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[ i];
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
            foreach (var successStatement in whileStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();

            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
            // now go back and fill in the success ptr
            var successJumpBytes = BitConverter.GetBytes(successJumpValue);
            var endJumpBytes = BitConverter.GetBytes(endJumpValue);
            for (var i = 0; i < successJumpBytes.Length; i++)
            {
                // offset by 2, because of the opcode, and the type code
                _buffer[successJumpIndex + 2 + i] = successJumpBytes[i];
                _buffer[exitJumpIndex + 2 + i] = endJumpBytes[i];
                foreach (var index in exitStatementIndexes)
                {
                    _buffer[index + 2 + i] = endJumpBytes[ i];
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

            // at the end of the successful statements, we need to jump to the end
            var endJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);

            // this is where the else statements begin
            var elseJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            foreach (var elseStatement in ifStatement.negativeStatements)
            {
                Compile(elseStatement);
            }

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            
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
                        Compile(fakeDeclStatement);
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
                            AddPush(_buffer, new byte[]{regAddr}, TypeCodes.PTR_REG);
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
                            
                            // call alloc, which expects to find the length on the stack, and the ptr is returned.
                            _buffer.Add(OpCodes.ALLOC);
                    
                            // cast the ptr to a struct type-code
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.STRUCT);
                            
                            // the ptr will be stored in the register for this variable
                            // _buffer.Add(OpCodes.STORE);
                            // _buffer.Add(compiledVar.registerAddress);
                            PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
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
                _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
                // _buffer.Add(OpCodes.STORE);
                // _buffer.Add(arrayVar.registerAddress);
                PushStore(_buffer, arrayVar.registerAddress, arrayVar.isGlobal);
            }
            
            
            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.
        }

        public void PushAddress(ArrayIndexReference arrayRefNode)
        {
            if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledArrayVar))
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
                // _buffer.Add(OpCodes.LOAD); // load the multiplier factor for the term
                // _buffer.Add(compiledArrayVar.rankIndexScalerRegisterAddresses[i]);
                PushLoad(_buffer, compiledArrayVar.rankIndexScalerRegisterAddresses[i], compiledArrayVar.isGlobal);
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
            // _buffer.Add(OpCodes.LOAD);
            // _buffer.Add(compiledArrayVar.registerAddress);
            PushLoad(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);


            // add the offset to the original pointer to get the write location
            _buffer.Add(OpCodes.ADD);

        }

        static void PushStore(List<byte> buffer, byte registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.STORE_GLOBAL : OpCodes.STORE);
            buffer.Add(registerAddress);
        }
        static void PushLoad(List<byte> buffer, byte registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.LOAD_GLOBAL : OpCodes.LOAD);
            buffer.Add(registerAddress);
        }

        void CompileStructData(CompiledVariable compiledVar, bool ignoreType=true)
        {
            if (!_types.TryGetValue(compiledVar.structType, out var structType))
            {
                throw new Exception("Referencing type that does not exist yet. In assignment." + compiledVar.name + " and " + compiledVar.structType);
            }
            _buffer.Add(OpCodes.BREAKPOINT); // we don't actually want the type code to live on the heap

            if (ignoreType)
            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

            // push the size of the write operation- it is the size of the struct we happen to have!
            AddPushInt(_buffer, structType.byteSize);
                        
            // now, push the pointer where to write the data to- which, we know is the register address
            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        
            // the address is a struct ref, not an int, so for the write-command to work, we need to cast the struct to an int
            CastToInt();
            
            _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
        }

        void CastToInt()
        {
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
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
                    PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
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
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                            
                            // load up the compiled type info 
                            var type = _types[compiledVar.structType];
                            ComputeStructOffsets(type, fieldReferenceNode.right, out var offset, out var length, out _);

                            // push the length of the write segment
                            AddPushInt(_buffer, length);
                            
                            // load the base address of the variable
                            // _buffer.Add(OpCodes.LOAD);
                            // _buffer.Add(compiledVar.registerAddress);
                            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            
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
            // _buffer.Add(OpCodes.DISCARD);
            
            //
            
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
                    // for (var i = str.Length - 1; i >= 0; i--)
                    for (var i = 0 ; i < str.Length; i ++)
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

                        // PushAddress(arrayRef);
                        PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        CastToInt();
                        
                        _buffer.Add(OpCodes.BREAKPOINT);
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
                        default:
                            throw new Exception("Compiler: unsupported unary operaton " + unary.operationType);
                    }

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
                        case OperationType.And:
                            _buffer.Add(OpCodes.MUL);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.Or:
                            _buffer.Add(OpCodes.ADD);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
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