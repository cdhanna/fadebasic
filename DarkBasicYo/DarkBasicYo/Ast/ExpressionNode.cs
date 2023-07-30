using System;
using System.Collections.Generic;
using System.Linq;

namespace DarkBasicYo.Ast
{

    public interface IExpressionNode : IAstNode
    {

    }

    public enum UnaryOperationType
    {
        Negate,
        Not
    }
    
    public enum OperationType
    {
        Add,
        Subtract,
        Divide,
        Mult,
        Mod,
        RaisePower,
        LessThan,
        GreaterThan,
        LessThanOrEqualTo,
        GreaterThanOrEqualTo,
        EqualTo,
        NotEqualTo,
        And,
        Or
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
                [LexemType.OpLte] = OperationType.LessThanOrEqualTo,
                [LexemType.OpGte] = OperationType.GreaterThanOrEqualTo,
                [LexemType.OpNotEqual] = OperationType.NotEqualTo,
                [LexemType.OpEqual] = OperationType.EqualTo,
                [LexemType.OpMod] = OperationType.Mod,
                [LexemType.OpPower] = OperationType.RaisePower,
                [LexemType.KeywordAnd] = OperationType.And,
                [LexemType.KeywordOr] = OperationType.Or,
            };

        public static OperationType Convert(Token token)
        {
            if (!_lexemToOperation.TryGetValue(token.type, out var opType))
            {
                throw new ParserException("Uknown operation", token);
            }

            return opType;
        }

        public static string ToString(UnaryOperationType type)
        {
            switch (type)
            {
                case UnaryOperationType.Negate:
                    return "neg";
                case UnaryOperationType.Not:
                    return "!";
                default:
                    throw new NotImplementedException("unknown unary operation string");
            }
        }

        public static string ToString(OperationType type)
        {
            switch (type)
            {
                case OperationType.Add:
                    return "+";
                case OperationType.Subtract:
                    return "-";
                case OperationType.Divide:
                    return "/";
                case OperationType.Mult:
                    return "*";
                case OperationType.Mod:
                    return "%";
                case OperationType.GreaterThan:
                    return "?>";
                case OperationType.LessThanOrEqualTo:
                    return "?<=";
                case OperationType.GreaterThanOrEqualTo:
                    return "?>=";
                case OperationType.LessThan:
                    return "?<";
                case OperationType.EqualTo:
                    return "?=";
                case OperationType.NotEqualTo:
                    return "?!=";
                case OperationType.RaisePower:
                    return "^";
                case OperationType.And:
                    return "and";
                case OperationType.Or:
                    return "or";
                default:
                    throw new NotImplementedException("no string value for " + type);
            }
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

    public class UnaryOperationExpression : AstNode, IExpressionNode
    {
        public UnaryOperationType operationType;
        public IExpressionNode rhs;
        public UnaryOperationExpression(UnaryOperationType op, IExpressionNode rhs, Token start, Token end)
        {
            startToken = start;
            endToken = end;
            operationType = op;
            this.rhs = rhs;
        }
        protected override string GetString()
        {
            return $"{OperationUtil.ToString(operationType)} {rhs}";
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

        public BinaryOperandExpression()
        {
        }

        protected override string GetString()
        {
            return $"{OperationUtil.ToString(operationType)} {lhs},{rhs}";
        }
    }

    public class DereferenceExpression : AstNode, IExpressionNode
    {
        public IVariableNode expression;

        public DereferenceExpression(IVariableNode expression, Token startToken)
        {
            this.expression = expression;
            this.startToken = startToken;
            this.endToken = this.expression.EndToken;
        }
        
        protected override string GetString()
        {
            return $"derefExpr {expression}";
        }
    }
    
    public class AddressExpression : AstNode, IExpressionNode
    {
        public IVariableNode variableNode;
        
        public AddressExpression(IVariableNode variableNode, Token startToken)
        {
            this.variableNode = variableNode;
            this.startToken = startToken;
            this.endToken = this.variableNode.EndToken;
        }
        
        protected override string GetString()
        {
            return $"addr {variableNode}";
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

        public LiteralIntExpression(Token token, int value) : base(token)
        {
            this.value = value;
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