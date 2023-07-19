using System;
using System.Collections.Generic;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Virtual
{
    public class Compiler
    {
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;

        public Compiler()
        {
            
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
                case BinaryOperandExpression op:
                    
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
                        default:
                            throw new NotImplementedException("unknown compiled op code");
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
        }
    }
}