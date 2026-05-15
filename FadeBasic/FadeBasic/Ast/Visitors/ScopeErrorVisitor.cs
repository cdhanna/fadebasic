using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Virtual;

namespace FadeBasic.Ast.Visitors
{
    public static partial class ErrorVisitors
    {

        public static void AddScopeRelatedErrors(this ProgramNode program, ParseOptions options, Dictionary<string, TypeInfo> knownFunctionTypes=null, ProgramNode parentProgram=null)
        { 
            if (options?.ignoreChecks ?? false)
            {
                // at one point I thought this should throw an exception...
                //  but then it turned out I thought it was handy for the REPL,
                //  because we can inject custom hand-made parse nodes before running the function
                return;
            }


            var scope = program.scope = new Scope();
            // Region name used to tag this program's top-level labels and to seed
            // GetCurrentFunctionName() so EnsureLabel can detect cross-scope gotos
            // between a test and its parent. Null for the outermost program (matches
            // the existing "top-level = null" convention).
            string topLevelRegion = parentProgram != null ? program.startToken?.raw : null;
            if (parentProgram != null)
            {
                // We're scoping a test's sub-program. Mark the scope so test-only
                // statements (assert/runto/mock/clear-mock) don't false-fire as
                // "outside test" while we recurse.
                scope.IsInsideTest = true;

                // Push the test's name as the current "function" context so
                // GetCurrentFunctionName() returns the test region (not null) for
                // the test's top-level statements. This makes EnsureLabel emit
                // TraverseLabelBetweenScopes when test code does `goto mainLabel`
                // (parent's top-level labels carry a null funcName tag).
                scope.currentFunctionName.Push(topLevelRegion);

                // Layer in the parent program's scope as a baseline. Per the design,
                // tests can read into parent (globals, types, functions, labels) but
                // parent never reads into a test. We copy dictionary state here; the
                // test's own pass below adds its locals on top. Stack-based state
                // (currentFunctionName/Region, localVariables frames) intentionally
                // stays separate — those are runtime-walk state, not symbol tables.
                var parentScope = parentProgram.scope;

                foreach (var kvp in parentScope.labelTable)
                    scope.labelTable[kvp.Key] = kvp.Value;
                foreach (var kvp in parentScope.labelDeclTable)
                    scope.labelDeclTable[kvp.Key] = kvp.Value;

                foreach (var kvp in parentScope.typeNameToTypeMembers)
                    scope.typeNameToTypeMembers[kvp.Key] = kvp.Value;
                foreach (var kvp in parentScope.typeNameToDecl)
                    scope.typeNameToDecl[kvp.Key] = kvp.Value;

                // Globals (`global X`) — always visible to tests.
                foreach (var kvp in parentScope.globalVariables)
                    scope.globalVariables[kvp.Key] = kvp.Value;
                foreach (var kvp in parentScope.allGlobalVariables)
                    scope.allGlobalVariables[kvp.Key] = kvp.Value;

                // Parent top-level locals: variables declared at the program's main
                // scope (including implicit-locals from bare assignments). The
                // strict-scope visitor decides per-runto which of these the test
                // can actually *see*; here we just make them resolvable so the
                // basic scope check doesn't flag them as unknown.
                if (parentScope.localVariables.Count > 0)
                {
                    var parentTopLocals = parentScope.localVariables.Peek();
                    var testTopLocals = scope.localVariables.Peek();
                    foreach (var kvp in parentTopLocals)
                    {
                        testTopLocals[kvp.Key] = kvp.Value;
                        scope.borrowedFromParent.Add(kvp.Key);
                    }
                }

                // Parent function-internal locals + parameters. Same rationale
                // as above: without this, `runto :insideFn; print y` blows up
                // with [0200] "unknown symbol y" before the strict visitor can
                // rule on per-runto visibility. The strict visitor's
                // ComputeFunctionInternalScopeAts already snapshots these
                // names so it can enforce reachability per runto target.
                //
                // Name collisions across functions are resolved
                // first-source-wins via the ContainsKey guard; type info on
                // those rare cases may resolve to the "wrong" function, but
                // visibility (the immediate goal) is unaffected.
                {
                    var testTopLocals = scope.localVariables.Peek();
                    foreach (var entry in parentScope.positionedVariables.entries)
                    {
                        var (fnTable, fnName) = entry.value;
                        if (fnName == null) continue; // top-level program, already copied
                        foreach (var kvp in fnTable)
                        {
                            if (!testTopLocals.ContainsKey(kvp.Key))
                            {
                                testTopLocals[kvp.Key] = kvp.Value;
                                scope.borrowedFromParent.Add(kvp.Key);
                            }
                        }
                    }
                }

                foreach (var kvp in parentScope.functionSymbolTable)
                    scope.functionSymbolTable[kvp.Key] = kvp.Value;
                foreach (var kvp in parentScope.functionTable)
                    scope.functionTable[kvp.Key] = kvp.Value;
                foreach (var kvp in parentScope.functionReturnTypeTable)
                    scope.functionReturnTypeTable[kvp.Key] = kvp.Value;
            }
            
            // add the main program variables. 
            scope.positionedVariables.Add(new TokenTable<(SymbolTable, string)>.Entry(program, (scope.localVariables.Peek(), null)));
            
            foreach (var label in program.labels)
            {
                // Inside a test scope, tag this program's top-level labels with the
                // test's region name (not null) so cross-scope gotos to/from main
                // get caught by EnsureLabel's funcName comparison.
                scope.AddLabel(topLevelRegion, label);
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
            
            var allFunctions = new List<FunctionStatement>();
            allFunctions.AddRange(program.functions);
            /*
             * the general rule here is that a TEST can call into global parent scope.
             * but parent scope can never call into TEST scope.
             *  - this is so that we can always safely remove tests from a production build
             *  - and so that the presence of the test never changes how the main code runs.
             *
             * In that sense- the scope error visiting is not so much about having specific
             * support for test scoping;
             * it is more about merging the parent scope as a baseline when starting to parse the test scope. 
             */
            
            foreach (var function in allFunctions)
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
                    // indexer rather than Add: parent-merged entries (in test
                    // sub-scopes) may already contain keys from knownFunctionTypes.
                    scope.functionReturnTypeTable[kvp.Key] = new List<TypeInfo>{kvp.Value};
                }
            }
            
            
            // Inside a test sub-scope, push the test's region name so calls to
            // test-internal functions (whose region equals the test's name) don't
            // trip the "test function called from top-level" check at line ~904.
            scope.currentRegionName.Push(parentProgram != null ? topLevelRegion : FunctionStatement.REGION_TOP_LEVEL);
            CheckStatements(program.statements, scope, globalCtx);

