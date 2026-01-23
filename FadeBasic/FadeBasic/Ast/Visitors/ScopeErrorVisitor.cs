using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Virtual;

namespace FadeBasic.Ast.Visitors
{
    public static partial class ErrorVisitors
    {

        public static void AddScopeRelatedErrors(this ProgramNode program, ParseOptions options, Dictionary<string, TypeInfo> knownFunctionTypes=null)
        { 
            if (options?.ignoreChecks ?? false)
            {
                // at one point I thought this should throw an exception...
                //  but then it turned out I thought it was handy for the REPL,
                //  because we can inject custom hand-made parse nodes before running the function
                return;
            }

            var scope = program.scope = new Scope();
            foreach (var label in program.labels)
            {
                scope.AddLabel(null, label.node);
            }

            foreach (var type in program.typeDefinitions)
            {
                scope.AddType(type);
            }
            
            // find all global declarations and put them in a list in the 
            //  scope itself, so we can know if the symbol exists AT ALL, or
            //  just yet...
            program.Visit(node =>
            {
                switch (node)
                {
                    case DeclarationStatement decl when decl.scopeType == DeclarationScopeType.Global:
                        scope.AddGlobalVariable(decl);
                        break;
                }
            });
            
            
            foreach (var function in program.functions)
            {
                foreach (var label in function.labels)
                {
                    scope.AddLabel(function.name, label);
                }
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

            var globalCtx = new EnsureTypeContext();
            
            
            if (knownFunctionTypes != null)
            {
                foreach (var kvp in knownFunctionTypes)
                {
                    scope.functionReturnTypeTable.Add(kvp.Key, new List<TypeInfo>{kvp.Value});
                }
            }
            
            CheckStatements(program.statements, scope, globalCtx);

            foreach (var function in program.functions)
            {
                if (scope.functionReturnTypeTable.ContainsKey(function.name))
                {
                    function.ParsedType = scope.functionReturnTypeTable[function.name][0];
                    continue; // already parsed. 
                }

                scope.BeginFunction(function);
                // var ctx = globalCtx.WithFunction(function);
                var ctx = globalCtx;
                CheckStatements(function.statements, scope, ctx);
                
                // throw away the type, just call this to make sure the type is validated. 
                var functionType = scope.GetFunctionTypeInfo(function, ctx);
                function.ParsedType = functionType;
                // if (functionType.unset)
                // {
                //     function.Errors.Add(new ParseError(function.startToken, ErrorCodes.UnknowableFunctionReturnType));
                // }
                
                scope.EndFunction();
                
            }

            foreach (var function in program.functions)
            {
                if (scope.functionReturnTypeTable.ContainsKey(function.name)) continue; // already parsed.
                function.Errors.Add(new ParseError(function.startToken, ErrorCodes.UnknowableFunctionReturnType));

            }

            foreach (var def in scope.defaultValueExpressions)
            {
                if (def.ParsedType.type == VariableType.Void)
                {
                    def.Errors.Add(new ParseError(def, ErrorCodes.DefaultExpressionUnknownType));
                }
            }

            scope.DoDelayedTypeChecks();
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
                
                // var seen = new HashSet<string>();
                var toExplore = new Stack<(string, HashSet<string>)>();
                //toExplore.Enqueue(node);
                
                foreach (var next in graph[node])
                {
                    toExplore.Push((next, new HashSet<string>{}));
                }

                while (toExplore.Count > 0)
                {
                    var (curr, callStack) = toExplore.Pop();

                    refCounter[curr] += 1;
                    
                    if (callStack.Contains(curr))
                    {
                        // ASYNC REF FOUND!
                        var source = scope.typeNameToDecl[curr];
                        source.Errors.Add(new ParseError(source.name, ErrorCodes.StructFieldsRecursive));

                        continue;
                    }

                    // seen.Add(curr);
                    // var nextStack = new HashSet<string>(callStack);
                    callStack.Add(curr);

                    processed.Add(curr);
                    
                    foreach (var next in graph[curr])
                    {
                        toExplore.Push((next, callStack));
                    }
                }
            }
        }
        
        
        static void CheckStatements(this List<IStatementNode> statements, Scope scope, EnsureTypeContext ctx)
        {
            // foreach (var statement in statements)
            for (var i = 0 ; i < statements.Count; i ++)
            {
                var statement = statements[i];
                switch (statement)
                {
                    case MacroTokenizeStatement tokenizeStatement:
                        // need to validate that substitutions are valid primitive types.
                        foreach (var sub in tokenizeStatement.substitutions)
                        {
                            sub.innerExpression.EnsureVariablesAreDefined(scope, ctx);
                            if (sub.innerExpression.ParsedType.type == VariableType.Struct || sub.innerExpression.ParsedType.IsArray)
                            {
                                // uh oh.
                                sub.Errors.Add(new ParseError(sub.innerExpression, ErrorCodes.SubstitutionMustBePrimitive));
                            } 
                            // switch (sub.innerExpression)
                            // {
                            //     case ILiteralNode literal:
                            //         // literals are good! 
                            //         break;
                            // }
                        }
                        break;
                    case CommandStatement commandStatement:
                        scope.AddCommand(commandStatement.command, commandStatement.args, commandStatement.argMap, ctx);
                        
                        break;
                    case DeclarationStatement decl:

                        if (decl.initializerExpression != null)
                        {
                            decl.initializerExpression.EnsureVariablesAreDefined(scope, ctx);
                        }
                        scope.AddDeclaration(decl, ctx);

                        if (decl.initializerExpression != null)
                        {
                            if (decl.initializerExpression is DefaultValueExpression defExpr)
                            {
                                defExpr.ParsedType = decl.ParsedType;
                            }
                            
                            scope.EnforceTypeAssignment(decl.initializerExpression,
                                decl.initializerExpression.ParsedType, decl.ParsedType, false, out _);
                        }

                        break;
                    case AssignmentStatement assignment:
                        
                        // check that the RHS of the assignment is valid.
                        assignment.expression.EnsureVariablesAreDefined(scope, ctx);

                        // and THEN register LHS of the assignemnt (otherwise you can get self-referential stuff)
                        scope.AddAssignment(assignment, ctx, out var implicitDecl);
                        
                        if (implicitDecl != null)
                        {
                            statements.Insert(i, implicitDecl);
                        }
                        switch (assignment.variable)
                        {
                            case StructFieldReference fieldRef:
                                fieldRef.EnsureStructField(scope, ctx);
                                break;
                            case ArrayIndexReference indexRef:
                                indexRef.EnsureArrayReferenceIsValid(scope, ctx);
                                break;
                            default:
                                break;
                        }

                        if (assignment.expression is DefaultValueExpression defExpr2 && assignment.variable.ParsedType.type != VariableType.Void)
                        {
                            defExpr2.ParsedType = assignment.variable.ParsedType;
                        }
                        
                        break;
                    case RedimStatement redimStatement:
                        redimStatement.variable.EnsureVariablesAreDefined(scope, ctx);
                        
                        if (!scope.TryGetSymbol(redimStatement.variable.variableName, out var symbol) &&
                            redimStatement.variable.variableName != "_")
                        {
                            
                        }
                        else
                        {
                            var src = symbol.source as DeclarationStatement;
                            if (redimStatement.ranks.Length != src.ranks.Length)
                            {
                                if (redimStatement.ranks.Length == 0)
                                {
                                    redimStatement.ranks = src.ranks; // just clone 'em
                                }
                                else
                                {
                                    redimStatement.Errors.Add(new ParseError(redimStatement, ErrorCodes.ReDimHasIncorrectNumberOfRanks));
                                }
                            }
                        }

                        
                        break;
                    case SwitchStatement switchStatement:
                        switchStatement.expression.EnsureVariablesAreDefined(scope, ctx);
                        foreach (var caseGroup in switchStatement.cases )
                            CheckStatements(caseGroup.statements, scope, ctx);
                        if (switchStatement.defaultCase != null)
                        {
                            CheckStatements(switchStatement.defaultCase.statements, scope, ctx);
                        }
                        break;
                    case ForStatement forStatement:
                        
                        if (forStatement.variableNode is VariableRefNode forVariable)
                        {
                            if (!scope.TryAddVariable(forVariable, out var existingSymbol))
                            {
                            }
                            forVariable.ParsedType = existingSymbol.typeInfo;
                        }
                        else
                        {
                            forVariable = null;
                        }
                        
                        
                        forStatement.endValueExpression?.EnsureVariablesAreDefined(scope, ctx);
                        forStatement.stepValueExpression?.EnsureVariablesAreDefined(scope, ctx);
                        forStatement.startValueExpression?.EnsureVariablesAreDefined(scope, ctx);
                        
                        if (forVariable != null)
                        {
                            scope.EnforceTypeAssignment(forVariable, forStatement.startValueExpression.ParsedType, forVariable.ParsedType, false, out _);
                        }
                        
                        scope.BeginLoop();
                        forStatement.statements.CheckStatements(scope, ctx);
                        scope.EndLoop();
                        
                        break;
                    case IfStatement ifStatement:
                        ifStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        scope.EnforceTypeAssignment(ifStatement.condition, ifStatement.condition.ParsedType, TypeInfo.Int, false, out _);

                        ifStatement.positiveStatements?.CheckStatements(scope, ctx);
                        ifStatement.negativeStatements?.CheckStatements(scope, ctx);
                        break;
                    case DoLoopStatement doStatement:
                        scope.BeginLoop();
                        doStatement.statements?.CheckStatements(scope, ctx);
                        scope.EndLoop();

                        break;
                    case WhileStatement whileStatement:
                        whileStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        scope.EnforceTypeAssignment(whileStatement.condition, whileStatement.condition.ParsedType, TypeInfo.Int, false, out _);

                        scope.BeginLoop();
                        whileStatement.statements.CheckStatements(scope, ctx);
                        scope.EndLoop();
                        break;
                    case RepeatUntilStatement repeatStatement:
                        scope.BeginLoop();
                        repeatStatement.statements.CheckStatements(scope, ctx);
                        scope.EndLoop();
                        repeatStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        scope.EnforceTypeAssignment(repeatStatement.condition, repeatStatement.condition.ParsedType, TypeInfo.Int, false, out _);

                        break;
                    case GoSubStatement goSub:
                        EnsureLabel(scope, goSub.label, goSub);
                        break;
                    case GotoStatement goTo:
                        EnsureLabel(scope, goTo.label, goTo);
                        break;
                    case ExpressionStatement exprStatement:
                        exprStatement.expression.EnsureVariablesAreDefined(scope, ctx);
                        break;
                    
                    case NoOpStatement _:
                    case ReturnStatement _:
                    case LabelDeclarationNode _:
                    case EndProgramStatement _:
                        break;
                    case ExitLoopStatement exitStatement:
                        if (!scope.AllowExits)
                        {
                            exitStatement.Errors.Add(new ParseError(exitStatement, ErrorCodes.ExitStatementFoundOutsideOfLoop));
                        }
                        break;
                    case SkipLoopStatement skipStatement:
                        if (!scope.AllowExits)
                        {
                            skipStatement.Errors.Add(new ParseError(skipStatement, ErrorCodes.SkipStatementFoundOutsideOfLoop));
                        }
                        break;
                    case FunctionStatement _:
                    case FunctionReturnStatement _:
                        break;
                    case TypeDefinitionStatement invalidTypeStatement:
                        invalidTypeStatement.Errors.Add(new ParseError(invalidTypeStatement.name, ErrorCodes.TypeMustBeTopLevel));
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
            else if (label != "_")
            {
                var currFuncName = scope.GetCurrentFunctionName();
                var declFuncName = scope.labelDeclTable[label];

                if (!string.Equals(currFuncName, declFuncName, StringComparison.InvariantCulture))
                {
                    node.Errors.Add(new ParseError(node, ErrorCodes.TraverseLabelBetweenScopes));
                }
            }

            node.DeclaredFromSymbol = labelSymbol;
        }
        static void EnsureStructRefRight(StructFieldReference fieldRef, Symbol symbol, Scope scope, EnsureTypeContext ctx)
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
                    EnsureStructField(nestedRef, subScope, ctx);
                    break;
                default:
                    throw new NotImplementedException(
                        "struct reference cannot have a right-side other than variable ref or nested-ref");
            }

        }

