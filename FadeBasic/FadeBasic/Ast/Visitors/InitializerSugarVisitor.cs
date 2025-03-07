using System.Collections.Generic;

namespace FadeBasic.Ast.Visitors
{
    public static class InitializerSugarVisitor
    {

        static void ApplyStatements(List<IStatementNode> statements)
        {
            for (var i = 0; i < statements.Count; i++)
            {
                var statement = statements[i];

                switch (statement)
                {
                    case DeclarationStatement decl:
                        ApplyDecl(decl, i, statements);
                        break;
                    case AssignmentStatement assignment:
                        ApplyAssign(assignment, i, statements);
                        break;
                }
                
            } 
        }

        static (IVariableNode outputLeft, IVariableNode outputRight) ReBalance(IVariableNode left, IVariableNode right)
        {
            switch (left)
            {
                case StructFieldReference leftStructRef:

                    var subLeft = leftStructRef.left;
                    var subRight = leftStructRef.right;

                    var (balancedLeft, balancedRight) = ReBalance(subLeft, subRight);

                    var newRight = new StructFieldReference
                    {
                        startToken = subRight.StartToken,
                        endToken = right.EndToken,
                        left = balancedRight,
                        right = right
                    };
                    return (balancedLeft, newRight);
                    
                    break;
                default:
                    return (left, right);
            }
        }
        
        static void ApplyAssign(AssignmentStatement assignment, int index, List<IStatementNode> statements)
        {
            if (!(assignment.expression is InitializerExpression init)) return;

            assignment.expression = new DefaultValueExpression
            {
                startToken = assignment.startToken, 
                endToken = assignment.endToken
            };
            
            for (var i = init.assignments.Count - 1; i >= 0; i--)
            {
                var subAssignment = init.assignments[i];
                
                
                // need to re-balance. if left is already a struct-field-reference, then must dig in.

                var (left, right) = ReBalance(assignment.variable, subAssignment.variable);
                
                subAssignment.variable = new StructFieldReference
                {
                    startToken = subAssignment.startToken,
                    endToken = subAssignment.endToken,
                    Errors = subAssignment.Errors,
                    
                    // TODO: this probably isn't right?
                    // left = new VariableRefNode(assignment.startToken, subAssignment.variable),
                    // right = assignment.variable
                    left = left,
                    right = right
                };
                statements.Insert(index + 1, subAssignment);
            }
        }
        
        static void ApplyDecl(DeclarationStatement decl, int index, List<IStatementNode> statements)
        {
            if (!(decl.initializerExpression is InitializerExpression init)) return;
            decl.initializerExpression = null; 
            for (var i = init.assignments.Count - 1; i >= 0; i--)
            {
                var assignment = init.assignments[i];
                assignment.variable = new StructFieldReference
                {
                    startToken = assignment.startToken,
                    endToken = assignment.endToken,
                    Errors = assignment.Errors,
                    
                    // TODO: this probably isn't right?
                    left = new VariableRefNode(decl.startToken, decl.variable),
                    right = assignment.variable
                };
                statements.Insert(index + 1, assignment);
            }
        }
        
        public static void AddInitializerSugar(this ProgramNode node)
        {
            ApplyStatements(node.statements);
        }
    }
}