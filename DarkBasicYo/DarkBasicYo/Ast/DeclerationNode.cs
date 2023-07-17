namespace DarkBasicYo.Ast
{


    public class DeclarationStatement : AstNode, IStatementNode
    {
        public string variable;
        public TypeReferenceNode type;

        protected override string GetString()
        {
            return $"decl {variable},{type}";
        }
    }
}