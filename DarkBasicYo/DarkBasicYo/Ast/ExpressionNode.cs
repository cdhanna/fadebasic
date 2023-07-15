namespace DarkBasicYo.Ast;

public interface IExpressionNode
{
    
}

public class LiteralIntExpression : AstNode, IExpressionNode
{
    public int value;
    public LiteralIntExpression(Token token) : base(token)
    {
        if (!int.TryParse(token.raw, out value))
        {
            throw new Exception("Parser exception! Expected int, but found " + token.raw);
        }
    }

    protected override string GetString()
    {
        return value.ToString();
    }
}