namespace DarkBasicYo.Ast;

public interface IVariableNode
{
    
}

public class VariableLiteralIntNode : AstNode, IVariableNode
{
    public string variableName;
    
    public VariableLiteralIntNode(Token token) : base(token)
    {
        variableName = token.raw;
    }
    
    protected override string GetString()
    {
        return variableName;
    }
}