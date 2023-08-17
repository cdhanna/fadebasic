using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DarkBasicYo.Virtual;

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

    public class ExpressionStatement : AstNode, IStatementNode
    {
        public IExpressionNode expression;
        public ExpressionStatement(IExpressionNode expression) : base(expression.StartToken, expression.EndToken)
        {
            this.expression = expression;
        }
        protected override string GetString()
        {
            return $"expr {expression}";
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
        public CommandInfo command;
        public List<IExpressionNode> args = new List<IExpressionNode>();

        protected override string GetString()
        {
            var argString = string.Join(",", args.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(argString))
            {
                argString = " " + argString;
            }
            return $"call {command.name}{argString}";
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

    public class ExitLoopStatement : AstNode, IStatementNode
    {
        public ExitLoopStatement(Token token) : base(token, token)
        {
            
        }
        protected override string GetString()
        {
            return "break";
        }
    }

    public class DoLoopStatement : AstNode, IStatementNode
    {
        public List<IStatementNode> statements;

        public DoLoopStatement(Token start, Token end, List<IStatementNode> statements) : base(start, end)
        {
            this.statements = statements;
        }
        
        protected override string GetString()
        {
            return $"do ({string.Join(",", statements.Select(x => x.ToString()))})";
        }
    }
    

    public class ForStatement : AstNode, IStatementNode
    {
        public IVariableNode variableNode;
        public IExpressionNode startValueExpression;
        public IExpressionNode endValueExpression;
        public IExpressionNode stepValueExpression;

        public List<IStatementNode> statements;

        public ForStatement(Token start, Token end, IVariableNode variable, IExpressionNode startValue,
            IExpressionNode endValue, IExpressionNode stepValue, List<IStatementNode> statements)
        {
            startToken = start;
            endToken = end;
            variableNode = variable;
            startValueExpression = startValue;
            endValueExpression = endValue;
            stepValueExpression = stepValue;
            this.statements = statements;
        }
        
        protected override string GetString()
        {
            return $"for {variableNode},{startValueExpression},{endValueExpression},{stepValueExpression},({string.Join(",", statements.Select(x => x.ToString()))})";
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
    
    public class RepeatUntilStatement : AstNode, IStatementNode
    {
        public IExpressionNode condition;
        public List<IStatementNode> statements = new List<IStatementNode>();

        protected override string GetString()
        {
            return $"repeat {condition} {string.Join(",", statements.Select(x => x.ToString()))}";
        }
    }

    public class SwitchStatement : AstNode, IStatementNode
    {
        public IExpressionNode expression;
        public List<CaseStatement> cases;
        public DefaultCaseStatement defaultCase ;
        
        protected override string GetString()
        {
            var statements = new List<IStatementNode>();
            statements.AddRange(cases);
            if (defaultCase != null )
            {
                statements.Add(defaultCase);
            }
            return $"switch {expression} ({string.Join(",", statements.Select(x => x.ToString()))})";
        }
    }

    public class CaseStatement : AstNode, IStatementNode
    {
        public List<ILiteralNode> values;
        public List<IStatementNode> statements;
        protected override string GetString()
        {
            return $"case {string.Join(",", values.Select(x => x.ToString()))} ({string.Join(",", statements.Select((x => x.ToString())))})";
        }
    }

    public class DefaultCaseStatement : AstNode, IStatementNode
    {
        public List<IStatementNode> statements ;
        protected override string GetString()
        {
            return $"case default ({string.Join(",", statements.Select((x => x.ToString())))})";
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

    public class CommentStatement : AstNode, IStatementNode
    {
        public string comment;
        public CommentStatement(Token token, string comment) : base(token, token)
        {
            this.comment = comment;
        }

        protected override string GetString()
        {
            return $"rem{comment}";
        }
    }

}