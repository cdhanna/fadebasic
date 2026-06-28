using System;
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

        protected override void VisitChildren(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit)
        {
            foreach (var x in assignments) x?.Visit(onVisit, onExit);
        }
    }
}