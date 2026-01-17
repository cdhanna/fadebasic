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
        public List<LabelDefinition> labels = new List<LabelDefinition>();
        protected override string GetString()
        {
            List<IStatementNode> allStatements = new List<IStatementNode>();
            // allStatements.AddRange(labels);
            allStatements.AddRange(typeDefinitions);
            allStatements.AddRange(statements);
            allStatements.AddRange(functions);
            return $"{string.Join(",", allStatements.Select(x => x.ToString()))}";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var statement in statements)
            {
                yield return statement;
            }
            foreach (var function in functions)
            {
                yield return function;
            }
            foreach (var type in typeDefinitions)
            {
                yield return type;
            }
        }
    }
}