using System.Collections.Generic;

namespace FadeBasic.Ast.Visitors
{
    public static class HauntingVisitor
    {
        public static bool HasAnyGeneratedHauntedTokens(this IAstVisitable node)
        {
            bool hasFlag = false;
            node.Visit(x =>
            {
                if (!hasFlag)
                {
                    hasFlag |= x.StartToken.flags.HasFlag(TokenFlags.IsHauntedGenerated);
                    hasFlag |= x.EndToken.flags.HasFlag(TokenFlags.IsHauntedGenerated);
                }
            });
            return hasFlag;
        }
        
        public static void AddHaunting(this ProgramNode program, ParseOptions options)
        {
            
            CheckStatements(program.statements);
        }

        public static void CheckStatements(IList<IStatementNode> statements)
        {
            
            
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];
                
                statement.Visit(x =>
                {
                    
                    switch (x)
                    {
                        case AssignmentStatement assignment:
                            var hasFlag = assignment.variable.HasAnyGeneratedHauntedTokens();
                            if (hasFlag)
                            {
                                assignment.Errors.Add(new ParseError(assignment.variable, ErrorCodes.VariableUsesHaunted));
                            }
                            break;
                        case MacroTokenizeStatement tokenizeStatement:
                            if (!x.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted))
                            {
                                // do not care about unhaunted stuff. 
                                return; 
                            }
                            tokenizeStatement.Errors.Add(new ParseError(tokenizeStatement.startToken, ErrorCodes.TokenizationContainsHaunted));
                            break;
                    }
                });
                
                switch (statement)
                {
                    case AssignmentStatement assignmentStatement:
                        // HandleStatement(assignmentStatement);
                        break;
                }
            }
        }

        // public static void HandleExpression(IExpressionNode expressionNode)
        // {
        //     switch (expressionNode)
        //     {
        //         case VariableRefNode variableRefNode:
        //             break;
        //         default:
        //             break;
        //     }
        // }

        // public static void HandleStatement(AssignmentStatement assignmentStatement)
        // {
        //     HandleExpression(assignmentStatement.expression);
        //     assignmentStatement.variable.TransitiveFlags |= assignmentStatement.expression.TransitiveFlags;
        // }
    }
}