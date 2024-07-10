using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DarkBasicYo.Ast
{

    public enum DeclarationScopeType
    {
        Local,
        Global
    }

    public class DeclarationStatement : AstNode, IStatementNode
    {
        public string variable;
        public ITypeReferenceNode type;
        public DeclarationScopeType scopeType;
        public IExpressionNode[] ranks;

        public DeclarationStatement()
        {
            
        }

        public DeclarationStatement(Token startToken, VariableRefNode variableNode, IExpressionNode[] ranks)
        {
            this.variable = variableNode.variableName;
            this.type = new TypeReferenceNode(variableNode.startToken);
            this.ranks = ranks;
            scopeType = DeclarationScopeType.Global;
            this.startToken = startToken;
            endToken = variableNode.endToken;
        }

        public DeclarationStatement(Token scopeToken, DeclarationStatement otherDecl)
        {
            this.variable = otherDecl.variable;
            type = otherDecl.type;
            startToken = scopeToken;
            endToken = otherDecl.endToken;
            ranks = otherDecl.ranks.ToArray();
            Errors.AddRange(otherDecl.Errors);
            SetScope(scopeToken);
        }

        public DeclarationStatement(Token scopeToken, VariableRefNode variableNode, ITypeReferenceNode type)
        {
            this.variable = variableNode.variableName;
            this.type = type;
            startToken = scopeToken;
            endToken = variableNode.endToken;
            SetScope(scopeToken);
        }

        public DeclarationStatement(Token startToken, VariableRefNode variableNode, ITypeReferenceNode type, IExpressionNode[] ranks)
        {
            this.variable = variableNode.variableName;
            this.type = type;
            this.startToken = startToken;
            endToken = variableNode.endToken;
            this.ranks = ranks;
            scopeType = DeclarationScopeType.Global;

        }

        
        private void SetScope(Token scopeToken)
        {
            this.scopeType = DeclarationScopeType.Local;
            switch (scopeToken.caseInsensitiveRaw.ToLowerInvariant())
            {
                case "global":
                    this.scopeType = DeclarationScopeType.Global;
                    break;
                case "local":
                    this.scopeType = DeclarationScopeType.Local;
                    break;
                default:
                    throw new ParserException("scope must be 'local' or 'global'", scopeToken);
            }

        }

        protected override string GetString()
        {
            if (ranks == null || ranks.Length == 0)
            {
                return $"decl {scopeType.ToString().ToLowerInvariant()},{variable},{type}";
            }

            return $"dim {scopeType.ToString().ToLowerInvariant()},{variable},{type},({string.Join(",", ranks.Select(x => x.ToString()))})";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return type;
            if (ranks != null) foreach (var rank in ranks) yield return rank;

        }
    }
}