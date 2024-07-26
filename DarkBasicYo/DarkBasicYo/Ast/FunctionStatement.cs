using System;
using System.Collections.Generic;
using System.Linq;

namespace DarkBasicYo.Ast
{
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

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return variable;
            yield return type;
        }
    }

    public class FunctionReturnStatement : AstNode, IStatementNode
    {
        public IExpressionNode returnExpression;

        public FunctionReturnStatement(Token startToken, IExpressionNode expressionNode)
        {
            this.startToken = startToken;
            this.returnExpression = expressionNode;
            endToken = expressionNode.EndToken;
        }
        protected override string GetString()
        {
            return $"retfunc {returnExpression}";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return returnExpression;
        }
    }
    
    public class FunctionStatement : AstNode, IStatementNode
    {
        public string name;
        public Token nameToken;
        public List<ParameterNode> parameters = new List<ParameterNode>();
        public List<IStatementNode> statements = new List<IStatementNode>();

        public FunctionStatement()
        {
            
        }
        
        protected override string GetString()
        {
            return $"func {name} ({string.Join(",", parameters.Select(x => x.ToString()))}),({string.Join(",", statements.Select(x => x.ToString()))})";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var parameter in parameters) yield return parameter;
            foreach (var statement in statements) yield return statement;

        }
    }
}