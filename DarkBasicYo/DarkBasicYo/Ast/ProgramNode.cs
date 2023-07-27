using System.Collections.Generic;
using System.Linq;

namespace DarkBasicYo.Ast
{
    public class ProgramNode : AstNode
    {
        public ProgramNode(Token start) : base(start)
        {

        }

        public List<IStatementNode> statements = new List<IStatementNode>();
        public List<TypeDefinitionStatement> typeDefinitions = new List<TypeDefinitionStatement>();

        protected override string GetString()
        {
            List<IStatementNode> allStatements = new List<IStatementNode>();
            allStatements.AddRange(typeDefinitions);
            allStatements.AddRange(statements);
            return $"{string.Join(",", allStatements.Select(x => x.ToString()))}";
        }
    }
}