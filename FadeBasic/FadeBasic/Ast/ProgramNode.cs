using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{
    public class ProgramNode : AstNode, IAstVisitable
    {
        public ProgramNode(Token start) : base(start)
        {

        }

        public Scope scope;
        public List<IStatementNode> statements = new List<IStatementNode>();
        public List<TypeDefinitionStatement> typeDefinitions = new List<TypeDefinitionStatement>();
        public List<FunctionStatement> functions = new List<FunctionStatement>();
        public List<LabelDeclarationNode> labels = new List<LabelDeclarationNode>();
        public List<TestNode> tests = new List<TestNode>();
        // CommandCollection the parser used to resolve command names. Stashed
        // here so post-parse visitors (e.g., mock-body type validation) can
        // look up command metadata without taking it as a parameter.
        public CommandCollection commands;
        protected override string GetString()
        {
            List<IStatementNode> allStatements = new List<IStatementNode>();
            // allStatements.AddRange(labels);
            allStatements.AddRange(typeDefinitions);
            allStatements.AddRange(statements);
            allStatements.AddRange(functions);
            allStatements.AddRange(tests);
            return $"{string.Join(",", allStatements.Select(x => x.ToString()))}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var statement in statements) statement?.Visit(onVisit, onExit);
            foreach (var function in functions) function?.Visit(onVisit, onExit);
            foreach (var type in typeDefinitions) type?.Visit(onVisit, onExit);
            foreach (var test in tests) test?.Visit(onVisit, onExit);
        }
    }
}