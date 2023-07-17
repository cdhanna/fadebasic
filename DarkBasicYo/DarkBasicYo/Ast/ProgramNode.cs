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

        protected override string GetString()
        {
            return $"{string.Join(",", statements.Select(x => x.ToString()))}";
        }
    }
}