namespace DarkBasicYo.Ast
{
    public interface IAstNode
    {
        Token StartToken { get; }
        Token EndToken { get; }
    }
    public abstract class AstNode : IAstNode
    {
        public Token startToken;
        public Token endToken;

        public Token StartToken => startToken;
        public Token EndToken => endToken;
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