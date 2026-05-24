using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FadeBasic.Virtual;

namespace FadeBasic.Ast
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class TypeDefinitionMember : AstNode, IAstVisitable, IHasTriviaNode
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            name?.Visit(onVisit, onExit);
            type?.Visit(onVisit, onExit);
        }

        public string Trivia { get; set; }
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            name?.Visit(onVisit, onExit);
            foreach (var decl in declarations) decl?.Visit(onVisit, onExit);
        }
    }

    public class EndProgramStatement : AstNode, IStatementNode
    {
        public EndProgramStatement(Token token) : base(token){}
        protected override string GetString()
        {
            return "end";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class AssertStatement : AstNode, IStatementNode
    {
        public IExpressionNode condition;
        // Source-text snapshot of the asserted expression at the time of parsing.
        // For macro-expanded sites this is the post-substitution text. The runtime
        // uses this to format failure messages.
        public string sourceText;

        // Optional second arg: a string expression giving a human-readable reason
        // surfaced in the failure report. Null when not supplied.
        public IExpressionNode reason;

        public AssertStatement(Token startToken, Token endToken, IExpressionNode condition, string sourceText)
            : base(startToken, endToken)
        {
            this.condition = condition;
            this.sourceText = sourceText;
        }

        protected override string GetString()
        {
            if (reason != null) return $"assert {condition}, {reason}";
            return $"assert {condition}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            condition?.Visit(onVisit, onExit);
            reason?.Visit(onVisit, onExit);
        }
    }

    public class RuntoStatement : AstNode, IStatementNode
    {
        public string targetLabel;
        public Token targetLabelToken;

        // Optional clauses parsed from the block form (`runto :name ... endrunto`).
        // Recorded for forward-compatibility; not yet wired into the runtime.
        public IExpressionNode maxCyclesExpression;

        public RuntoStatement(Token startToken, Token endToken, Token labelToken)
            : base(startToken, endToken)
        {
            targetLabelToken = labelToken;
            targetLabel = labelToken.caseInsensitiveRaw;
        }

        protected override string GetString()
        {
            if (maxCyclesExpression != null)
            {
                return $"runto {targetLabel} max-cycles {maxCyclesExpression}";
            }
            return $"runto {targetLabel}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            maxCyclesExpression?.Visit(onVisit, onExit);
        }
    }

    /// <summary>
    /// `returns <expr>` inside a mock body. Sets the return value the mocked
    /// command produces when called. Only valid inside a mock block; the
    /// scope-error visitor enforces that.
    /// </summary>
    public class MockExitMockStatement : AstNode, IStatementNode
    {
        public IExpressionNode expression;

        public MockExitMockStatement(Token startToken, Token endToken) : base(startToken, endToken)
        {
        }

        protected override string GetString()
        {
            return $"returns {expression}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            expression?.Visit(onVisit, onExit);
        }
    }

    /// <summary>
    /// `forbid [<reason>]` inside a mock body. Causes the test to fail when
    /// the mocked command is called. The optional reason string surfaces in
    /// the failure report (mirrors `assert <cond>, "reason"`).
    /// </summary>
    public class MockForbidStatement : AstNode, IStatementNode
    {
        public IExpressionNode reason; // null when no reason was supplied

        public MockForbidStatement(Token startToken, Token endToken) : base(startToken, endToken)
        {
        }

        protected override string GetString()
        {
            return reason != null ? $"forbid {reason}" : "forbid";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            reason?.Visit(onVisit, onExit);
        }
    }

    public class MockStatement : AstNode, IStatementNode
    {
        // The full command name, e.g. "screen width". Stored as the source text
        // of the command-name token (already normalized by the lexer's
        // CommandNameTree pass).
        public string commandName;
        public Token commandNameToken;
        // Optional parameter names — `mock find pattern, list` binds the
        // command's args to locals named `pattern` and `list` inside the body.
        // Empty means anonymous (args are popped off the stack but not
        // accessible). The count must match the command's non-VmArg arg count
        // when names are given; the visitor enforces that.
        public List<VariableRefNode> parameters = new List<VariableRefNode>();
        // Optional fall-through return expression on `endmock <expr>` — the
        // value the body produces when execution reaches the closing
        // `endmock` without an earlier `exitmock`. Mirrors `endfunction
        // <expr>` for functions. Null when the user wrote bare `endmock`.
        public IExpressionNode endmockExpression;
        // Body of the mock block. Compiled as a mini-function the VM
        // dispatches to at CALL_HOST time: a scope is pushed, parameters
        // bound from the call's args, then body statements run. `returns`
        // (MockExitMockStatement) sets the return value; `forbid`
        // (MockForbidStatement) fails the test. Other test-block statements
        // (static print, local, if/then, assert) are legal here too.
        // An empty body on a void command means "suppress the call."
        public List<IStatementNode> body = new List<IStatementNode>();

        public MockStatement(Token startToken, Token endToken) : base(startToken, endToken)
        {
        }

        protected override string GetString()
        {
            var paramStr = parameters.Count > 0
                ? " " + string.Join(",", parameters.Select(p => p.variableName))
                : "";
            return $"mock {commandName}{paramStr} ({string.Join(",", body.Select(s => s.ToString()))})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var p in parameters) p?.Visit(onVisit, onExit);
            foreach (var stmt in body) stmt?.Visit(onVisit, onExit);
            endmockExpression?.Visit(onVisit, onExit);
        }
    }

    public class ClearMockStatement : AstNode, IStatementNode
    {
        // Null means "clear all mocks" (`clear mocks`).
        // Non-null is a specific command name (`clear mock screen width`).
        public string commandName;
        public Token commandNameToken;

        public ClearMockStatement(Token startToken, Token endToken) : base(startToken, endToken)
        {
        }

        protected override string GetString()
        {
            return commandName == null ? "clear mocks" : $"clear mock {commandName}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class MacroSubstitutionExpression : AstNode, IExpressionNode
    {
        public IExpressionNode innerExpression;
        public int substitutionIndex;
        public int tokenStartIndex, tokenEndIndex;
        public bool isStringify = false;
        protected override string GetString()
        {
            return $"subst ({innerExpression})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            innerExpression?.Visit(onVisit, onExit);
        }
    }

    public class MacroTokenizeStatement : AstNode, IStatementNode
    {
        public List<MacroSubstitutionExpression> substitutions = new List<MacroSubstitutionExpression>();

        public int startTokenIndex;
        public int endTokenIndex;

        public int tokenBlockIndex;
        // public List<Token> tokens;

        public MacroTokenizeStatement(Token start, Token end, List<MacroSubstitutionExpression> statements, int startTokenIndex, int endTokenIndex, int tokenBlockIndex) : base(start, end)
        {
            this.endTokenIndex = endTokenIndex;
            this.startTokenIndex = startTokenIndex;
            this.substitutions = statements;
            this.tokenBlockIndex = tokenBlockIndex;
        }
        // public MacroTokenizeStatement(Token start, Token end, List<MacroSubstitutionExpression> statements, List<Token> tokens) : base(start, end)
        // {
        //     this.tokens = tokens;
        //     this.substitutions = statements;
        // }
        
        protected override string GetString()
        {
            return $"tokenize ({string.Join(",", substitutions.Select(x => x.ToString()))})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var statement in substitutions) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            expression?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    
    public class CommandStatement : AstNode, IStatementNode
    {
        public CommandInfo command;
        public List<IExpressionNode> args = new List<IExpressionNode>();
        public List<int> argMap = new List<int>();

        public CommandStatement()
        {
            
        }
        
        protected override string GetString()
        {
            var argString = string.Join(",", args.Select(x => x.ToString()));
            if (!string.IsNullOrEmpty(argString))
            {
                argString = " " + argString;
            }
            return $"call {command.name}{argString}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var arg in args) arg?.Visit(onVisit, onExit);
        }
    }

    public class AssignmentStatement : AstNode, IStatementNode, IHasTriviaNode
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            variable?.Visit(onVisit, onExit);
            expression?.Visit(onVisit, onExit);
        }

        public string Trivia { get; set; }
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    public class SkipLoopStatement : AstNode, IStatementNode
    {
        public SkipLoopStatement(Token token) : base(token, token)
        {
            
        }
        protected override string GetString()
        {
            return "skip";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            variableNode?.Visit(onVisit, onExit);
            startValueExpression?.Visit(onVisit, onExit);
            endValueExpression?.Visit(onVisit, onExit);
            stepValueExpression?.Visit(onVisit, onExit);
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
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
        
        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            condition?.Visit(onVisit, onExit);
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            condition?.Visit(onVisit, onExit);
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            expression?.Visit(onVisit, onExit);
            if (cases != null) foreach (var caseInstance in cases) caseInstance?.Visit(onVisit, onExit);
            defaultCase?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var value in values) value?.Visit(onVisit, onExit);
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
        }
    }

    public class DefaultCaseStatement : AstNode, IStatementNode
    {
        public List<IStatementNode> statements ;
        protected override string GetString()
        {
            return $"case default ({string.Join(",", statements.Select((x => x.ToString())))})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            condition?.Visit(onVisit, onExit);
            foreach (var statement in positiveStatements) statement?.Visit(onVisit, onExit);
            foreach (var statement in negativeStatements) statement?.Visit(onVisit, onExit);
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit) { }
    }

    /// <summary>
    /// Represents a defer statement that executes its contained statements at the end of the current scope.
    /// </summary>
    public class DeferStatement : AstNode, IStatementNode
    {
        /// <summary>
        /// The statements to be executed at the end of the scope.
        /// </summary>
        public List<IStatementNode> statements = new List<IStatementNode>();

        /// <summary>
        /// Whether this is a single-line defer (defer statement) or block defer (defer...enddefer)
        /// </summary>
        public bool isSingleLine;

        public DeferStatement(Token start, Token end, List<IStatementNode> statements, bool isSingleLine) : base(start, end)
        {
            this.statements = statements;
            this.isSingleLine = isSingleLine;
        }

        protected override string GetString()
        {
            return $"defer ({string.Join(",", statements.Select(x => x.ToString()))})";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
        }
    }

}