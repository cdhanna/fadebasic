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
        public List<IStatementNode> statements = new List<IStatementNode>();
        public List<LabelDeclarationNode> labels = new List<LabelDeclarationNode>();
        public List<FunctionStatement> functions = new List<FunctionStatement>();

        public TestNode()
        {
        }

        protected override string GetString()
        {
            var prefix = isAbstract ? "abstract test" : "test";
            var fromClause = fromParent != null ? $" from {fromParent}" : "";
            return $"{prefix} {name}{fromClause} ({string.Join(",", statements.Select(x => x.ToString()))})";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var statement in statements) yield return statement;
            foreach (var function in functions) yield return function;
        }

        public string Trivia { get; set; }
    }
}
