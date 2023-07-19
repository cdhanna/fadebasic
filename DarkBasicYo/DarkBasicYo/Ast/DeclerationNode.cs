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

        protected override string GetString()
        {
            return $"decl {scopeType.ToString().ToLowerInvariant()},{variable},{type}";
        }
    }
}