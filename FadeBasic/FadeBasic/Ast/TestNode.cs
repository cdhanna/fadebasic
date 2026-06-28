using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{
    public class TestNode : AstNode, IStatementNode, IHasTriviaNode
    {
        public string name;
        public Token nameToken;
        public bool isAbstract;
        public string fromParent;
        public Token fromParentToken;
        // public List<IStatementNode> statements = new List<IStatementNode>();
        // public List<LabelDeclarationNode> labels = new List<LabelDeclarationNode>();
        // public List<FunctionStatement> functions = new List<FunctionStatement>();

        public ProgramNode testProgram;
        
        public TestNode()
        {
        }

        protected override string GetString()
        {
            var prefix = isAbstract ? "abstract test" : "test";
            var fromClause = fromParent != null ? $" from {fromParent}" : "";
            return $"{prefix} {name}{fromClause} {testProgram.ToString()}";
        }

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var s in testProgram.statements) s?.Visit(onVisit, onExit);
            foreach (var f in testProgram.functions) f?.Visit(onVisit, onExit);
            foreach (var t in testProgram.typeDefinitions) t?.Visit(onVisit, onExit);
            foreach (var t in testProgram.tests) t?.Visit(onVisit, onExit);
        }

        public string Trivia { get; set; }
    }
}