        static void EnsureStructField(this StructFieldReference fieldRef, Scope scope, EnsureTypeContext ctx)
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
                        indexRef.EnsureArrayReferenceIsValid(scope, ctx);
                    }

                    foreach (var rankExpr in indexRef.rankExpressions)
                    {
                        rankExpr.EnsureVariablesAreDefined(scope, ctx);
                    }

                    
                    // need to validate the rhs too
                    EnsureStructRefRight(fieldRef, arraySymbol, scope, ctx);

                    break;
                case VariableRefNode variableRefNode:

                    if (!scope.TryGetSymbol(variableRefNode.variableName, out var symbol))
                    {
                        if (symbol != null)
                        {
                            // accessing a symbol before it has been decalred
                            variableRefNode.Errors.Add(new ParseError(variableRefNode, ErrorCodes.SymbolNotDeclaredYet, "unknown symbol, " + variableRefNode.variableName));
                        }
                        else
                        {
                            // accessing an undefined variable...
                            variableRefNode.Errors.Add(new ParseError(variableRefNode, ErrorCodes.InvalidReference, "unknown symbol, " + variableRefNode.variableName));
                        }
                        break; // no hook into the symbol table, the rest of this expression is unknown...
                    }
                    EnsureStructRefRight(fieldRef, symbol, scope, ctx);
                    
