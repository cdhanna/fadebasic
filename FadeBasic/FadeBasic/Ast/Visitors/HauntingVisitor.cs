using System.Collections.Generic;

namespace FadeBasic.Ast.Visitors
{
    public static class HauntingVisitor
    {
        public static void AddHaunting(this ProgramNode program, ParseOptions options)
        {
            
            CheckStatements(program.statements);
        }

        public static void CheckStatements(IList<IStatementNode> statements)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                switch (statement)
                {
                    case AssignmentStatement assignmentStatement:
                        HandleStatement(assignmentStatement);
                        break;
                }
            }
        }

        public static void HandleExpression(IExpressionNode expressionNode)
        {
            switch (expressionNode)
            {
                case VariableRefNode variableRefNode:
                    break;
                default:
                    break;
            }
        }

        public static void HandleStatement(AssignmentStatement assignmentStatement)
        {
            HandleExpression(assignmentStatement.expression);
            assignmentStatement.variable.TransitiveFlags |= assignmentStatement.expression.TransitiveFlags;
        }
    }
}