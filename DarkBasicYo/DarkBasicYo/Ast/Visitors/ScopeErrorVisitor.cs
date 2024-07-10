using System;
using System.Collections.Generic;

namespace DarkBasicYo.Ast.Visitors
{
    public static partial class ErrorVisitors
    {

        public static void AddScopeRelatedErrors(this ProgramNode program)
        {
            var scope = new Scope();
            foreach (var label in program.labels)
            {
                scope.AddLabel(label.node);
            }
            CheckStatements(program.statements, scope);
        }

        static void CheckStatements(this List<IStatementNode> statements, Scope scope)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case FunctionReturnStatement returnStatement:
                        returnStatement.returnExpression.EnsureVariablesAreDefined(scope);
                        // scope.EndFunction();
                        break;
                    case FunctionStatement functionStatement:
                        scope.BeginFunction(functionStatement.parameters);
                        CheckStatements(functionStatement.statements, scope);
                        scope.EndFunction();
                        break;
                    case DeclarationStatement decl:
                        scope.AddDeclaration(decl);
                        break;
                    case AssignmentStatement assignment:
                        scope.AddAssignment(assignment);
                        // assignment.variable.EnsureVariablesAreDefined(scope);
                        assignment.expression.EnsureVariablesAreDefined(scope);

                        switch (assignment.variable)
                        {
                            case StructFieldReference fieldRef:
                                fieldRef.EnsureStructField(scope);
                                break;
                            default:
                                break;
                        }
                        
                        break;
                    case SwitchStatement switchStatement:
                        switchStatement.expression.EnsureVariablesAreDefined(scope);
                        foreach (var caseGroup in switchStatement.cases )
                            CheckStatements(caseGroup.statements, scope);
                        if (switchStatement.defaultCase != null)
                        {
                            CheckStatements(switchStatement.defaultCase.statements, scope);
                        }
                        break;
                    case ForStatement forStatement:
                        if (forStatement.variableNode is VariableRefNode forVariable)
                        {
                            scope.AddVariable(forVariable);
                        }
                        forStatement.endValueExpression?.EnsureVariablesAreDefined(scope);
                        forStatement.stepValueExpression?.EnsureVariablesAreDefined(scope);
                        forStatement.startValueExpression?.EnsureVariablesAreDefined(scope);
                        forStatement.statements.CheckStatements(scope);
                        break;
                    case IfStatement ifStatement:
                        ifStatement.condition.EnsureVariablesAreDefined(scope);
                        ifStatement.positiveStatements?.CheckStatements(scope);
                        ifStatement.negativeStatements?.CheckStatements(scope);
                        break;
                    case DoLoopStatement doStatement:
                        doStatement.statements?.CheckStatements(scope);
                        break;
                    case WhileStatement whileStatement:
                        whileStatement.condition.EnsureVariablesAreDefined(scope);
                        whileStatement.statements.CheckStatements(scope);
                        break;
                    case RepeatUntilStatement repeatStatement:
                        repeatStatement.statements.CheckStatements(scope);
                        repeatStatement.condition.EnsureVariablesAreDefined(scope);
                        break;
                    case GoSubStatement goSub:
                        goSub.EnsureVariablesAreDefined(scope);
                        break;
                    case GotoStatement goTo:
                        goTo.EnsureVariablesAreDefined(scope);
                        break;
                    
                    case ExpressionStatement exprStatement:
                        exprStatement.EnsureVariablesAreDefined(scope);
                        break;
                    
                    case NoOpStatement _:
                    case ReturnStatement _:
                    case ExitLoopStatement _:
                    case LabelDeclarationNode _:
                        
                        break;
                    default:
                        throw new NotImplementedException($"cannot check statement for scope errors - {statement}");
                        // break;
                }
            }
        }

        static void EnsureStructField(this StructFieldReference fieldRef, Scope scope)
        {
            // the left most thing needs to exist in the scope, 
            switch (fieldRef.left)
            {
                case VariableRefNode variableRefNode:
                    if (!scope.TryGetVariable(variableRefNode.variableName))
                    {
                        variableRefNode.Errors.Add(new ParseError(fieldRef.StartToken, ErrorCodes.InvalidReference, variableRefNode.variableName));
                        break;
                    }
                    
                    // we need to know what the left side _is_ in order to create a scope for the right side.
                    break;
            }
            
        }
        
        static void EnsureVariablesAreDefined(this IAstVisitable visitable, Scope scope)
        {
            visitable.Visit(child =>
            {
                switch (child)
                {
                    case GoSubStatement goSub:
                        if (!scope.TryGetLabel(goSub.label) && goSub.label != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.UnknownLabel, goSub.label));
                        }
                        break;
                    case VariableRefNode variable:
                        if (!scope.TryGetVariable(variable.variableName) && variable.variableName != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {variable.variableName}"));
                        }
                        break;
                }
            });
        }
        
        public static void AddScopeRelatedErrors2(this ProgramNode program)
        {
            var scope = new Scope();

            foreach (var type in program.typeDefinitions)
            {
                scope.AddType(type);
            }
            
            
            program.Visit(child =>
            {
                var p2 = program;

                switch (child)
                {
                    case DeclarationStatement declarationStatement:
                        scope.AddDeclaration(declarationStatement);
                        break;
                    case AssignmentStatement assignment:
                        scope.AddAssignment(assignment);
                        break;
                    case ForStatement forStatement when forStatement.variableNode is VariableRefNode forVariable:
                        scope.AddVariable(forVariable);
                        break;
                    case FunctionStatement functionStatement:
                        scope.BeginFunction(functionStatement.parameters);
                        break;
                    case TypeDefinitionMember typeDefinition:

                        break;
                    case StructFieldReference structReference:
                        break;
                    // case FunctionReturnStatement _:
                    //     scope.EndFunction();
                    //     break;
                    case VariableRefNode refNode:
                        var p = program;
                        if (!scope.TryGetVariable(refNode.variableName))
                        {
                            refNode.Errors.Add(new ParseError(refNode.startToken, ErrorCodes.InvalidReference, $"unknown variable=[{refNode.variableName}]"));
                        }
                        break;
                }
            }, child =>
            {
                switch (child)
                {
                    case FunctionStatement functionStatement:
                        scope.EndFunction();
                        break;
                    case TypeDefinitionMember typeDefinition:
                        break;
                }
            });
        }
    }
}