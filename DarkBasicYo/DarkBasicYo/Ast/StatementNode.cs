using System.Collections.Generic;
using System.Linq;

namespace DarkBasicYo.Ast
{

    public interface IStatementNode : IAstNode
    {
    }

    public class TypeDefinitionMember : AstNode
    {
        public VariableRefNode name;
        public ITypeReferenceNode type;
        public TypeDefinitionMember(Token start, Token end, VariableRefNode name, ITypeReferenceNode type)
        {
            startToken = start;
            endToken = end;
            this.name = name;
            this.type = type;
        }
        
        protected override string GetString()
        {
            return $"{name} as {type}";
        }
    }

    public class TypeDefinitionStatement : AstNode, IStatementNode
    {
        public List<TypeDefinitionMember> declarations;
        public VariableRefNode name;
        public TypeDefinitionStatement(Token start, Token end, VariableRefNode name, List<TypeDefinitionMember> declarations)
        {
            startToken = start;
            endToken = end;
            this.name = name;
            this.declarations = declarations;
        }
        protected override string GetString()
        {
            return $"type {name.variableName} {string.Join(",", declarations.Select(x => x.ToString()))}";
        }
    }

    public class EndProgramStatement : AstNode, IStatementNode
    {
        public EndProgramStatement(Token token) : base(token){}
        protected override string GetString()
        {
            return "end";
        }
    }
    
    public class GotoStatement : AstNode, IStatementNode
    {
        public string label;
        public GotoStatement(Token startToken, Token labelToken) : base(startToken, labelToken)
        {
            label = labelToken.raw;
        }

        protected override string GetString()
        {
            return $"goto {label}";
        }
    }
    
    
    public class ReturnStatement : AstNode, IStatementNode
    {
        public ReturnStatement(Token startToken) : base(startToken)
        {
        }

        protected override string GetString()
        {
            return $"ret";
        }
    }


    public class GoSubStatement : AstNode, IStatementNode
    {
        public string label;
        public GoSubStatement(Token startToken, Token labelToken) : base(startToken, labelToken)
        {
            label = labelToken.raw;
        }

        protected override string GetString()
        {
            return $"gosub {label}";
        }
    }

    
    public class CommandStatement : AstNode, IStatementNode
    {
        public CommandDescriptor command;
        public List<IExpressionNode> args = new List<IExpressionNode>();

        protected override string GetString()
        {
            var argString = string.Join(",", args.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(argString))
            {
                argString = " " + argString;
            }
            return $"call {command.command}{argString}";
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

    public class WhileStatement : AstNode, IStatementNode
    {
        public IExpressionNode condition;
        public List<IStatementNode> statements = new List<IStatementNode>();

        protected override string GetString()
        {
            return $"while {condition} {string.Join(",", statements.Select(x => x.ToString()))}";
        }
    }

    public class IfStatement : AstNode, IStatementNode
    {
        public IExpressionNode condition;
        public List<IStatementNode> positiveStatements;
        public List<IStatementNode> negativeStatements;
        public IfStatement(Token start, Token end, IExpressionNode condition, List<IStatementNode> positiveStatements, List<IStatementNode> negativeStatements) : base(start, end)
        {
            this.condition = condition;
            this.positiveStatements = positiveStatements;
            this.negativeStatements = negativeStatements;
        }
        public IfStatement(Token start, Token end, IExpressionNode condition, List<IStatementNode> positiveStatements) : base(start, end)
        {
            this.condition = condition;
            this.positiveStatements = positiveStatements;
            this.negativeStatements = new List<IStatementNode>();
        }
        
        protected override string GetString()
        {
            var negativeStr = "";
            if (negativeStatements.Count > 0)
            {
                negativeStr = $" ({string.Join(",", negativeStatements)}";
            }
            
            return $"if {condition} ({string.Join(",", positiveStatements)}){negativeStr}";
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