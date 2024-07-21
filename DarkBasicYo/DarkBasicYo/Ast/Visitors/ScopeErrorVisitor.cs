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

            foreach (var type in program.typeDefinitions)
            {
                scope.AddType(type);
            }
            CheckTypeInfo2(scope);
            CheckStatements(program.statements, scope);
        }

        static void CheckTypeInfo2(Scope scope)
        {
            var toExplore = new Queue<Symbol>();
            var seenTypes = new HashSet<string>();
            foreach (var namedType in scope.typeNameToTypeMembers)
            {
                seenTypes.Clear();
                toExplore.Clear();
                foreach (var namedField in namedType.Value)
                {
                    toExplore.Enqueue(namedField.Value);
                }

                while (toExplore.Count > 0)
                {
                    var curr = toExplore.Dequeue();
                    var fieldType = curr.typeInfo.type;
                    var structName = curr.typeInfo.structName;

                    if (fieldType != VariableType.Struct)
                        continue;

                    if (seenTypes.Contains(structName))
                    {
                        curr.source.Errors.Add(new ParseError(curr.source, ErrorCodes.StructFieldsRecursive));
                        continue; // recursive error needs to stop processing this chain
                    }

                    seenTypes.Add(structName);
                    
                    
                    if (!scope.typeNameToTypeMembers.TryGetValue(structName, out var referencedType))
                    {
                        curr.source.Errors.Add(new ParseError(curr.source, ErrorCodes.StructFieldReferencesUnknownStruct));
                        continue;
                    }
                    
                    foreach (var namedField in referencedType)
                    {
                        toExplore.Enqueue(namedField.Value);
                    }
                }
            }
            
            
        }
        
        static void CheckTypeInfo(Scope scope)
        {
            
            // check that all types have valid type references...
            foreach (var kvp in scope.typeNameToTypeMembers)
            {
                var typeTable = kvp.Value;
                foreach (var namedField in typeTable)
                {
                    var fieldType = namedField.Value;
                    if (fieldType.typeInfo.type == VariableType.Struct)
                    {
                        var structName = fieldType.typeInfo.structName;
                        if (!scope.typeNameToTypeMembers.ContainsKey(structName))
                        {
                            fieldType.source.Errors.Add(new ParseError(fieldType.source, ErrorCodes.StructFieldReferencesUnknownStruct));
                        } else if (structName == kvp.Key)
                        {
                            fieldType.source.Errors.Add(new ParseError(fieldType.source, ErrorCodes.StructFieldsRecursive));
                        }
                    }
                }
            }

        }

        // static void CheckExpressions(this List<IExpressionNode> expressions, Scope scope)
        // {
        //     foreach (var expr in expressions)
        //     {
        //         switch (expr)
        //         {
        //             case CommandExpression commandExpression:
        //                 // this can define a variable...
        //                 commandExpression.
        //                 break;
        //         }
        //     }
        // }
        
        static void CheckStatements(this List<IStatementNode> statements, Scope scope)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case CommandStatement commandStatement:
                        scope.AddCommand(commandStatement.command, commandStatement.args, commandStatement.argMap);
                        // commandStatement.args.CheckExpressions(scope);
                        
                        // check that all parameters make sense...
                        commandStatement.EnsureVariablesAreDefined(scope);
                        break;
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
                        
                        // check that the RHS of the assignment is valid.
                        assignment.expression.EnsureVariablesAreDefined(scope);

                        // and THEN register LHS of the assignemnt (otherwise you can get self-referential stuff)
                        scope.AddAssignment(assignment);
                        
                        switch (assignment.variable)
                        {
                            case StructFieldReference fieldRef:
                                fieldRef.EnsureStructField(scope);
                                break;
                            case ArrayIndexReference indexRef:
                                indexRef.EnsureArrayReferenceIsValid(scope);
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
                            scope.TryAddVariable(forVariable);
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
                    case EndProgramStatement _:
                        
                        break;
                    default:
                        throw new NotImplementedException($"cannot check statement for scope errors - {statement.GetType().Name} {statement}");
                        // break;
                }
            }
        }
        
        // static void TryGetSymbolTable(this StructFieldReference)

        static void EnsureStructRefRight(StructFieldReference fieldRef, Symbol symbol, Scope scope)
        {

            // now that we have a symbol for the left side...
            if (symbol.typeInfo.type != VariableType.Struct)
            {
                fieldRef.left.Errors.Add(new ParseError(fieldRef.left, ErrorCodes.ExpressionIsNotAStruct));
                return; // this type wasn't a struct-like, so we can't search the right value...
            }

            // now that we know the left side is a struct... 
            if (!scope.TryGetType(symbol.typeInfo.structName, out var typeTable))
            {
                fieldRef.left.Errors.Add(new ParseError(fieldRef.left, ErrorCodes.UnknownStructRef));
                return;
            }

            // we finally have the type info!
            var rhs = fieldRef.right;

            // it can only be a variable, or another sub-nested struct reference
            switch (rhs)
            {
                case VariableRefNode variableRight:
                    if (!typeTable.ContainsKey(variableRight.variableName))
                    {
                        variableRight.Errors.Add(new ParseError(variableRight, ErrorCodes.StructFieldDoesNotExist));
                        // terminal position...
                    }

                    break;
                case StructFieldReference nestedRef:
                    var subScope = Scope.CreateStructScope(scope, typeTable);
                    EnsureStructField(nestedRef, subScope);
                    break;
                default:
                    throw new NotImplementedException(
                        "struct reference cannot have a right-side other than variable ref or nested-ref");
            }

        }

        static void EnsureStructField(this StructFieldReference fieldRef, Scope scope)
        {
            // the left most thing needs to exist in the scope, 
            switch (fieldRef.left)
            {
                case ArrayIndexReference indexRef:
                    // x(2) = 1
                    if (!scope.TryGetSymbol(indexRef.variableName, out var arraySymbol))
                    {
                        indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.InvalidReference));
                        break;
                    }

                    if (!arraySymbol.typeInfo.IsArray)
                    {
                        indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.CannotIndexIntoNonArray));
                    }
                    else
                    {
                        indexRef.EnsureArrayReferenceIsValid(scope);
                    }

                    foreach (var rankExpr in indexRef.rankExpressions)
                    {
                        rankExpr.EnsureVariablesAreDefined(scope);
                    }

                    
                    // need to validate the rhs too
                    EnsureStructRefRight(fieldRef, arraySymbol, scope);

                    break;
                case VariableRefNode variableRefNode:

                    if (!scope.TryGetSymbol(variableRefNode.variableName, out var symbol))
                    {
                        // accessing an undefined variable...
                        variableRefNode.Errors.Add(new ParseError(variableRefNode, ErrorCodes.InvalidReference, "unknown symbol, " + variableRefNode.variableName));
                        return; // no hook into the symbol table, the rest of this expression is unknown...
                    }
                    EnsureStructRefRight(fieldRef, symbol, scope);
                    
                    // we need to know what the left side _is_ in order to create a scope for the right side.
                    break;
                default:
                    throw new NotImplementedException("How do you do this? asdf");
            }
            
        }

        static void EnsureArrayReferenceIsValid(this ArrayIndexReference indexRef, Scope scope)
        {
            if (!scope.TryGetSymbol(indexRef.variableName, out var arraySymbol))
            {
                throw new NotImplementedException();
            }

            var rankMatch = arraySymbol.typeInfo.rank == indexRef.rankExpressions.Count;
            if (!rankMatch)
            {
                indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.ArrayCardinalityMismatch));
            }
        }
        
        static void EnsureVariablesAreDefined(this IAstVisitable visitable, Scope scope)
        {
            visitable.Visit(child =>
            {
                switch (child)
                {
                    case CommandExpression commandExpr: // commandExprs have the ability to declare variables!
                        scope.AddCommand(commandExpr);
                        break;
                    case GoSubStatement goSub:
                        if (!scope.TryGetLabel(goSub.label) && goSub.label != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.UnknownLabel, goSub.label));
                        }
                        break;
                    case ArrayIndexReference arrayRef:
                        if (!scope.TryGetSymbol(arrayRef.variableName, out _) && arrayRef.variableName != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {arrayRef.variableName}"));
                        }
                        break;
                    case VariableRefNode variable:
                        if (!scope.TryGetSymbol(variable.variableName, out _) && variable.variableName != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {variable.variableName}"));
                        }
                        break;
                }
            });
        }
        
        // public static void AddScopeRelatedErrors2(this ProgramNode program)
        // {
        //     var scope = new Scope();
        //
        //     foreach (var type in program.typeDefinitions)
        //     {
        //         scope.AddType(type);
        //     }
        //     
        //     
        //     program.Visit(child =>
        //     {
        //         var p2 = program;
        //
        //         switch (child)
        //         {
        //             case DeclarationStatement declarationStatement:
        //                 scope.AddDeclaration(declarationStatement);
        //                 break;
        //             case AssignmentStatement assignment:
        //                 scope.AddAssignment(assignment);
        //                 break;
        //             case ForStatement forStatement when forStatement.variableNode is VariableRefNode forVariable:
        //                 scope.AddVariable(forVariable);
        //                 break;
        //             case FunctionStatement functionStatement:
        //                 scope.BeginFunction(functionStatement.parameters);
        //                 break;
        //             case TypeDefinitionMember typeDefinition:
        //
        //                 break;
        //             case StructFieldReference structReference:
        //                 break;
        //             // case FunctionReturnStatement _:
        //             //     scope.EndFunction();
        //             //     break;
        //             case VariableRefNode refNode:
        //                 var p = program;
        //                 if (!scope.TryGetVariable(refNode.variableName))
        //                 {
        //                     refNode.Errors.Add(new ParseError(refNode.startToken, ErrorCodes.InvalidReference, $"unknown variable=[{refNode.variableName}]"));
        //                 }
        //                 break;
        //         }
        //     }, child =>
        //     {
        //         switch (child)
        //         {
        //             case FunctionStatement functionStatement:
        //                 scope.EndFunction();
        //                 break;
        //             case TypeDefinitionMember typeDefinition:
        //                 break;
        //         }
        //     });
        // }
    }
}