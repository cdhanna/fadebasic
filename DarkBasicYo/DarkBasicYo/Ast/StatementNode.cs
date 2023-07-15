namespace DarkBasicYo.Ast;

public interface IStatementNode
{
}

public class CommandStatement : AstNode, IStatementNode
{
    public CommandDescriptor command;
    public List<IExpressionNode> args = new List<IExpressionNode>();
    
    protected override string GetString()
    {
        return $"{command.command} {string.Join(",", args.Select(x => x.ToString()))}";
    }
}

public class AssignmentStatement : AstNode, IStatementNode
{
    public IVariableNode variable;
    public IExpressionNode expression;

    public AssignmentStatement()
    {
        
    }
    
    protected override string GetString()
    {
        return $"= {variable},{expression}";
    }
}