            foreach (var function in allFunctions)
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

            foreach (var function in allFunctions)
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
            
            // as the very last part of verifying the scope, 
            //  we need to verify the child scopes, which at this point, are just tests 
            scope.currentRegionName.Pop(); // remove the top level region.
            foreach (var test in program.tests)
            {
                // Tests cannot be nested inside another test. parentProgram != null
                // means *we* are already a test sub-program, so any tests we contain
                // are an invalid nesting.
                if (parentProgram != null)
                {
                    test.Errors.Add(new ParseError(test.nameToken ?? test.StartToken, ErrorCodes.TestNestingNotAllowed));
                    continue;
                }
                test.testProgram.AddScopeRelatedErrors(options, knownFunctionTypes, program);
            }

            // Strict scope_at(:L) enforcement runs after all test sub-scopes are built,
            // and only on the outermost program — a test's own ProgramNode has no further
            // tests to validate (nested tests already errored above).
            if (parentProgram == null)
            {
                program.EnforceStrictTestScopes();
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

        static void CheckStatements(this List<IStatementNode> statements, Scope scope, EnsureTypeContext ctx, IAstNode[] inheritTransitiveTypeFlagsFrom=null)
        {
            // foreach (var statement in statements)
            for (var i = 0 ; i < statements.Count; i ++)
            {
                var statement = statements[i];

                if (inheritTransitiveTypeFlagsFrom != null)
                {
                    statement.Visit(v =>
                    {
                        foreach (var x in inheritTransitiveTypeFlagsFrom)
                        {
                            if (x == null) continue;
                            v.ApplyTransitiveTypeFlags(x);
                        }
                    });
                }
                
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
                        // an assignment statement RESETS the transitive nature. 
                        assignment.variable.TransitiveFlags = assignment.expression.TransitiveFlags;
                        switch (assignment.variable)
                        {
                            case StructFieldReference fieldRef:
                                
                                
                                
                                fieldRef.EnsureStructField(scope, ctx, assignment: true);
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

                        if (assignment.variable is AstNode node && node.ParsedType.unset)
                        {
                            node.ParsedType = assignment.expression.ParsedType;
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
                            CheckStatements(caseGroup.statements, scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]{switchStatement.expression});
                        if (switchStatement.defaultCase != null)
                        {
                            CheckStatements(switchStatement.defaultCase.statements, scope, ctx, new IAstNode[]{switchStatement.expression});
                        }
                        break;
                    case ForStatement forStatement:
                        
                        if (forStatement.variableNode is VariableRefNode forVariable)
                        {
                            if (!scope.TryAddVariable(forVariable, out var existingSymbol))
                            {
                            }
                            forVariable.ParsedType = existingSymbol.typeInfo;
                            // TODO: remove any existing transitive properties?? 
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
                        forStatement.statements.CheckStatements(scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]
                        {
                            forStatement.startValueExpression, forStatement.endValueExpression, forStatement.stepValueExpression
                        });
                        scope.EndLoop();
                        
                        break;
                    case IfStatement ifStatement:
                        ifStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        scope.EnforceTypeAssignment(ifStatement.condition, ifStatement.condition.ParsedType, TypeInfo.Int, false, out _);

                        ifStatement.positiveStatements?.CheckStatements(scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]{ifStatement.condition});
                        ifStatement.negativeStatements?.CheckStatements(scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]{ifStatement.condition});
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
                        whileStatement.statements.CheckStatements(scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]{whileStatement.condition});
                        scope.EndLoop();
                        break;
                    case RepeatUntilStatement repeatStatement:
                        
                        scope.BeginLoop();
                        repeatStatement.statements.CheckStatements(scope, ctx);
                        scope.EndLoop();
                        repeatStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        scope.EnforceTypeAssignment(repeatStatement.condition, repeatStatement.condition.ParsedType, TypeInfo.Int, false, out _);
                       
                        scope.BeginLoop();
                        repeatStatement.statements.CheckStatements(scope, ctx, inheritTransitiveTypeFlagsFrom: new IAstNode[]{repeatStatement.condition});
                        scope.EndLoop();
                        
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
                    
                    case DeferStatement deferStatement:
                        deferStatement.statements?.CheckStatements(scope, ctx);
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
                    case AssertStatement assertStatement:
                        // `assert` is legal anywhere. Inside a test, strict-scope
                        // enforcement is handled by TestScopeStrictnessVisitor.
                        // Here we resolve symbols in the condition + reason so
                        // general "unknown symbol" errors still surface.
                        if (assertStatement.condition != null)
                        {
                            assertStatement.condition.EnsureVariablesAreDefined(scope, ctx);
                        }
                        if (assertStatement.reason != null)
                        {
                            assertStatement.reason.EnsureVariablesAreDefined(scope, ctx);
                            if (assertStatement.reason.ParsedType.type != VariableType.String
                                && !assertStatement.reason.ParsedType.unset)
                            {
                                assertStatement.Errors.Add(new ParseError(assertStatement.reason, ErrorCodes.AssertReasonMustBeString));
                            }
                        }
                        break;
                    case RuntoStatement runtoStatement:
                        // Runto target validation happens in the TestScopeStrictnessVisitor.
                        // Here we just resolve the target label's symbol so the
                        // LSP can offer go-to-definition + find-references on
                        // `runto labelName` sites.
                        if (!scope.IsInsideTest)
                        {
                            runtoStatement.Errors.Add(new ParseError(runtoStatement.StartToken, ErrorCodes.RuntoOutsideTest));
                        }
                        if (runtoStatement.targetLabel != null
                            && scope.TryGetLabel(runtoStatement.targetLabel, out var runtoLabelSymbol))
                        {
                            runtoStatement.DeclaredFromSymbol = runtoLabelSymbol;
                        }
                        if (runtoStatement.maxCyclesExpression != null)
                        {
                            runtoStatement.maxCyclesExpression.EnsureVariablesAreDefined(scope, ctx);
                        }
                        break;
                    case MockStatement mockStatement:
                        if (!scope.IsInsideTest)
                        {
                            mockStatement.Errors.Add(new ParseError(mockStatement.StartToken, ErrorCodes.MockOutsideTest));
                        }
                        ValidateMockStatement(mockStatement, scope, ctx);
                        break;
                    case ClearMockStatement clearMockStatement:
                        if (!scope.IsInsideTest)
                        {
                            clearMockStatement.Errors.Add(new ParseError(clearMockStatement.StartToken, ErrorCodes.ClearMockOutsideTest));
                        }
                        ValidateClearMockStatement(clearMockStatement);
                        break;
                    default:
                        throw new NotImplementedException($"cannot check statement for scope errors - {statement.GetType().Name} {statement}");
                        // break;
                }
            }
        }
        
        // mock and clear-mock validation. Command-existence is enforced by the
        // lexer's CommandNameTree pass (an unknown command name doesn't tokenize
        // as CommandWord, so the parser already errors). Here we walk the entry
        // expressions to catch general scope errors (unknown variable refs in
        // `returns` expressions, etc.) and emit unreachable-entry warnings.
        static void ValidateMockStatement(MockStatement mock, Scope scope, EnsureTypeContext ctx)
        {
            var sawAlways = false;
            for (var i = 0; i < mock.entries.Count; i++)
            {
                var entry = mock.entries[i];
                if (sawAlways)
                {
                    entry.Errors.Add(new ParseError(entry.StartToken, ErrorCodes.MockUnreachableEntry));
                }
                if (entry.frequency == MockFrequencyKind.Always) sawAlways = true;

                if (entry.returnExpression != null)
                {
                    entry.returnExpression.EnsureVariablesAreDefined(scope, ctx);
                }
                if (entry.countExpression != null)
                {
                    entry.countExpression.EnsureVariablesAreDefined(scope, ctx);
                }
            }
        }

        static void ValidateClearMockStatement(ClearMockStatement clear)
        {
            // Nothing to validate at the scope level — the parser already
            // checked for `mock <name>` / `mocks` shape, and the command name
            // (if present) was a CommandWord token (so it's known to the
            // command collection).
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
            // symbol.transitiveTypeFlags |= fieldRef.TransitiveFlags;
            // symbol.source.TransitiveFlags |= fieldRef.TransitiveFlags;
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

        public static void ApplyTransitiveTypeFlags(this IAstNode node, IAstNode other)
        {
            node.TransitiveFlags |= other.TransitiveFlags;
        }

        static void EnsureStructField(this StructFieldReference fieldRef, Scope scope, EnsureTypeContext ctx, bool assignment=false)
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

                    if (assignment)
                    {
                        // fieldRef.TransitiveFlags = symbol.transitiveTypeFlags;
                        symbol.transitiveTypeFlags |= fieldRef.TransitiveFlags;
                    }
                    else
                    {
                        fieldRef.TransitiveFlags |= symbol.transitiveTypeFlags;
                        // symbol.transitiveTypeFlags |= fieldRef.TransitiveFlags;
                    }
                    // fieldRef.TransitiveFlags |= symbol.transitiveTypeFlags;
                    // symbol.source.TransitiveFlags |= fieldRef.TransitiveFlags;
                    // fieldRef.TransitiveFlags = symbol.transitiveTypeFlags;

                    if (fieldRef.left is AstNode leftNode)
                    {
                        leftNode.ParsedType = symbol.typeInfo;
                        leftNode.DeclaredFromSymbol = symbol;
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
                    unaryOpExpr.ApplyTransitiveTypeFlags(unaryOpExpr.rhs);
                    break;
                case StructFieldReference structRef:
                    structRef.EnsureStructField(scope, ctx);
                    structRef.ApplyTransitiveTypeFlags(structRef.left);
                    structRef.ApplyTransitiveTypeFlags(structRef.right);
                    break;
                case CommandExpression commandExpr: // commandExprs have the ability to declare variables!
                    scope.AddCommand(commandExpr, ctx);

                    // all command return values are haunted, because we cannot know the value without running the program. 
                    commandExpr.TransitiveFlags |= TransitiveTypeFlags.Haunted;
                    // foreach (var arg in commandExpr.args)
                    // {
                    //     commandExpr.ApplyTransitiveTypeFlags(arg);
                    // }
                    
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
                            
                            // if the function is not a top level, and the current scope IS top level; then we have an issue.
                            if (function.region != FunctionStatement.REGION_TOP_LEVEL && scope.currentRegionName.Peek() == FunctionStatement.REGION_TOP_LEVEL)
                            {
                                arrayRef.Errors.Add(new ParseError(arrayRef.startToken, ErrorCodes.CannotCallTestFunctionFromOutsideTest));
                            }
                            
                            TypeInfo functionType = default;
                            arrayRef.TransitiveFlags |= function.TransitiveFlags;
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
                            
                                arrayRef.TransitiveFlags |= function.TransitiveFlags;


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


                                    arrayRef.TransitiveFlags |= argExr.TransitiveFlags;
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

                    if (arraySymbol != null)
                    {
                        arrayRef.DeclaredFromSymbol = arraySymbol;
                        arrayRef.TransitiveFlags |= arraySymbol.transitiveTypeFlags;
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
                        arrayRef.TransitiveFlags |= rankExpr.TransitiveFlags;
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