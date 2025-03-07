using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast
{
    public class InitializerExpression : AstNode, IExpressionNode
    {
        public List<AssignmentStatement> assignments = new List<AssignmentStatement>();
        
        
        protected override string GetString()
        {
            return $"init ({string.Join(",", assignments.Select(x => x.ToString()))})";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            foreach (var x in assignments)
                yield return x;
        }
    }
}