                    // we need to know what the left side _is_ in order to create a scope for the right side.
                    break;
                default:
                    throw new NotImplementedException("How do you do this? asdf");
            }

            // the entire value of the structure is the right-hand-side.
            fieldRef.ParsedType = fieldRef.right.ParsedType;

        }

        static void EnsureArrayReferenceIsValid(this ArrayIndexReference indexRef, Scope scope, EnsureTypeContext ctx)
        {
            if (!scope.TryGetSymbol(indexRef.variableName, out var arraySymbol))
            {
                throw new NotImplementedException();
            }

            indexRef.DeclaredFromSymbol = arraySymbol;
            if (!arraySymbol.typeInfo.IsArray)
            {
                indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.CannotIndexIntoNonArray));
                return;
            }
            var rankMatch = arraySymbol.typeInfo.rank == indexRef.rankExpressions.Count;
            // foreach (var rankExpr in indexRef.rankExpressions)
            // {
            //     rankExpr.EnsureVariablesAreDefined(scope, ctx);
            // }
            if (!rankMatch)
            {
                indexRef.Errors.Add(new ParseError(indexRef, ErrorCodes.ArrayCardinalityMismatch));
            } 
        }


        public static void EnsureVariablesAreDefined(this IExpressionNode expr, Scope scope, EnsureTypeContext ctx)
        {
            switch (expr)
            {
                case DefaultValueExpression defExpr:
                    scope.AddDefaultExpression(defExpr);
                    break;
                case InitializerExpression initExpr:
                    // initializers are not allowed to appear here; they are syntax sugar and should be removed by now.
                    initExpr.Errors.Add(new ParseError(initExpr.startToken, ErrorCodes.InitializerNotAllowed));
                    break;
                case BinaryOperandExpression binaryOpExpr:
                    binaryOpExpr.lhs.EnsureVariablesAreDefined(scope, ctx);
                    binaryOpExpr.rhs.EnsureVariablesAreDefined(scope, ctx);
                    scope.EnforceOperatorTypes(binaryOpExpr);
                    break;
                case UnaryOperationExpression unaryOpExpr:
                    unaryOpExpr.rhs.EnsureVariablesAreDefined(scope, ctx);
                    unaryOpExpr.ParsedType = unaryOpExpr.rhs.ParsedType;
                    break;
                case StructFieldReference structRef:
                    structRef.EnsureStructField(scope, ctx);
                    break;
                case CommandExpression commandExpr: // commandExprs have the ability to declare variables!
                    scope.AddCommand(commandExpr, ctx);

                    if (commandExpr.command.returnType != TypeCodes.VOID && VmUtil.TryGetVariableType(commandExpr.command.returnType, out var tc))
                    {
                        commandExpr.ParsedType = TypeInfo.FromVariableType(tc);
                    }
                   
                    break;
                case ArrayIndexReference arrayRef:
                    if (!scope.TryGetSymbol(arrayRef.variableName, out var arraySymbol) && arrayRef.variableName != "_")
                    {
                        if (scope.functionTable.TryGetValue(arrayRef.variableName, out var function))
                        {
                            TypeInfo functionType = default;

                            if (ctx.functionHistory.Contains(function.name))
                            {
                                // we've already seen this before.
                                //  make no modifications, but report in the ctx that a recursive loop has been detected.
                                ctx.ReportLoop(function.name);
                            }
                            else
                            {

                                if (!scope.functionReturnTypeTable.TryGetValue(function.name, out var functionTypes))
                                {

                                    /*
                                     * this is a recursive call, and we need history checking.
                                     *  if this execution has seen the given method,
                                     *  then checking its statements WILL result in an infinite loop.
                                     *
                                     * if this call is from the "main" scope,
                                     * or if this call is from a "function" scope
                                     */

                                    if (scope.functionCheck.Contains(function.name))
                                    {
                                        // we've already seen this function
                                    }

                                    scope.BeginFunction(function);
                                    var subCtx = ctx.WithFunction(function);
                                    function.statements.CheckStatements(scope, subCtx);
                                    functionType = scope.GetFunctionTypeInfo(function, subCtx);
                                    scope.EndFunction();

                                }
                                else
                                {
                                    functionType = functionTypes[0];
                                }

                                arrayRef.ParsedType = functionType;
                                arrayRef.DeclaredFromSymbol = arraySymbol;
                            


                                // ah, this is a function!
                                arrayRef.startToken.flags |= TokenFlags.FunctionCall;
                                if (arrayRef.rankExpressions.Count != function.parameters.Count)
                                {
                                    arrayRef.Errors.Add(new ParseError(arrayRef.startToken,
                                        ErrorCodes.FunctionParameterCardinalityMismatch));
                                }

                                // check that types match
                                for (var argIndex = 0;
                                     argIndex < arrayRef.rankExpressions.Count && argIndex < function.parameters.Count;
                                     argIndex++)
                                {
                                    var argExr = arrayRef.rankExpressions[argIndex];
                                    argExr.EnsureVariablesAreDefined(scope, ctx);

                                    var parameter = function.parameters[argIndex];
                                    if (parameter.ParsedType.type == VariableType.Void)
                                    {
                                        switch (parameter.type)
                                        {
                                            case TypeReferenceNode typeNode:
                                                parameter.ParsedType = TypeInfo.FromVariableType(typeNode.variableType);
                                                break;
                                            case StructTypeReferenceNode structNode:
                                                parameter.ParsedType =
                                                    TypeInfo.FromVariableType(structNode.variableType,
                                                        structName: structNode.variableNode.variableName);
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }
                                    }


                                    // var _ = GetFunctionTypeInfo(function, scope);
                                    scope.AddDelayedTypeCheck(argExr, argExr, parameter);
                                    // scope.EnforceTypeAssignment(argExr, argExr.ParsedType, parameter.ParsedType, false,
                                    // out _);
                                }
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
                    if (arraySymbol != null)
                    {
                        if (arraySymbol.typeInfo.IsArray && arrayRef.rankExpressions.Count != arraySymbol.typeInfo.rank)
                        {
                            if (arrayRef.Errors.All(x => x.errorCode.code != ErrorCodes.VariableIndexMissingCloseParen.code))
                            {
                                arrayRef.Errors.Add(new ParseError(arrayRef, ErrorCodes.ArrayCardinalityMismatch));
                            }
                        }
                        arrayRef.ParsedType = new TypeInfo
                        {
                            type = arraySymbol.typeInfo.type,
                            structName = arraySymbol.typeInfo.structName,
                            rank = 0,
                        };
                    }
                    foreach (var rankExpr in arrayRef.rankExpressions)
                    {
                        rankExpr.EnsureVariablesAreDefined(scope, ctx);
                        if (rankExpr.ParsedType.type != VariableType.Integer)
                        {
                            rankExpr.Errors.Add(new ParseError(rankExpr, ErrorCodes.ArrayRankMustBeInteger));
                        }
                    }
                    break;
                case VariableRefNode variable:
                    if (!scope.TryGetSymbol(variable.variableName, out var symbol) && variable.variableName != "_")
                    {
                        if (symbol != null)
                        {
                            // accessing a symbol before it has been decalred
                            expr.Errors.Add(new ParseError(expr.StartToken, ErrorCodes.SymbolNotDeclaredYet, "symbol, " + variable.variableName));
                        }
                        else
                        {
                            expr.Errors.Add(new ParseError(expr.StartToken, ErrorCodes.InvalidReference, $"unknown symbol, {variable.variableName}"));
                        }
                    }

                    variable.DeclaredFromSymbol = symbol;
                    variable.ApplyTypeFromSymbol(symbol);

                    // variable.ParsedType = symbol.typeInfo;
                    break;
                case LiteralStringExpression literalString:
                    literalString.ParsedType = TypeInfo.String;
                    break;
                case LiteralIntExpression literalInt:
                    literalInt.ParsedType = TypeInfo.Int;
                    break;
                case LiteralRealExpression literalReal:
                    literalReal.ParsedType = TypeInfo.Real;
                    break;
                default:
                    break;
            }
        }
        
    }

    public class EnsureTypeContext
    {
        public HashSet<string> functionHistory = new HashSet<string>();
        public bool HasLoop { get; private set; }

        public EnsureTypeContext WithFunction(FunctionStatement function)
        {
            var names = new HashSet<string>(functionHistory);
            names.Add(function.name);
            return new EnsureTypeContext
            {
                functionHistory = names
            };
        }

        public void ReportLoop(string functionName)
        {
            HasLoop = true;
        }
    }
}