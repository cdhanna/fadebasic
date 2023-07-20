using System;
using System.Collections.Generic;
using System.Linq.Expressions;
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
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;
        public int registerCount;

        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        public Compiler()
        {
            
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
                default:
                    throw new Exception("compiler exception: unhandled statement node");
            }
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
            if (!_varToReg.TryGetValue(assignmentStatement.variable.variableName, out var compiledVar))
            {
                // // ?
                var tc = VmUtil.GetTypeCode(assignmentStatement.variable.DefaultTypeByName);
                compiledVar = _varToReg[assignmentStatement.variable.variableName] = new CompiledVariable
                {
                    registerAddress = (byte)(registerCount++),
                    name = assignmentStatement.variable.variableName,
                    typeCode = tc,
                    byteSize = TypeCodes.GetByteSize(tc)
                };
            }
            
            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);

            _buffer.Add(OpCodes.CAST);
            _buffer.Add(compiledVar.typeCode);
    
            _buffer.Add(OpCodes.STORE);
            _buffer.Add(compiledVar.registerAddress);
            
            // _buffer.Add((byte)addr);
        }
        
        
        public void Compile(IExpressionNode expr)
        {
            switch (expr)
            {
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