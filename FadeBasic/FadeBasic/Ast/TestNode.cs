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

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var child in testProgram.IterateChildNodes()) yield return child;
        }

        public string Trivia { get; set; }
    }
}
