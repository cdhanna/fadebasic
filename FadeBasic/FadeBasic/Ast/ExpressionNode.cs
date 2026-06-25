using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FadeBasic.Virtual;

namespace FadeBasic.Ast
{

    public interface IExpressionNode : IAstNode, IAstVisitable
    {
    }

    public interface ILiteralNode : IExpressionNode
    {
        
    }

    public interface ICanHaveErrors
    {
        List<ParseError> Errors { get; }
        bool HasErrors { get; }
    }

    public enum UnaryOperationType
    {
        Negate,
        Not,
        BitwiseNot
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
        Xor,
        Or,
        Bitwise_LeftShift,
        Bitwise_RightShift,
        Bitwise_And,
        Bitwise_Or,
        Bitwise_Xor,
        Bitwise_Not,
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
                [LexemType.KeywordXor] = OperationType.Xor,
                [LexemType.KeywordOr] = OperationType.Or,
                [LexemType.OpBitwiseAnd] = OperationType.Bitwise_And,
                [LexemType.OpBitwiseNot] = OperationType.Bitwise_Not,
                [LexemType.OpBitwiseOr] = OperationType.Bitwise_Or,
                [LexemType.OpBitwiseXor] = OperationType.Bitwise_Xor,
                [LexemType.OpBitwiseLeftShift] = OperationType.Bitwise_LeftShift,
                [LexemType.OpBitwiseRightShift] = OperationType.Bitwise_RightShift,
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
                case UnaryOperationType.BitwiseNot:
                    return "..";
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
                case OperationType.Xor:
                    return "xor";
                case OperationType.Bitwise_And:
                    return "&&";
                case OperationType.Bitwise_Or:
                    return "||";
                case OperationType.Bitwise_Not:
                    return "..";
                case OperationType.Bitwise_Xor:
                    return "~~";
                case OperationType.Bitwise_RightShift:
                    return ">>";
                case OperationType.Bitwise_LeftShift:
                    return "<<";
                default:
                    throw new NotImplementedException("no string value for " + type);
            }
        }
    }

    public class CommandExpression : AstNode, IExpressionNode
    {
        public CommandInfo command;
        public List<IExpressionNode> args = new List<IExpressionNode>();
        public List<int> argMap = new List<int>();
        
        protected override string GetString()
        {
            var argString = string.Join(",", args.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(argString))
            {
                argString = " " + argString;
            }
            return $"xcall {command.name}{argString}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var arg in args) arg?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            rhs?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            lhs?.Visit(onVisit, onExit);
            rhs?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            expression?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            variableNode?.Visit(onVisit, onExit);
        }
    }

    public class DefaultValueExpression : AstNode, ILiteralNode
    {
        protected override string GetString()
        {
            return "default";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class LiteralIntExpression : AstNode, ILiteralNode
    {
        public int value;

        public LiteralIntExpression(Token token) : base(token)
        {
            int fromBase = 10;
            var strOffset = 0;
            switch (token.type)
            {
                case LexemType.LiteralOctal:
                    fromBase = 8;
                    strOffset = 2;
                    break;
                case LexemType.LiteralBinary:
                    fromBase = 2;
                    strOffset = 1;
                    break;
                case LexemType.LiteralHex:
                    fromBase = 16;
                    strOffset = 2;
                    break;
                case LexemType.LiteralInt:
                    fromBase = 10;
                    break;
            }
            
            value = Convert.ToInt32(token.caseInsensitiveRaw.Substring(strOffset), fromBase);
            
            // if (!int.TryParse(token.caseInsensitiveRaw, out value))
            // {
            //     throw new Exception("Parser exception! Expected int, but found " + token.caseInsensitiveRaw);
            // }
        }

        public LiteralIntExpression(Token token, int value) : base(token)
        {
            this.value = value;
        }

        protected override string GetString()
        {
            return value.ToString();
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }

    }


    public class LiteralRealExpression : AstNode, ILiteralNode
    {
        public float value;

        public LiteralRealExpression(Token token) : base(token)
        {
            if (!float.TryParse(token.caseInsensitiveRaw, out value))
            {
                throw new Exception("Parser exception! Expected float, but found " + token.caseInsensitiveRaw);
            }
        }

        protected override string GetString()
        {
            return startToken.caseInsensitiveRaw;
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class LiteralStringExpression : AstNode, ILiteralNode
    {
        public string value;

        public LiteralStringExpression(Token token) : base(token)
        {
            value = token.raw.Substring(1, token.raw.Length - 2); // account for quotes
        }
        public LiteralStringExpression(Token token, string value) : base(token)
        {
            this.value = value;
        }


        protected override string GetString()
        {
            return startToken.raw;
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    /// <summary>
    /// `len(<expr>)` — integer expression returning the element count of
    /// an array or the character count of a string. The inner expression
    /// must be array- or string-typed; the visitor enforces that. Element
    /// size is determined at compile time and emitted as an inline byte
    /// after the LENGTH opcode.
    /// </summary>
    public class LenExpression : AstNode, IExpressionNode
    {
        public IExpressionNode inner;

        /// <summary>
        /// Optional dimension selector: `len(arr, k)` returns the size of the
        /// k-th dimension, zero-indexed like array indexing (`len(arr, 0)` is
        /// the first dimension). Only legal when <see cref="inner"/> is an
        /// array; the visitor enforces that. May be a runtime expression — an
        /// out-of-range value is a fatal VM error (INVALID_ADDRESS), like an
        /// out-of-bounds array index.
        /// </summary>
        public IExpressionNode dimension;

        public LenExpression(Token startToken, Token endToken, IExpressionNode inner) : base(startToken, endToken)
        {
            this.inner = inner;
        }

        protected override string GetString()
        {
            return dimension == null ? $"len {inner}" : $"len {inner} {dimension}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            inner?.Visit(onVisit, onExit);
            dimension?.Visit(onVisit, onExit);
        }
    }

    /// <summary>
    /// `dims(arr)` — the highest valid dimension index of an array (rank - 1),
    /// as an int, so `for d = 0 to dims(arr)` visits every dimension via the
    /// zero-indexed `len(arr, d)`. The rank is compile-time knowledge, so this
    /// constant-folds. Only arrays are legal — the visitor enforces that.
    /// </summary>
    public class DimsExpression : AstNode, IExpressionNode
    {
        public IExpressionNode inner;

        public DimsExpression(Token startToken, Token endToken, IExpressionNode inner) : base(startToken, endToken)
        {
            this.inner = inner;
        }

        protected override string GetString()
        {
            return $"dims {inner}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            inner?.Visit(onVisit, onExit);
        }
    }

    /// <summary>
    /// `bytes(x)` — the memory size of a value in bytes, as an int.
    /// Accepts a variable of any type, a type name, or any value-producing
    /// expression:
    ///  - struct variable / type name / scalar variable → compile-time constant
    ///  - array or string variable → runtime allocation size
    ///  - other expressions (e.g. `bytes(someCall())`) → the expression is
    ///    evaluated (side effects run), its value discarded, and the size of
    ///    its parsed type is the result; string-valued expressions report
    ///    their runtime allocation size.
    /// When the argument resolves to a type name rather than a variable, the
    /// visitor records it in <see cref="resolvedTypeName"/> and the inner
    /// VariableRefNode is not treated as a variable reference.
    /// </summary>
    public class BytesExpression : AstNode, IExpressionNode
    {
        public IExpressionNode inner;

        /// <summary>
        /// Set by the scope visitor when <see cref="inner"/> is an identifier
        /// that names a declared type instead of a variable. Variables shadow
        /// type names.
        /// </summary>
        public string resolvedTypeName;

        public BytesExpression(Token startToken, Token endToken, IExpressionNode inner) : base(startToken, endToken)
        {
            this.inner = inner;
        }

        protected override string GetString()
        {
            return $"bytes {inner}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            inner?.Visit(onVisit, onExit);
        }
    }

    /// <summary>
    /// `call count <command>` — integer expression returning the number of
    /// times the host command was invoked during the current VM execution.
    /// Counts every CALL_HOST (whether mocked or not) so the user can write
    /// `assert call count save_file = 1` without having to install a mock
    /// first. Legal inside a test block.
    /// </summary>
    public class CallCountExpression : AstNode, IExpressionNode
    {
        // Full command name, lowercased (matches the lexer's
        // CommandNameTree-normalized form, like MockStatement.commandName).
        public string commandName;
        public Token commandNameToken;

        public CallCountExpression(Token startToken, Token endToken, Token nameToken) : base(startToken, endToken)
        {
            commandNameToken = nameToken;
            commandName = nameToken?.caseInsensitiveRaw;
        }

        protected override string GetString()
        {
            return $"call count {commandName}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

}