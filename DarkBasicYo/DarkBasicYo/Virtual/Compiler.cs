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
        public int byteSize;
        public byte typeCode;
        public string name;
        public byte registerAddress;
    }
    
    public class Compiler
    {
        public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;
        public int registerCount;

        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();
        public HostMethodTable methodTable;

        private Dictionary<CommandDescriptor, int> _commandToPtr = new Dictionary<CommandDescriptor, int>();

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
            foreach (var statement in program.statements)
            {
                Compile(statement);
            }
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
                default:
                    throw new Exception("compiler exception: unhandled statement node");
            }
        }

        public void Compile(CommandStatement commandStatement)
        { 
            // TODO: save local state?
            
            // put each expression on the stack.
            foreach (var argExpr in commandStatement.args)
            {
                Compile(argExpr);
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
            _varToReg[declaration.variable] = new CompiledVariable
            {
                registerAddress = (byte)(registerCount++),
                name = declaration.variable,
                typeCode = tc,
                byteSize = TypeCodes.GetByteSize(tc)
            };

            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.
        }

        public void Compile(AssignmentStatement assignmentStatement)
        {
            /*
             * in order to assign, we need to know what we are assigning two, and find the correct place to put the result.
             *
             * If it is a simple variable, then it lives on a local register.
             * If it is an array, then it lives in memory.
             */

            var variableRefNode = assignmentStatement.variable as VariableRefNode;
            if (variableRefNode == null)
            {
                throw new NotImplementedException("We don't support this yet");
            }
            
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
            
            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);

            // always cast the expression to the correct type code; slightly wasteful, could be better.
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(compiledVar.typeCode);
    
            // store the value of the expression&cast in the desired register.
            _buffer.Add(OpCodes.STORE);
            _buffer.Add(compiledVar.registerAddress);
        }
        
        
        public void Compile(IExpressionNode expr)
        {
            switch (expr)
            {
                case CommandExpression commandExpr:
                    foreach (var argExpr in commandExpr.args)
                    {
                        Compile(argExpr);
                    }
            
                    // find the address of the method
                    if (!_commandToPtr.TryGetValue(commandExpr.command, out var commandAddress))
                    {
                        throw new Exception("compiler: could not find method address: " + commandExpr.command);
                    }
            
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.INT);
                    var bytes = BitConverter.GetBytes(commandAddress);
                    for (var i = bytes.Length -1; i >= 0; i--)
                    {
                        _buffer.Add(bytes[i]);
                    }

                    _buffer.Add(OpCodes.CALL_HOST);
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
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.INT);
                    var value = BitConverter.GetBytes(literalInt.value);
                    for (var i = value.Length - 1; i >= 0; i--)
                    {
                        _buffer.Add(value[i]);
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
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
        }
    }
}