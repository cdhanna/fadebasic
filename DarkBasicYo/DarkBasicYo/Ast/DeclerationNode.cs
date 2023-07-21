using System.Collections.Generic;

namespace DarkBasicYo.Ast
{

    public enum DeclarationScopeType
    {
        Default,
        Local,
        Global
    }

    public class DeclarationStatement : AstNode, IStatementNode
    {
        public string variable;
        public TypeReferenceNode type;
        public DeclarationScopeType scopeType;

        public List<int> dimensionRanks = new List<int>();

        protected override string GetString()
        {
            if (dimensionRanks.Count == 0)
            {
                return $"decl {scopeType.ToString().ToLowerInvariant()},{variable},{type}";
            }

            return $"dim {scopeType.ToString().ToLowerInvariant()},{variable},{type},({string.Join(",", dimensionRanks)})";
        }
    }
}