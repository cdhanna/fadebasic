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
            // Plumb the program's CommandCollection onto the scope so visitors
            // can look up command metadata (return type, args) without taking
            // it as a parameter. Test sub-programs inherit from the parent.
            scope.commands = program.commands ?? parentProgram?.commands;

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

            // Flag duplicate test names (case-insensitive — matches the
            // lookup semantics used by FindTestByName + the runner's
            // manifest lookup). The first occurrence keeps the name; every
            // later sibling with the same name gets an error pinned on its
            // own name token. We don't drop them from validation — the
            // user might want to fix one at a time, and downstream checks
            // still produce useful errors for both bodies.
            {
                var seenTestNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in program.tests)
                {
                    if (t.name == null) continue;
                    if (!seenTestNames.Add(t.name))
                    {
                        Token dupTok = t.nameToken ?? t.StartToken;
                        t.Errors.Add(new ParseError(dupTok,
                            ErrorCodes.TestDuplicateName, t.name));
                    }
                }
            }
            //
            // A test with `from <parent>` must validate AFTER its parent so we can
            // pass the parent's testProgram as the scope baseline — the same
            // program→test copy logic above then folds parent's locals/functions
            // into the child's fresh scope. Order tests Kahn-style by from-chain;
            // anything still unordered after a full pass is in a cycle (the strict
            // visitor will flag it) and falls back to the program baseline so the
            // child still validates against globals and doesn't lose unrelated
            // errors.
            var orderedTests = OrderTestsByFromChain(program.tests);
            foreach (var test in orderedTests)
            {
                // Tests cannot be nested inside another test. parentProgram != null
                // means *we* are already a test sub-program, so any tests we contain
                // are an invalid nesting.
                if (parentProgram != null)
                {
                    Token nestingTok = test.nameToken ?? test.StartToken;
                    test.Errors.Add(new ParseError(nestingTok, ErrorCodes.TestNestingNotAllowed));
                    continue;
                }

                // Default baseline = the outer program (program-level globals,
                // labels, types, functions). If this test has a resolvable,
                // already-validated parent test, use the parent's testProgram
                // instead so child picks up parent's locals/functions on top
                // of the program baseline (parent's own validation already
                // folded the program-level state into its scope, so the
                // baselines compose transitively).
                ProgramNode baseline = program;
                if (test.fromParent != null)
                {
                    var parentTest = FindTestByName(program.tests, test.fromParent);
                    if (parentTest != null
                        && parentTest != test
                        && parentTest.testProgram.scope != null)
                    {
                        baseline = parentTest.testProgram;
                    }
                }
                test.testProgram.AddScopeRelatedErrors(options, knownFunctionTypes, baseline);
            }

            // Strict scope_at(:L) enforcement runs after all test sub-scopes are built,
            // and only on the outermost program — a test's own ProgramNode has no further
            // tests to validate (nested tests already errored above).
            if (parentProgram == null)
            {
                program.EnforceStrictTestScopes();
            }
        }


        // Locate a sibling test by name (case-insensitive). Used by the
        // test-iteration loop to resolve `from <parent>` references. Returns
        // null when the parent name doesn't match any test — the strict
        // visitor handles that with a clean TestFromParentUnknown error;
        // here we just fall back to using the outer program as the baseline.
        static TestNode FindTestByName(List<TestNode> tests, string name)
        {
            if (name == null || tests == null) return null;
            foreach (var t in tests)
            {
                if (t.name != null
                    && string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }
            return null;
        }

        // Order tests so each child appears after its `from`-parent. Anything
        // unreachable (cycle members, tests whose chain hits an unknown name)
        // appends at the end and gets validated against the outer program
        // baseline — preserves error coverage without infinite-recursing.
        // Kahn-style: repeatedly emit any test whose parent has been emitted
        // (or whose parent is missing/null), until no progress is possible.
        static List<TestNode> OrderTestsByFromChain(List<TestNode> tests)
        {
            var byName = new Dictionary<string, TestNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tests)
            {
                if (t.name != null) byName[t.name] = t;
            }

            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<TestNode>(tests.Count);
            var pending = new List<TestNode>(tests);

            bool TryEmit(TestNode t)
            {
                if (t.name == null) return false;
                if (emitted.Contains(t.name)) return true;
                // Tests with no parent, an unknown parent, or a parent that
                // resolves to themselves can emit immediately — there's
                // nothing to wait for in the inheritance chain.
                if (t.fromParent == null
                    || !byName.TryGetValue(t.fromParent, out var parent)
                    || parent == t)
                {
                    result.Add(t);
                    emitted.Add(t.name);
                    return true;
                }
                if (!emitted.Contains(parent.name)) return false;
                result.Add(t);
                emitted.Add(t.name);
                return true;
            }

            var madeProgress = true;
            while (pending.Count > 0 && madeProgress)
            {
                madeProgress = false;
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    if (TryEmit(pending[i]))
                    {
                        pending.RemoveAt(i);
                        madeProgress = true;
                    }
                }
            }
            // Anything still pending is in a cycle. Append them in source
            // order with no parent-state inheritance available — they'll
            // validate against the program baseline. The strict visitor
            // has already flagged the cycle.
            foreach (var t in pending) result.Add(t);
            return result;
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
        // lexer's CommandNameTree pass (an unknown command name doesn't
        // tokenize as CommandWord, so the parser already errors). Here we:
        //   - Walk body expressions for unknown-symbol errors.
        //   - Enforce body structure: at most one `returns`, at most one
        //     `forbid`, never both.
        //   - Type-check the `forbid` reason expression (string).
        //   - Validate the `returns` expression against the command's
        //     declared return type. Multi-overload commands must accept the
        //     same expression for every overload, so we intersect: if any
        //     overload would reject the expression, error.
        static void ValidateMockStatement(MockStatement mock, Scope scope, EnsureTypeContext ctx)
        {
            MockExitMockStatement seenReturns = null;
            MockForbidStatement seenForbid = null;

            // Collect structure-validation findings up front (multiple returns,
            // multiple forbids, returns+forbid). We still need to walk the
            // body with the full visitor so locals/ifs/etc. type-check, but
            // we can detect these duplicates by scanning the body shallowly.
            foreach (var stmt in mock.body)
            {
                switch (stmt)
                {
                    case MockExitMockStatement rs:
                        if (seenReturns != null)
                        {
                            rs.Errors.Add(new ParseError(rs.StartToken, ErrorCodes.MockMultipleReturns));
                        }
                        seenReturns = rs;
                        break;

                    case MockForbidStatement fs:
                        if (seenForbid != null)
                        {
                            fs.Errors.Add(new ParseError(fs.StartToken, ErrorCodes.MockMultipleForbid));
                        }
                        seenForbid = fs;
                        if (fs.reason != null
                            && fs.reason.ParsedType.type != VariableType.String
                            && !fs.reason.ParsedType.unset)
                        {
                            // Type check happens after we visit the body
                            // (so the reason expression's ParsedType is set).
                            // We re-check after CheckStatements below.
                        }
                        break;
                }
            }

            // Push a body scope. Parameters become locals with types derived
            // from the command's arg metadata. This mirrors BeginFunction:
            // the body's local symbol table is independent of the test's,
            // and `local` declarations inside the body add to it.
            //
            // Pick the overload that MATCHES the user's named param count.
            // Falling back to overloads[0] would mis-type params and skip
            // the ref-assignment check when overloads have different arg
            // counts (e.g. `input(ref string)` vs `input(string, ref string)`).
            CommandInfo? bodyOverload = null;
            if (scope.commands != null
                && mock.commandName != null
                && scope.commands.Lookup.TryGetValue(mock.commandName, out var bodyOverloads)
                && bodyOverloads.Count > 0)
            {
                if (mock.parameters.Count == 0)
                {
                    bodyOverload = bodyOverloads[0];
                }
                else
                {
                    foreach (var ov in bodyOverloads)
                    {
                        var ovArgs = ov.args ?? System.Array.Empty<CommandArgInfo>();
                        var realCount = 0;
                        foreach (var a in ovArgs) if (!a.isVmArg) realCount++;
                        if (realCount == mock.parameters.Count)
                        {
                            bodyOverload = ov;
                            break;
                        }
                    }
                }
            }

            var bodyTable = new SymbolTable();
            scope.localVariables.Push(bodyTable);
            if (bodyOverload.HasValue && mock.parameters.Count > 0)
            {
                var args = bodyOverload.Value.args ?? System.Array.Empty<CommandArgInfo>();
                var realArgIndices = new List<int>();
                for (var ai = 0; ai < args.Length; ai++)
                {
                    if (!args[ai].isVmArg) realArgIndices.Add(ai);
                }
                for (var pi = 0; pi < mock.parameters.Count && pi < realArgIndices.Count; pi++)
                {
                    var p = mock.parameters[pi];
                    var argDesc = args[realArgIndices[pi]];
                    var typeCode = argDesc.typeCode;
                    if (argDesc.isParams && typeCode == TypeCodes.ANY)
                    {
                        // `params object[]` — TypeCodes.ANY has no Fade
                        // variable-type mapping, and the body's gathered
                        // array would need per-element type storage that
                        // the current array model doesn't support. Surface
                        // a clean error here instead of letting the compiler
                        // crash on SIZE_TABLE[ANY]. The user can still mock
                        // the command; they just can't reference the args.
                        var cmdName = mock.commandName ?? "<command>";
                        var detail =
                            $"`{p.variableName}` is bound to the `params object[]` parameter of `{cmdName}`. " +
                            $"That parameter accepts a mix of element types at runtime, so there's no single Fade " +
                            $"element type the body's array could have. Rewrite as `mock {cmdName}` (no parameter name) " +
                            $"to install the mock without naming the args.";
                        p.Errors.Add(new ParseError(p,
                            ErrorCodes.MockParamsObjectArrayUnnamable, detail));
                    }
                    else if (VmUtil.TryGetVariableType(typeCode, out var varType))
                    {
                        // A params arg is bound as a rank-1 array of the
                        // element type so the body can `len(p)` and `p(i)`.
                        var paramTypeInfo = argDesc.isParams
                            ? TypeInfo.FromVariableType(varType, new IExpressionNode[1])
                            : TypeInfo.FromVariableType(varType);
                        bodyTable.Add(p.variableName, new Symbol
                        {
                            text = p.variableName,
                            typeInfo = paramTypeInfo,
                            source = p
                        });
                    }
                }
            }

            // Set active-mock context so any PassthroughExpression we
            // encounter in the body knows what return type to wear and
            // doesn't trip its outside-mock-body error.
            var prevInsideMock = ctx.insideMockBody;
            var prevMockReturnTc = ctx.activeMockReturnTypeCode;
            var prevMockReturnInfo = ctx.activeMockReturnTypeInfo;
            var prevMockArgInfos = ctx.activeMockArgInfos;
            var prevMockBoundRefs = ctx.activeMockBoundRefParamNames;
            ctx.insideMockBody = true;
            ctx.activeMockReturnTypeCode = bodyOverload?.returnType ?? TypeCodes.VOID;
            if (bodyOverload.HasValue
                && bodyOverload.Value.returnType != TypeCodes.VOID
                && VmUtil.TryGetVariableType(bodyOverload.Value.returnType, out var mockRetVarType))
            {
                ctx.activeMockReturnTypeInfo = TypeInfo.FromVariableType(mockRetVarType);
            }
            else
            {
                ctx.activeMockReturnTypeInfo = TypeInfo.Void;
            }
            // Build the real-arg list (in declaration order) and the
            // set of names bound to a ref param. PassthroughExpression's
            // validator reads these.
            if (bodyOverload.HasValue)
            {
                var ovArgs = bodyOverload.Value.args ?? System.Array.Empty<CommandArgInfo>();
                var realArgsOrdered = new List<CommandArgInfo>();
                var boundRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var paramIdx = 0;
                for (var ai = 0; ai < ovArgs.Length; ai++)
                {
                    if (ovArgs[ai].isVmArg) continue;
                    realArgsOrdered.Add(ovArgs[ai]);
                    if (ovArgs[ai].isRef && paramIdx < mock.parameters.Count)
                    {
                        boundRefs.Add(mock.parameters[paramIdx].variableName);
                    }
                    paramIdx++;
                }
                ctx.activeMockArgInfos = realArgsOrdered.ToArray();
                ctx.activeMockBoundRefParamNames = boundRefs;
            }
            else
            {
                ctx.activeMockArgInfos = System.Array.Empty<CommandArgInfo>();
                ctx.activeMockBoundRefParamNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            // Walk the body with the standard statement checker so locals,
            // ifs, expression statements, asserts, etc. all get proper
            // type-checking and symbol resolution. The dedicated
            // MockExitMockStatement / MockForbidStatement / MockRefStatement
            // cases below resolve their internal expressions; the generic
            // dispatch handles the rest.
            foreach (var stmt in mock.body)
            {
                switch (stmt)
                {
                    case MockExitMockStatement rs:
                        if (rs.expression != null)
                        {
                            rs.expression.EnsureVariablesAreDefined(scope, ctx);
                        }
                        break;
                    case MockForbidStatement fs:
                        if (fs.reason != null)
                        {
                            fs.reason.EnsureVariablesAreDefined(scope, ctx);
                            if (fs.reason.ParsedType.type != VariableType.String
                                && !fs.reason.ParsedType.unset)
                            {
                                fs.Errors.Add(new ParseError(fs.reason, ErrorCodes.MockForbidReasonMustBeString));
                            }
                        }
                        break;
                    default:
                        // Send single-statement lists through CheckStatements
                        // so it shares all the path-aware infrastructure
                        // (loops, defers, declarations, etc.).
                        var oneShot = new List<IStatementNode> { stmt };
                        oneShot.CheckStatements(scope, ctx);
                        break;
                }
            }

            // `runto` is a test-flow primitive — it switches execution
            // between the test body and main-program code. It has no
            // sensible meaning inside a mock body (which is run when a
            // command is invoked, not when the test is navigating). Catch
            // any occurrence anywhere in the body tree, not just top-level,
            // so wrapping in `if`/`while` doesn't sneak past the check.
            foreach (var stmt in mock.body)
            {
                stmt.Visit(node =>
                {
                    if (node is RuntoStatement runtoNode)
                    {
                        runtoNode.Errors.Add(new ParseError(runtoNode.StartToken,
                            ErrorCodes.RuntoInsideMockBody));
                    }
                });
            }

            // Validate self-recursive calls to the mocked command — these
            // get rewritten to CALL_HOST_REAL with a scope swap, so any
            // ref arg's address must point into the caller's scope. The
            // only body-level names that satisfy that are the mock's own
            // bound ref params (their hidden ptr targets the caller).
            // Any other expression at a ref slot is a hard error.
            if (mock.commandName != null
                && ctx.activeMockBoundRefParamNames != null)
            {
                var boundRefNames = ctx.activeMockBoundRefParamNames;
                foreach (var stmt in mock.body)
                {
                    stmt.Visit(node =>
                    {
                        if (node is CommandStatement cs
                            && cs.command.name != null
                            && string.Equals(cs.command.name, mock.commandName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ValidateSelfRecursiveRefArgs(cs.command, cs.args,
                                cs.argMap, boundRefNames);
                        }
                        else if (node is CommandExpression ce
                            && ce.command.name != null
                            && string.Equals(ce.command.name, mock.commandName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ValidateSelfRecursiveRefArgs(ce.command, ce.args,
                                ce.argMap, boundRefNames);
                        }
                    });
                }
            }

            // Resolve symbols + type on `endmock <expr>` while the body
            // scope (with params) is still pushed.
            if (mock.endmockExpression != null)
            {
                mock.endmockExpression.EnsureVariablesAreDefined(scope, ctx);
            }

            // Strict ref-arg validation: every ref param must have at least
            // one top-level assignment in the body. Otherwise the caller's
            // variable is left in an undefined state when the mock runs.
            // `forbid` short-circuits this — the test halts before the
            // caller observes anything, so no writes are needed.
            if (bodyOverload.HasValue && seenForbid == null && mock.parameters.Count > 0)
            {
                var args = bodyOverload.Value.args ?? System.Array.Empty<CommandArgInfo>();
                var realArgIndices = new List<int>();
                for (var ai = 0; ai < args.Length; ai++)
                {
                    if (!args[ai].isVmArg) realArgIndices.Add(ai);
                }

                // A self-recursive call to the mocked command inside the
                // body invokes the real host, which writes through every
                // ref it's passed. If the body contains such a call (at
                // any nesting level) we treat every ref param as assigned
                // — the user delegated the writes to the real host. The
                // compiler still enforces, per call, that each ref arg
                // names a bound ref param (MockBodyRefArgMustBeBoundRefParam).
                var hasSelfCall = false;
                var mockedName = mock.commandName;
                if (mockedName != null)
                {
                    foreach (var stmt in mock.body)
                    {
                        stmt.Visit(node =>
                        {
                            if (node is CommandStatement cs
                                && cs.command.name != null
                                && string.Equals(cs.command.name, mockedName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                hasSelfCall = true;
                            }
                            if (node is CommandExpression ce
                                && ce.command.name != null
                                && string.Equals(ce.command.name, mockedName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                hasSelfCall = true;
                            }
                        });
                        if (hasSelfCall) break;
                    }
                }

                for (var pi = 0; pi < mock.parameters.Count && pi < realArgIndices.Count; pi++)
                {
                    if (!args[realArgIndices[pi]].isRef) continue;
                    if (hasSelfCall) continue;
                    var paramName = mock.parameters[pi].variableName;
                    var assigned = false;
                    foreach (var stmt in mock.body)
                    {
                        if (stmt is AssignmentStatement asn
                            && asn.variable is VariableRefNode lhs
                            && string.Equals(lhs.variableName, paramName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            assigned = true;
                            break;
                        }
                    }
                    if (!assigned)
                    {
                        mock.parameters[pi].Errors.Add(new ParseError(mock.parameters[pi],
                            ErrorCodes.MockRefParamNotAssigned));
                    }
                }
            }

            scope.localVariables.Pop();

            // Restore the outer ctx now that we're done walking this
            // mock body. Nested mocks aren't legal (mock is block-only at
            // the top level of a test), but this still keeps the stack
            // discipline tidy in case a future change allows them.
            ctx.insideMockBody = prevInsideMock;
            ctx.activeMockReturnTypeCode = prevMockReturnTc;
            ctx.activeMockReturnTypeInfo = prevMockReturnInfo;
            ctx.activeMockArgInfos = prevMockArgInfos;
            ctx.activeMockBoundRefParamNames = prevMockBoundRefs;

            if (seenReturns != null && seenForbid != null)
            {
                // `returns` + `forbid` in the same body is nonsensical — the
                // forbid prevents the return path from being reached.
                seenForbid.Errors.Add(new ParseError(seenForbid.StartToken, ErrorCodes.MockReturnsAndForbid));
            }

            // Look up the command in the scope's CommandCollection to validate
            // `returns` against the command's declared return type. We need
            // ALL overloads — a mock applies to every overload of the same
            // name, so the returns expression must satisfy every one of them.
            //
            // Type compatibility uses EnforceTypeAssignment so the same numeric
            // coercion rules that apply to `local n as long = 5` apply here:
            // an int literal is fine as a `returns` value for a long-returning
            // command, etc. Anything else surfaces an InvalidCast/InvalidType
            // error on the expression — we then translate the first such error
            // into a clearer MockReturnsTypeMismatch and stop.
            if (scope.commands != null && mock.commandName != null
                && scope.commands.Lookup.TryGetValue(mock.commandName, out var overloads)
                && overloads.Count > 0)
            {
                // When the user names params, at least one overload must
                // have a matching non-VmArg arg count. Otherwise the mock
                // can't bind cleanly and the compiler will refuse to emit
                // any body (silent no-op without an error).
                if (mock.parameters.Count > 0)
                {
                    var hasMatchingOverload = false;
                    foreach (var ov in overloads)
                    {
                        var ovArgs = ov.args ?? System.Array.Empty<CommandArgInfo>();
                        var realCount = 0;
                        foreach (var a in ovArgs) if (!a.isVmArg) realCount++;
                        if (realCount == mock.parameters.Count) { hasMatchingOverload = true; break; }
                    }
                    if (!hasMatchingOverload)
                    {
                        mock.Errors.Add(new ParseError(
                            mock.commandNameToken ?? mock.StartToken,
                            ErrorCodes.MockParamCountNoMatchingOverload));
                    }
                }

                // Strict body validation: a value-returning command's mock
                // body must produce a return value via one of three paths:
                //   - exitmock <expr> somewhere in the body (top level)
                //   - endmock <expr> as the closing form (fall-through)
                //   - forbid (the test halts before the caller observes the
                //     missing return)
                // Without one of these the caller pops a return value that
                // was never pushed → stack corruption at the call site.
                if (seenReturns == null && seenForbid == null && mock.endmockExpression == null)
                {
                    var anyValueReturning = false;
                    foreach (var ov in overloads)
                    {
                        if (ov.returnType != TypeCodes.VOID) { anyValueReturning = true; break; }
                    }
                    if (anyValueReturning)
                    {
                        mock.Errors.Add(new ParseError(mock.StartToken ?? mock.commandNameToken,
                            ErrorCodes.MockValueCommandMissingReturns));
                    }
                }

                // Return-type checks against each overload. Apply to both
                // `exitmock <expr>` (seenReturns) and `endmock <expr>`
                // (mock.endmockExpression). Each one must satisfy the
                // command's return type or — if the command is void — not
                // appear at all.
                CheckMockReturnAgainstOverloads(seenReturns?.expression,
                    seenReturns?.StartToken, overloads, scope);
                CheckMockReturnAgainstOverloads(mock.endmockExpression,
                    mock.endmockExpression?.StartToken, overloads, scope);
            }
        }

        // For a self-recursive call inside a mock body, each ref-position
        // user arg must be a VariableRefNode naming one of the mock's
        // bound ref params. Anything else would push a pointer that the
        // CALL_HOST_REAL scope swap can't make sense of (a body-local
        // address means "register N in body scope"; after the swap that
        // address indexes the wrong scope and would clobber unrelated
        // data). Skip non-ref slots — they're plain expressions and the
        // standard command-arg checks cover them.
        static void ValidateSelfRecursiveRefArgs(CommandInfo command,
            List<IExpressionNode> args, List<int> argMap,
            HashSet<string> boundRefNames)
        {
            if (command.args == null) return;
            var argCounter = 0;
            for (var i = 0; i < command.args.Length; i++)
            {
                if (command.args[i].isVmArg) continue;
                if (command.args[i].isParams) break;
                if (argCounter >= args.Count) break;
                if (command.args[i].isRef)
                {
                    var userExpr = args[argCounter];
                    if (!(userExpr is VariableRefNode vn)
                        || boundRefNames == null
                        || !boundRefNames.Contains(vn.variableName))
                    {
                        userExpr.Errors.Add(new ParseError(userExpr,
                            ErrorCodes.MockBodyRefArgMustBeBoundRefParam));
                    }
                }
                argCounter++;
            }
        }

        // Helper: validate a single return-expression (from exitmock or
        // endmock) against every overload of the mocked command. Adds the
        // appropriate error to the expression node on the first mismatch.
        static void CheckMockReturnAgainstOverloads(
            IExpressionNode returnExpr, Token reportToken,
            List<CommandInfo> overloads, Scope scope)
        {
            if (returnExpr == null) return;
            foreach (var overload in overloads)
            {
                if (overload.returnType == TypeCodes.VOID)
                {
                    returnExpr.Errors.Add(new ParseError(reportToken ?? returnExpr.StartToken,
                        ErrorCodes.MockReturnsOnVoidCommand));
                    return;
                }
                if (returnExpr.ParsedType.unset) continue;
                if (!TypeInfo.TryGetFromTypeCode(overload.returnType, out var expectedType)) continue;

                var probe = new ProbeNode();
                scope.EnforceTypeAssignment(probe,
                    returnExpr.ParsedType, expectedType,
                    softLeft: false, out _);
                if (probe.Errors.Count > 0)
                {
                    returnExpr.Errors.Add(new ParseError(returnExpr,
                        ErrorCodes.MockReturnsTypeMismatch));
                    return;
                }
            }
        }

        // Throwaway IAstNode used to capture errors from EnforceTypeAssignment
        // without polluting a real source node. EnforceTypeAssignment adds
        // ParseErrors to whatever node is passed in; we want to test assignment
        // legality without committing those errors to the user's expression.
        sealed class ProbeNode : IAstNode
        {
            public List<ParseError> Errors { get; } = new List<ParseError>();
            public Token StartToken => null;
            public Token EndToken => null;
            public TypeInfo ParsedType => TypeInfo.Unset;
            public TransitiveTypeFlags TransitiveFlags { get; set; }
            public Symbol DeclaredFromSymbol { get; set; }
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
                case CallCountExpression callCountExpr:
                    // `call count <cmd>` always evaluates to an int. The
                    // command name was already validated by the lexer's
                    // CommandNameTree pass; nothing further to do.
                    callCountExpr.ParsedType = TypeInfo.Int;
                    break;
                case LenExpression lenExpr:
                    // `len(...)` always evaluates to an int. Resolve inner
                    // expression first so its ParsedType is set, then
                    // validate it's array- or string-typed.
                    lenExpr.ParsedType = TypeInfo.Int;
                    if (lenExpr.inner != null)
                    {
                        lenExpr.inner.EnsureVariablesAreDefined(scope, ctx);
                        var innerType = lenExpr.inner.ParsedType;
                        if (!innerType.unset
                            && !innerType.IsArray
                            && innerType.type != VariableType.String)
                        {
                            lenExpr.inner.Errors.Add(new ParseError(lenExpr.inner,
                                ErrorCodes.LenInvalidType));
                        }
                    }
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

        // Set while walking the body of a `mock` statement. Drives:
        //  - PassthroughExpression validation: passthrough outside a mock
        //    body is an error.
        //  - PassthroughExpression ParsedType: set to the active mock
        //    command's return type so callers like `r = passthrough` get
        //    the right type info.
        public bool insideMockBody;
        public byte activeMockReturnTypeCode;
        public TypeInfo activeMockReturnTypeInfo;

        // Active mock command's full (non-VmArg) arg metadata, in
        // declaration order. PassthroughExpression uses this to validate
        // explicit `passthrough(...)` arg count + per-position kind
        // (value / ref / params).
        public CommandArgInfo[] activeMockArgInfos;

        // Names of the mock's bound REF parameters. A ref argument in
        // `passthrough(...)` must name one of these — otherwise we'd
        // hand the real command a ptr that doesn't actually target the
        // caller's scope and the writeback would land in the wrong place.
        public HashSet<string> activeMockBoundRefParamNames;

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