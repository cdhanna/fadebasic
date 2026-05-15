using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Ast.Visitors
{
    /// <summary>
    /// Enforces the strict <c>scope_at(:L)</c> semantics from TEST_DESIGN.md §5.
    /// A test body sees only:
    /// <list type="bullet">
    /// <item>Test-locals (declared via <c>local</c> in the test).</item>
    /// <item>Test-functions (declared via <c>function</c> inside the test).</item>
    /// <item>Always-visible globals (declared via <c>global X</c> at top level).</item>
    /// <item>Names declared by program top-level execution up to the most recent <c>runto</c> target.</item>
    /// </list>
    /// References to program-declared names that are <em>not</em> visible at the
    /// current point are flagged with <c>TestVariableUnreachable</c> (no runto has
    /// reached the declaration yet) or <c>TestVariableNotYetDeclared</c> (the
    /// runto target is earlier than the declaration).
    /// </summary>
    public static class TestScopeStrictnessVisitor
    {
        public static void EnforceStrictTestScopes(this ProgramNode program)
        {
            var scopeAt = ComputeTopLevelScopeAt(program, out var globalNames, out var allTopLevelNames);

            // Extend scope_at to cover function-internal labels too.
            // For a label inside a function, the visible names are:
            //   globals + function params + function locals declared up to that label
            // (Main-body names visible at the function's callsites are NOT included
            // here — that pulls in callsite-intersection analysis which we defer.
            // Users who want test-visibility for shared variables should use `global`.)
            ComputeFunctionInternalScopeAts(program, globalNames, scopeAt, allTopLevelNames);

            foreach (var test in program.tests)
            {
                ValidateTest(test, scopeAt, globalNames, allTopLevelNames);
            }
        }

        private static void ComputeFunctionInternalScopeAts(
            ProgramNode program,
            HashSet<string> globalNames,
            Dictionary<string, HashSet<string>> scopeAt,
            HashSet<string> allTopLevelNames)
        {
            foreach (var fn in program.functions)
            {
                var fnState = new HashSet<string>(globalNames, StringComparer.OrdinalIgnoreCase);
                if (fn.parameters != null)
                {
                    foreach (var param in fn.parameters)
                    {
                        if (param.variable != null)
                        {
                            fnState.Add(param.variable.variableName);
                            allTopLevelNames.Add(param.variable.variableName);
                        }
                    }
                }
                WalkStatements(fn.statements, fnState, scopeAt);
                // Add function-local names to allTopLevelNames so the test validator
                // can distinguish "declared but not visible from this runto" vs
                // "doesn't exist at all".
                foreach (var n in fnState) allTopLevelNames.Add(n);
            }
        }

        // Walk program top-level statements once, accumulating declared names in
        // source order. At each label declaration, snapshot the current set.
        // Globals (`global X = ...`) are present from the start; bare top-level
        // assignments (`x = 5`) get added when their statement is reached.
        // Branch rule: both arms of `if/else` contribute their names at the merge
        // point (matches Fade's existing semantics).
        private static Dictionary<string, HashSet<string>> ComputeTopLevelScopeAt(
            ProgramNode program,
            out HashSet<string> globalNames,
            out HashSet<string> allTopLevelNames)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var globals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First pass: collect globals (always visible).
            program.Visit(node =>
            {
                if (node is DeclarationStatement decl
                    && decl.scopeType == DeclarationScopeType.Global
                    && IsAtTopLevel(node, program))
                {
                    globals.Add(decl.variable);
                }
            });

            var current = new HashSet<string>(globals, StringComparer.OrdinalIgnoreCase);
            WalkStatements(program.statements, current, result);

            globalNames = globals;
            allTopLevelNames = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static bool IsAtTopLevel(IAstNode node, ProgramNode program)
        {
            // Heuristic: a node is "top-level" if it appears in program.statements,
            // not inside any function body. The Visit method walks recursively, so
            // we need a cheap check. For simplicity, use the start token's depth in
            // function bodies — if any function contains the node, it's not top-level.
            foreach (var fn in program.functions)
            {
                foreach (var stmt in fn.statements)
                {
                    if (ReferenceEquals(stmt, node)) return false;
                    if (stmt is IAstVisitable visitable)
                    {
                        var found = visitable.FindFirst(n => ReferenceEquals(n, node));
                        if (found != null) return false;
                    }
                }
            }
            return true;
        }

        private static void WalkStatements(
            IEnumerable<IStatementNode> stmts,
            HashSet<string> current,
            Dictionary<string, HashSet<string>> result)
        {
            foreach (var stmt in stmts)
            {
                switch (stmt)
                {
                    case LabelDeclarationNode label:
                        // Snapshot the visible-names set at this label's position.
                        result[label.label] = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
                        break;

                    case DeclarationStatement decl:
                        current.Add(decl.variable);
                        break;

                    case AssignmentStatement asn when asn.variable is VariableRefNode vref:
                        current.Add(vref.variableName);
                        break;

                    case CommandStatement cmd:
                        // Ref-args at top level introduce variables — the base
                        // scope checker registers them via Scope.AddCommand ->
                        // TryAddVariable. Mirror that here so the strict
                        // test-scope check knows the binding exists.
                        if (cmd.command.args != null && cmd.argMap != null)
                        {
                            for (var i = 0; i < cmd.args.Count && i < cmd.argMap.Count; i++)
                            {
                                var descIdx = cmd.argMap[i];
                                if (descIdx < 0 || descIdx >= cmd.command.args.Length) continue;
                                if (cmd.command.args[descIdx].isRef
                                    && cmd.args[i] is VariableRefNode refV)
                                {
                                    current.Add(refV.variableName);
                                }
                            }
                        }
                        break;

                    case ForStatement forStmt:
                        if (forStmt.variableNode is VariableRefNode forVar)
                        {
                            current.Add(forVar.variableName);
                        }
                        WalkStatements(forStmt.statements, current, result);
                        break;

                    case WhileStatement whileStmt:
                        WalkStatements(whileStmt.statements, current, result);
                        break;

                    case DoLoopStatement doStmt:
                        WalkStatements(doStmt.statements, current, result);
                        break;

                    case RepeatUntilStatement repeatStmt:
                        WalkStatements(repeatStmt.statements, current, result);
                        break;

                    case IfStatement ifStmt:
                        // Both branches contribute names — Fade's existing
                        // branch-merge semantics.
                        if (ifStmt.positiveStatements != null)
                        {
                            WalkStatements(ifStmt.positiveStatements, current, result);
                        }
                        if (ifStmt.negativeStatements != null)
                        {
                            WalkStatements(ifStmt.negativeStatements, current, result);
                        }
                        break;

                    case SwitchStatement switchStmt:
                        if (switchStmt.cases != null)
                        {
                            foreach (var c in switchStmt.cases)
                            {
                                if (c.statements != null) WalkStatements(c.statements, current, result);
                            }
                        }
                        if (switchStmt.defaultCase?.statements != null)
                        {
                            WalkStatements(switchStmt.defaultCase.statements, current, result);
                        }
                        break;
                }
            }
        }

        private static void ValidateTest(
            TestNode test,
            Dictionary<string, HashSet<string>> scopeAt,
            HashSet<string> globalNames,
            HashSet<string> allTopLevelNames)
        {
            var testProgram = test.testProgram;
            var testLocals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var testFunctions = new HashSet<string>(
                testProgram.functions.Select(f => f.name),
                StringComparer.OrdinalIgnoreCase);

            // Visible program-scope names. Starts with globals only (pre-runto).
            // Updated to scope_at(target) when a runto is encountered.
            var visible = new HashSet<string>(globalNames, StringComparer.OrdinalIgnoreCase);
            string currentRuntoTarget = null;

            void VisitStatement(IStatementNode stmt)
            {
                switch (stmt)
                {
                    case RuntoStatement runto:
                        if (scopeAt.TryGetValue(runto.targetLabel, out var snapshot))
                        {
                            visible = new HashSet<string>(snapshot, StringComparer.OrdinalIgnoreCase);
                            currentRuntoTarget = runto.targetLabel;

                            // Runto-induced visibility colliding with a
                            // test-local is a real conflict (globals are
                            // always-shadowable so we exclude them).
                            foreach (var name in visible)
                            {
                                if (globalNames.Contains(name)) continue;
                                if (testLocals.Contains(name))
                                {
                                    runto.Errors.Add(new ParseError(
                                        runto.targetLabelToken ?? runto.StartToken ?? runto.EndToken,
                                        ErrorCodes.TestRuntoShadowsLocal,
                                        name));
                                }
                            }
                        }
                        else
                        {
                            // Unknown label -> hard parse error. Leave visible /
                            // currentRuntoTarget unchanged so subsequent refs
                            // aren't double-flagged with a misleading
                            // TestVariableNotYetDeclared.
                            runto.Errors.Add(new ParseError(
                                runto.targetLabelToken ?? runto.StartToken ?? runto.EndToken,
                                ErrorCodes.RuntoUnknownLabel,
                                runto.targetLabel));
                        }
                        break;

                    case DeclarationStatement decl when decl.scopeType == DeclarationScopeType.Local:
                        // Declaring a test-local for a name that's currently
                        // visible from a runto'd program scope is a conflict
                        // (globals are excluded — they're always shadowable).
                        if (visible.Contains(decl.variable) && !globalNames.Contains(decl.variable))
                        {
                            decl.Errors.Add(new ParseError(decl.StartToken,
                                ErrorCodes.TestRuntoShadowsLocal, decl.variable));
                        }
                        testLocals.Add(decl.variable);
                        if (decl.initializerExpression != null)
                        {
                            CheckExpression(decl.initializerExpression, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        }
                        break;

                    case AssignmentStatement asn:
                        // RHS must be visible.
                        CheckExpression(asn.expression, testLocals, testFunctions,
                            visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        // LHS: bare `name = expr` follows BASIC's rule —
                        // assigning to an unbound name creates a fresh local in
                        // the enclosing scope (here, the test). When `name` IS
                        // visible from a runto'd program scope, the assignment
                        // writes through to that program-scope variable (intentional
                        // state setup), so no implicit-local is created.
                        if (asn.variable is VariableRefNode vref)
                        {
                            if (!testLocals.Contains(vref.variableName)
                                && !visible.Contains(vref.variableName))
                            {
                                testLocals.Add(vref.variableName);
                            }
                        }
                        break;

                    case AssertStatement assert:
                        if (assert.condition != null)
                        {
                            CheckExpression(assert.condition, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        }
                        if (assert.reason != null)
                        {
                            CheckExpression(assert.reason, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        }
                        break;

                    case IfStatement ifStmt:
                        if (ifStmt.condition != null)
                        {
                            CheckExpression(ifStmt.condition, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        }
                        if (ifStmt.positiveStatements != null)
                            foreach (var s in ifStmt.positiveStatements) VisitStatement(s);
                        if (ifStmt.negativeStatements != null)
                            foreach (var s in ifStmt.negativeStatements) VisitStatement(s);
                        break;

                    case ForStatement forStmt:
                        if (forStmt.variableNode is VariableRefNode forVar) testLocals.Add(forVar.variableName);
                        if (forStmt.startValueExpression != null)
                            CheckExpression(forStmt.startValueExpression, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        if (forStmt.endValueExpression != null)
                            CheckExpression(forStmt.endValueExpression, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        if (forStmt.stepValueExpression != null)
                            CheckExpression(forStmt.stepValueExpression, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        if (forStmt.statements != null)
                            foreach (var s in forStmt.statements) VisitStatement(s);
                        break;

                    case WhileStatement whileStmt:
                        if (whileStmt.condition != null)
                            CheckExpression(whileStmt.condition, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        if (whileStmt.statements != null)
                            foreach (var s in whileStmt.statements) VisitStatement(s);
                        break;

                    case DoLoopStatement doStmt:
                        if (doStmt.statements != null)
                            foreach (var s in doStmt.statements) VisitStatement(s);
                        break;

                    case RepeatUntilStatement repeatStmt:
                        if (repeatStmt.statements != null)
                            foreach (var s in repeatStmt.statements) VisitStatement(s);
                        if (repeatStmt.condition != null)
                            CheckExpression(repeatStmt.condition, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        break;

                    case SwitchStatement switchStmt:
                        if (switchStmt.expression != null)
                            CheckExpression(switchStmt.expression, testLocals, testFunctions,
                                visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        if (switchStmt.cases != null)
                            foreach (var c in switchStmt.cases)
                                if (c.statements != null)
                                    foreach (var s in c.statements) VisitStatement(s);
                        if (switchStmt.defaultCase?.statements != null)
                            foreach (var s in switchStmt.defaultCase.statements) VisitStatement(s);
                        break;

                    case CommandStatement cmd:
                        // Ref-args with bare names follow the AssignmentStatement
                        // LHS rule: known-but-not-visible -> error; otherwise
                        // implicit test-local. Everything else flows through the
                        // standard expression check.
                        if (cmd.command.args != null && cmd.argMap != null)
                        {
                            for (var i = 0; i < cmd.args.Count; i++)
                            {
                                var argExpr = cmd.args[i];
                                var descIdx = i < cmd.argMap.Count ? cmd.argMap[i] : -1;
                                var isRef = descIdx >= 0
                                    && descIdx < cmd.command.args.Length
                                    && cmd.command.args[descIdx].isRef;
                                var refVref = isRef ? argExpr as VariableRefNode : null;

                                if (refVref != null)
                                {
                                    var name = refVref.variableName;
                                    if (testLocals.Contains(name) || visible.Contains(name))
                                    {
                                        // already in scope; ref read/write is fine
                                    }
                                    else if (allTopLevelNames.Contains(name))
                                    {
                                        AddVisibilityError(cmd, name, currentRuntoTarget);
                                    }
                                    else
                                    {
                                        testLocals.Add(name);
                                    }
                                }
                                else
                                {
                                    CheckExpression(argExpr, testLocals, testFunctions,
                                        visible, currentRuntoTarget, globalNames, allTopLevelNames);
                                }
                            }
                        }
                        break;

                    case ExpressionStatement expStmt:
                        CheckExpression(expStmt.expression, testLocals, testFunctions,
                            visible, currentRuntoTarget, globalNames, allTopLevelNames);
                        break;

                    case FunctionStatement _:
                        // Function bodies are validated independently; their own
                        // parameters/locals are scoped within. Don't descend.
                        break;
                }
            }

            void AddVisibilityError(IAstNode node, string name, string runtoTarget)
            {
                var code = runtoTarget == null
                    ? ErrorCodes.TestVariableUnreachable
                    : ErrorCodes.TestVariableNotYetDeclared;
                node.Errors.Add(new ParseError(node.StartToken ?? node.EndToken, code, name));
            }

            // Check an expression's variable references.
            void CheckExpression(IExpressionNode expr,
                HashSet<string> testLocalsRef,
                HashSet<string> testFunctionsRef,
                HashSet<string> visibleRef,
                string runtoTargetRef,
                HashSet<string> globalsRef,
                HashSet<string> allNamesRef)
            {
                Walk(expr);

                void Walk(IAstVisitable node)
                {
                    if (node == null) return;
                    switch (node)
                    {
                        case VariableRefNode vref:
                            var name = vref.variableName;
                            if (testLocalsRef.Contains(name)) return;
                            if (testFunctionsRef.Contains(name)) return;
                            if (visibleRef.Contains(name)) return;
                            // Known program name but unreachable from here -> strict error.
                            // Unknown to allNames is handled by the main scope checker.
                            if (allNamesRef.Contains(name))
                            {
                                AddVisibilityError(vref, name, runtoTargetRef);
                            }
                            break;

                        case StructFieldReference sfr:
                            // The `right` side is a field name on the type, not
                            // a variable lookup — skip it. The `left` may itself
                            // be a struct ref / array ref / var ref; recurse.
                            Walk(sfr.left);
                            break;

                        default:
                            foreach (var child in node.IterateChildNodes())
                            {
                                Walk(child);
                            }
                            break;
                    }
                }
            }

            foreach (var stmt in testProgram.statements)
            {
                VisitStatement(stmt);
            }
        }
    }
}
