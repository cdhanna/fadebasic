using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DarkBasicYo.Virtual;

namespace DarkBasicYo.Ast
{

    public interface IStatementNode : IAstNode, IAstVisitable
    {
    }

    public class NoOpStatement : AstNode, IStatementNode
    {
        protected override string GetString()
        {
            return "noop";
        }

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }
    }

    public class TypeDefinitionMember : AstNode, IAstVisitable
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return name;
            yield return type;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return name;
            foreach (var decl in declarations)
                yield return decl;
        }
    }

    public class EndProgramStatement : AstNode, IStatementNode
    {
        public EndProgramStatement(Token token) : base(token){}
        protected override string GetString()
        {
            return "end";
        }

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }
    }
    
    public class GotoStatement : AstNode, IStatementNode
    {
        public string label;
        public GotoStatement(Token startToken, Token labelToken) : base(startToken, labelToken)
        {
            label = labelToken.caseInsensitiveRaw;
        }

        protected override string GetString()
        {
            return $"goto {label}";
        }

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return expression;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }
    }


    public class GoSubStatement : AstNode, IStatementNode
    {
        public string label;
        public GoSubStatement(Token startToken, Token labelToken) : base(startToken, labelToken)
        {
            label = labelToken.caseInsensitiveRaw;
        }

        protected override string GetString()
        {
            return $"gosub {label}";
        }

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var arg in args) yield return arg;

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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return variable;
            yield return expression;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var statement in statements) yield return statement;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return variableNode;
            yield return startValueExpression;
            yield return endValueExpression;
            yield return stepValueExpression;
            foreach (var statement in statements) yield return statement;

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
        
        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return condition;
            foreach (var statement in statements) yield return statement;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return condition;
            foreach (var statement in statements) yield return statement;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return expression;
            if (cases != null) foreach (var caseInstance in cases)
                yield return caseInstance;
            yield return defaultCase;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var value in values)
                yield return value;
            foreach (var statement in statements)
                yield return statement;
        }
    }

    public class DefaultCaseStatement : AstNode, IStatementNode
    {
        public List<IStatementNode> statements ;
        protected override string GetString()
        {
            return $"case default ({string.Join(",", statements.Select((x => x.ToString())))})";
        }

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var statement in statements)
                yield return statement;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield return condition;
            foreach (var statement in positiveStatements)
                yield return statement;
            foreach (var statement in negativeStatements)
                yield return statement;
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

        public IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }
    }

}