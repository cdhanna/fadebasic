using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{

    public interface IVariableNode : IExpressionNode
    {

    }
    public class DeReference : AstNode, IVariableNode
    {
        public readonly IVariableNode ptrExpression;

        public DeReference(IVariableNode ptrExpression, Token start)
        {
            this.ptrExpression = ptrExpression;
            startToken = start;
            endToken = this.ptrExpression.EndToken;
        }
        protected override string GetString()
        {
            return $"deref {ptrExpression}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            ptrExpression?.Visit(onVisit, onExit);
        }
    }
    
    public class StructFieldReference : AstNode, IVariableNode
    {
        public IVariableNode left;
        public IVariableNode right;
        
        
        protected override string GetString()
        {
            return $"{left}.{right}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            left?.Visit(onVisit, onExit);
            right?.Visit(onVisit, onExit);
        }
    }
    
    public class ArrayIndexReference : AstNode, IVariableNode
    {
        public string variableName;
        public List<IExpressionNode> rankExpressions = new List<IExpressionNode>();

        public ArrayIndexReference()
        {
            
        }
        protected override string GetString()
        {
            return $"ref {variableName}[{string.Join(",", rankExpressions.Select(x => x.ToString()))}]";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var rankExpr in rankExpressions) rankExpr?.Visit(onVisit, onExit);
        }

    }

    public class VariableRefNode : AstNode, IVariableNode, IAstVisitable
    {
        public string variableName;
        public string VariableNameCaseSensitive => startToken.raw;

        public VariableRefNode(Token token, string variableName) : base(token)
        {
            this.variableName = variableName;
        }
        public VariableRefNode(Token token) : base(token)
        {
            variableName = token.caseInsensitiveRaw;
        }

        public VariableType DefaultTypeByName
        {
            get
            {
                switch (base.startToken.type)
                {
                    case LexemType.VariableReal:
                        return VariableType.Float;
                    case LexemType.VariableGeneral:
                        return VariableType.Integer;
                    case LexemType.VariableString:
                        return VariableType.String;
                    default:
                        throw new Exception("Unknown variable type");
                }
            }
        }

        protected override string GetString()
        {
            return $"ref {variableName}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }

    }
}