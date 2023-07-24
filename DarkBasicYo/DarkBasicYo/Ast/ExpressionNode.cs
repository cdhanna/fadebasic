using System;
using System.Collections.Generic;
using System.Linq;

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

    public class CommandExpression : AstNode, IExpressionNode
    {
        public CommandDescriptor command;
        public List<IExpressionNode> args = new List<IExpressionNode>();

        protected override string GetString()
        {
            var argString = string.Join(",", args.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(argString))
            {
                argString = " " + argString;
            }
            return $"xcall {command.command}{argString}";
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

    public class DereferenceExpression : AstNode, IExpressionNode
    {
        public IExpressionNode expression;

        public DereferenceExpression(IExpressionNode expression, Token startToken)
        {
            this.expression = expression;
            this.startToken = startToken;
            this.endToken = this.expression.EndToken;
        }
        
        protected override string GetString()
        {
            return $"deref {expression}";
        }
    }
    
    public class AddressExpression : AstNode, IExpressionNode
    {
        public IExpressionNode expression;
        
        public AddressExpression(IExpressionNode expression, Token startToken)
        {
            this.expression = expression;
            this.startToken = startToken;
            this.endToken = this.expression.EndToken;
        }
        
        protected override string GetString()
        {
            return $"addr {expression}";
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

    public class LiteralStringExpression : AstNode, IExpressionNode
    {
        public string value;

        public LiteralStringExpression(Token token) : base(token)
        {
            value = token.raw.Substring(1, token.raw.Length - 2); // account for quotes
        }


        protected override string GetString()
        {
            return startToken.raw;
        }
    }
}