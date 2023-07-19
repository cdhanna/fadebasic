using System;
using System.Collections.Generic;

namespace DarkBasicYo.Ast
{

    public interface IExpressionNode : IAstNode
    {

    }

    public enum OperationType
    {
        Add,
        Subtract,
        Divide,
        Mult,
        LessThan,
        GreaterThan
    }

    public static class OperationUtil
    {
        public static readonly Dictionary<LexemType, OperationType> _lexemToOperation =
            new Dictionary<LexemType, OperationType>
            {
                [LexemType.OpPlus] = OperationType.Add,
                [LexemType.OpDivide] = OperationType.Divide,
                [LexemType.OpMinus] = OperationType.Subtract,
                [LexemType.OpMultiply] = OperationType.Mult,
                [LexemType.OpGt] = OperationType.GreaterThan,
                [LexemType.OpLt] = OperationType.LessThan,
            };

        public static OperationType Convert(Token token)
        {
            if (!_lexemToOperation.TryGetValue(token.type, out var opType))
            {
                throw new ParserException("Uknown operation", token);
            }

            return opType;
        }
    }

    public class BinaryOperandExpression : AstNode, IExpressionNode
    {
        public IExpressionNode lhs, rhs;
        public OperationType operationType;

        public BinaryOperandExpression(Token start, Token end, Token op, IExpressionNode lhs, IExpressionNode rhs)
            : base(start, end)
        {
            this.lhs = lhs;
            this.rhs = rhs;
            this.operationType = OperationUtil.Convert(op);
           
        }

        protected override string GetString()
        {
            return $"{operationType.ToString().ToLowerInvariant()} {lhs},{rhs}";
        }
    }

    public class LiteralIntExpression : AstNode, IExpressionNode
    {
        public int value;

        public LiteralIntExpression(Token token) : base(token)
        {
            if (!int.TryParse(token.raw, out value))
            {
                throw new Exception("Parser exception! Expected int, but found " + token.raw);
            }
        }

        protected override string GetString()
        {
            return value.ToString();
        }
    }


    public class LiteralRealExpression : AstNode, IExpressionNode
    {
        public float value;

        public LiteralRealExpression(Token token) : base(token)
        {
            if (!float.TryParse(token.raw, out value))
            {
                throw new Exception("Parser exception! Expected float, but found " + token.raw);
            }
        }

        protected override string GetString()
        {
            return startToken.raw;
        }
    }
}