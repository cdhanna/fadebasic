namespace DarkBasicYo.Ast
{

    public abstract class AstNode
    {
        public Token startToken;
        public Token endToken;

        protected AstNode()
        {

        }

        protected AstNode(Token start, Token end)
        {
            startToken = start;
            endToken = end;
        }

        protected AstNode(Token token) : this(token, token)
        {

        }

        public override string ToString()
        {
            return $"({GetString()})";
        }

        protected abstract string GetString();

    }
}