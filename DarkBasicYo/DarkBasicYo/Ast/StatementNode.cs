using System.Collections.Generic;
using System.Linq;

namespace DarkBasicYo.Ast
{

    public interface IStatementNode : IAstNode
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
        public VariableRefNode variable;
        public IExpressionNode expression;

        public AssignmentStatement()
        {

        }

        protected override string GetString()
        {
            return $"= {variable},{expression}";
        }
    }

    public class WhileStatement : AstNode, IStatementNode
    {
        public IExpressionNode condition;
        public List<IStatementNode> statements = new List<IStatementNode>();

        protected override string GetString()
        {
            return $"while {condition} {string.Join(",", statements.Select(x => x.ToString()))}";
        }
    }

    public class EndWhileStatement : AstNode, IStatementNode
    {
        public EndWhileStatement(Token token) : base(token)
        {
        }

        protected override string GetString()
        {
            return "endwhile";
        }
    }
}