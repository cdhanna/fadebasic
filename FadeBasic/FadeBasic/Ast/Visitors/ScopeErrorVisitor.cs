using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast.Visitors
{
    public static partial class ErrorVisitors
    {

        public static void AddScopeRelatedErrors(this ProgramNode program, ParseOptions options)
        {
            if (options?.ignoreChecks ?? false) throw new NotSupportedException("take this out- it shouldnt be valid to ignore saftey checks");
            
            var scope = program.scope = new Scope();
            foreach (var label in program.labels)
            {
                scope.AddLabel(label.node);
            }

            foreach (var type in program.typeDefinitions)
            {
                scope.AddType(type);
            }
            
            
            foreach (var function in program.functions)
            {
                scope.DeclareFunction(function);
            }

            // CheckTypeInfo2(scope);
            CheckTypesForUnknownReferences(scope);
            CheckTypesForRecursiveReferences(scope, out var typeRefCounter);
            program.typeDefinitions?.Sort((a, b) =>
            {
                var aVal = typeRefCounter[a.name.variableName];
                var bVal = typeRefCounter[b.name.variableName];
                if (aVal == bVal) return 0;
                return aVal > bVal ? -1 : 1;
            });
            
            
            CheckStatements(program.statements, scope);
            foreach (var function in program.functions)
            {
                scope.BeginFunction(function);
                CheckStatements(function.statements, scope);
                scope.EndFunction();
            }
        }

        static void CheckTypesForUnknownReferences(Scope scope)
        {
            foreach (var namedType in scope.typeNameToTypeMembers)
            {
                var typeName = namedType.Key;
                var members = namedType.Value;

                foreach (var member in members)
                {
                    var memberName = member.Key;
                    var memberSymbol = member.Value;
                    
                    if (memberSymbol.typeInfo.type != VariableType.Struct)
                        continue; // not a struct reference...
                    
                    if (!scope.typeNameToTypeMembers.TryGetValue(memberSymbol.typeInfo.structName, out var referencedType))
                    {
                        memberSymbol.source.Errors.Add(new ParseError(memberSymbol.source, ErrorCodes.StructFieldReferencesUnknownStruct));
                        continue;
                    }
                }
            }
        }
        
        static void CheckTypesForRecursiveReferences(Scope scope, out Dictionary<string, int> referenceCounter)
        {
            var graph = new Dictionary<string, HashSet<string>>();
            referenceCounter = new Dictionary<string, int>(); // 

            // create a type dependency graph...
            {
                foreach (var namedType in scope.typeNameToTypeMembers)
                {
                    var typeName = namedType.Key;
                    var members = namedType.Value;
                    referenceCounter[typeName] = 1;

                    graph[typeName] = new HashSet<string>();

                    foreach (var member in members)
                    {
                        var memberSymbol = member.Value;

                        if (memberSymbol.typeInfo.type != VariableType.Struct)
                            continue; // not a struct reference...

                        if (!scope.typeNameToTypeMembers.ContainsKey(memberSymbol.typeInfo.structName))
                            continue; // not a valid struct reference...
                        graph[typeName].Add(memberSymbol.typeInfo.structName);
                    }
                }
            }
            
            var processed = new HashSet<string>();
            // now that we have a graph, check each node
            foreach (var kvp in graph)
            {
                Process(kvp.Key, referenceCounter);
            }
            
            // re-sort the types

            void Process(string node, Dictionary<string, int> refCounter)
            {
                if (processed.Contains(node))
                {
                    // leave uncommented; if we return, then we only get the error from one side of the type collision.
                    // return; // already done!
                }
                
                var seen = new HashSet<string>();
               
                var toExplore = new Queue<string>();
                //toExplore.Enqueue(node);
                
                foreach (var next in graph[node])
                {
                    toExplore.Enqueue(next);
                }

                while (toExplore.Count > 0)
                {
                    var curr = toExplore.Dequeue();

                    refCounter[curr] += 1;
                    
                    if (seen.Contains(curr))
                    {
                        // ASYNC REF FOUND!
                        var source = scope.typeNameToDecl[curr];
                        source.Errors.Add(new ParseError(source.name, ErrorCodes.StructFieldsRecursive));

                        continue;
                    }

                    seen.Add(curr);
                    processed.Add(curr);
                    
                    foreach (var next in graph[curr])
                    {
                        toExplore.Enqueue(next);
                    }
                }
            }
        }
        
        static void CheckStatements(this List<IStatementNode> statements, Scope scope)
        {
            // foreach (var statement in statements)
            for (var i = 0 ; i < statements.Count; i ++)
            {
                var statement = statements[i];
                switch (statement)
                {
                    case CommandStatement commandStatement:
                        scope.AddCommand(commandStatement.command, commandStatement.args, commandStatement.argMap);
                        // commandStatement.args.CheckExpressions(scope);
                        
                        // check that all parameters make sense...
                        foreach (var arg in commandStatement.args)
                        {
                            arg.EnsureVariablesAreDefined(scope);
                        }
                        break;
                    case FunctionReturnStatement returnStatement:
                        returnStatement.returnExpression.EnsureVariablesAreDefined(scope);
                        // scope.EndFunction();
                        break;
                    case FunctionStatement functionStatement:
                        scope.BeginFunction(functionStatement);
                        CheckStatements(functionStatement.statements, scope);
                        scope.EndFunction();
                        break;
                    case DeclarationStatement decl:
                        if (decl.initializerExpression != null)
                        {
                            decl.initializerExpression.EnsureVariablesAreDefined(scope);
                        }
                        scope.AddDeclaration(decl);
                        break;
                    case AssignmentStatement assignment:
                        
                        // check that the RHS of the assignment is valid.
                        assignment.expression.EnsureVariablesAreDefined(scope);

                        // and THEN register LHS of the assignemnt (otherwise you can get self-referential stuff)
                        scope.AddAssignment(assignment, out var implicitDecl);
                        if (implicitDecl != null)
                        {
                            statements.Insert(i, implicitDecl);
                        }
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
                        EnsureLabel(scope, goSub.label, goSub);
                        break;
                    case GotoStatement goTo:
                        EnsureLabel(scope, goTo.label, goTo);
                        break;
                    
                    case ExpressionStatement exprStatement:
                        exprStatement.expression.EnsureVariablesAreDefined(scope);
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
        static void EnsureLabel(Scope scope, string label, AstNode node)
        {
            if (!scope.TryGetLabel(label, out var labelSymbol) && label != "_")
            {
                node.Errors.Add(new ParseError(node.StartToken, ErrorCodes.UnknownLabel, label));
            }

            node.DeclaredFromSymbol = labelSymbol;
        }
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
                        // terminal position...
                    }

                    if (!typeTable.TryGetValue(variableRight.variableName, out var variableSymbol))
                    {
                        variableRight.Errors.Add(new ParseError(variableRight, ErrorCodes.StructFieldDoesNotExist));
                        break;
                    }

                    variableRight.ApplyTypeFromSymbol(variableSymbol);
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
                        break; // no hook into the symbol table, the rest of this expression is unknown...
                    }
                    EnsureStructRefRight(fieldRef, symbol, scope);
                    
                    // we need to know what the left side _is_ in order to create a scope for the right side.
                    break;
                default:
                    throw new NotImplementedException("How do you do this? asdf");
            }

            // the entire value of the structure is the right-hand-side.
            fieldRef.ParsedType = fieldRef.right.ParsedType;

        }

        static void EnsureArrayReferenceIsValid(this ArrayIndexReference indexRef, Scope scope)
        {
            if (!scope.TryGetSymbol(indexRef.variableName, out var arraySymbol))
            {
                throw new NotImplementedException();
            }

            indexRef.DeclaredFromSymbol = arraySymbol;
            var rankMatch = arraySymbol.typeInfo.rank == indexRef.rankExpressions.Count;
            if (!rankMatch)
            {
                indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.ArrayCardinalityMismatch));
            }
        }


        static void EnsureVariablesAreDefined(this IExpressionNode expr, Scope scope)
        {
            switch (expr)
            {
                case BinaryOperandExpression binaryOpExpr:
                    binaryOpExpr.lhs.EnsureVariablesAreDefined(scope);
                    binaryOpExpr.rhs.EnsureVariablesAreDefined(scope);
                    break;
                case UnaryOperationExpression unaryOpExpr:
                    unaryOpExpr.rhs.EnsureVariablesAreDefined(scope);
                    break;
                case StructFieldReference structRef:
                    structRef.EnsureStructField(scope);
                    break;
                case CommandExpression commandExpr: // commandExprs have the ability to declare variables!
                    scope.AddCommand(commandExpr);
                    break;
                case ArrayIndexReference arrayRef:
                    if (!scope.TryGetSymbol(arrayRef.variableName, out var arraySymbol) && arrayRef.variableName != "_")
                    {
                        if (scope.functionTable.TryGetValue(arrayRef.variableName, out var function))
                        {
                            // ah, this is a function!
                            arrayRef.startToken.flags |= TokenFlags.FunctionCall;
                            if (arrayRef.rankExpressions.Count != function.parameters.Count)
                            {
                                arrayRef.Errors.Add(new ParseError(arrayRef.startToken, ErrorCodes.FunctionParameterCardinalityMismatch));
                            }
                            arrayRef.DeclaredFromSymbol = scope.functionSymbolTable[arrayRef.variableName];

                            break;
                        }
                        expr.Errors.Add(new ParseError(expr.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {arrayRef.variableName}"));
                    }
                    else
                    {
                        if (!arraySymbol.typeInfo.IsArray)
                        {
                            expr.Errors.Add(new ParseError(expr.StartToken, ErrorCodes.CannotIndexIntoNonArray));
                        }
                    }

                    arrayRef.DeclaredFromSymbol = arraySymbol;
                    arrayRef.ApplyTypeFromSymbol(arraySymbol);
                    // arrayRef.ParsedType = arraySymbol.typeInfo; // TODO: this doesn't work if the program isn't complete. partial errors and whatnot
                    break;
                case VariableRefNode variable:
                    if (!scope.TryGetSymbol(variable.variableName, out var symbol) && variable.variableName != "_")
                    {
                        expr.Errors.Add(new ParseError(expr.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {variable.variableName}"));
                    }

                    variable.DeclaredFromSymbol = symbol;
                    variable.ApplyTypeFromSymbol(symbol);

                    // variable.ParsedType = symbol.typeInfo;
                    break;
                default:
                    break;
            }
        }
        
        static void EnsureVariablesAreDefinedOld(this IExpressionNode visitable, Scope scope)
        {
            visitable.Visit(child =>
            {
                switch (child)
                {
                    case CommandExpression commandExpr: // commandExprs have the ability to declare variables!
                        scope.AddCommand(commandExpr);
                        break;
                    case ArrayIndexReference arrayRef:
                        if (!scope.TryGetSymbol(arrayRef.variableName, out var arraySymbol) && arrayRef.variableName != "_")
                        {
                            if (scope.functionTable.TryGetValue(arrayRef.variableName, out var function))
                            {
                                // ah, this is a function!
                                arrayRef.startToken.flags |= TokenFlags.FunctionCall;
                                if (arrayRef.rankExpressions.Count != function.parameters.Count)
                                {
                                    arrayRef.Errors.Add(new ParseError(arrayRef.startToken, ErrorCodes.FunctionParameterCardinalityMismatch));
                                }
                                arrayRef.DeclaredFromSymbol = scope.functionSymbolTable[arrayRef.variableName];

                                break;
                            }
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {arrayRef.variableName}"));
                        }

                        arrayRef.DeclaredFromSymbol = arraySymbol;
                        break;
                    case VariableRefNode variable:
                        if (!scope.TryGetSymbol(variable.variableName, out var symbol) && variable.variableName != "_")
                        {
                            child.Errors.Add(new ParseError(child.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {variable.variableName}"));
                        }

                        variable.DeclaredFromSymbol = symbol;
                        break;
                }
            });
        }
        
    }
}