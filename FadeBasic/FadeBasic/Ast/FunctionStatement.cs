using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{

    public interface IHasTriviaNode : IAstNode
    {
        public string Trivia { get; set; }
    }
    
    public class ParameterNode : AstNode, IAstVisitable
    {
        public VariableRefNode variable;
        public ITypeReferenceNode type;

        public ParameterNode(VariableRefNode variable, ITypeReferenceNode type)
        {
            this.variable = variable;
            this.type = type;
            startToken = variable.startToken;
            endToken = type.EndToken;
        }
        
        protected override string GetString()
        {
            return $"arg {variable} as {type}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            variable?.Visit(onVisit, onExit);
            type?.Visit(onVisit, onExit);
        }
    }

    public class FunctionReturnStatement : AstNode, IStatementNode
    {
        public IExpressionNode returnExpression;

        public FunctionReturnStatement(Token startToken, IExpressionNode expressionNode)
        {
            this.startToken = startToken;
            this.returnExpression = expressionNode;
            endToken = expressionNode?.EndToken ?? startToken;
        }
        protected override string GetString()
        {
            if (returnExpression == null) return "retfunc void";
            return $"retfunc {returnExpression}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            returnExpression?.Visit(onVisit, onExit);
        }
    }
    
    public class FunctionStatement : AstNode, IStatementNode, IHasTriviaNode
    {
        public const string REGION_TOP_LEVEL = null; // a top level function.
        
        public string name;
        public Token nameToken;
        public string region = REGION_TOP_LEVEL; // a null 
        public List<ParameterNode> parameters = new List<ParameterNode>();
        public List<IStatementNode> statements = new List<IStatementNode>();
        public List<LabelDeclarationNode> labels = new List<LabelDeclarationNode>();
        public bool hasNoReturnExpression;

        public FunctionStatement()
        {
            
        }
        
        protected override string GetString()
        {
            return $"func {name} ({string.Join(",", parameters.Select(x => x.ToString()))}),({string.Join(",", statements.Select(x => x.ToString()))})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var parameter in parameters) parameter?.Visit(onVisit, onExit);
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
        }

        public string Trivia { get; set; }
    }
}