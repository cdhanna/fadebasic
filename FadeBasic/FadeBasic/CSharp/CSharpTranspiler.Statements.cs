using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    public partial class CSharpTranspiler
    {
        bool LineDirectivesEnabled => _options.SourceMap != null || _options.SourceFileName != null;

        void LineDirective(Token token)
        {
            if (!LineDirectivesEnabled || token == null) return;
            string file;
            int line;
            if (_options.SourceMap != null)
            {
                var loc = _options.SourceMap.GetOriginalLocation(token);
                file = loc.fileName;
                line = loc.startLine;
            }
            else
            {
                file = _options.SourceFileName;
                line = token.lineNumber;
            }

            if (file == null) return;
            if (line == _region.lastDirectiveLine && file == _region.lastDirectiveFile) return;
            _region.lastDirectiveLine = line;
            _region.lastDirectiveFile = file;
            _region.lines.Add($"#line {line + 1} {StringLiteral(file.Replace('\\', '/'))}");
        }

        void HiddenLines()
        {
            if (!LineDirectivesEnabled) return;
            _region.lines.Add("#line hidden");
            _region.lastDirectiveLine = -2;
        }

        int Site => _region.currentLine;

        void CompileStatement(IStatementNode statement)
        {
            if (statement is CommentStatement) return;
            if (statement.StartToken != null)
            {
                _region.currentLine = statement.StartToken.lineNumber;
                if (!(statement is FunctionStatement)) LineDirective(statement.StartToken);
            }

            switch (statement)
            {
                case DeclarationStatement declarationStatement:
                    CompileDeclaration(declarationStatement);
                    break;
                case RedimStatement redimStatement:
                    CompileRedim(redimStatement);
                    break;
                case AssignmentStatement assignmentStatement:
                    CompileAssignment(assignmentStatement);
                    break;
                case CommandStatement commandStatement:
                    CompileCommandStatement(commandStatement);
                    break;
                case LabelDeclarationNode labelStatement:
                    CompileLabel(labelStatement);
                    break;
                case GotoStatement gotoStatement:
                    Line($"goto @@L:{gotoStatement.label}@@;");
                    break;
                case GoSubStatement goSubStatement:
                    CompileGosub(goSubStatement);
                    break;
                case ReturnStatement _:
                    CompileGosubReturn();
                    break;
                case EndProgramStatement _:
                    CompileEnd();
                    break;
                case IfStatement ifStatement:
                    CompileIf(ifStatement);
                    break;
                case ExitLoopStatement _:
                    if (_region.loops.Count == 0) throw new CSharpTranspileException("exit outside of a loop", statement);
                    Line(_region.loops.Peek().exit);
                    break;
                case SkipLoopStatement _:
                    if (_region.loops.Count == 0) throw new CSharpTranspileException("skip outside of a loop", statement);
                    Line(_region.loops.Peek().skip);
                    break;
                case WhileStatement whileStatement:
                    CompileWhile(whileStatement);
                    break;
                case RepeatUntilStatement repeatUntilStatement:
                    CompileRepeat(repeatUntilStatement);
                    break;
                case ForStatement forStatement:
                    CompileFor(forStatement);
                    break;
                case DoLoopStatement doLoopStatement:
                    CompileDoLoop(doLoopStatement);
                    break;
                case SwitchStatement switchStatement:
                    CompileSwitch(switchStatement);
                    break;
                case FunctionStatement _:
                    // functions are compiled after the main body
                    break;
                case FunctionReturnStatement returnStatement:
                    CompileFunctionReturn(returnStatement);
                    break;
                case AssertStatement assertStatement:
                    CompileAssert(assertStatement);
                    break;
                case ExpressionStatement expressionStatement:
                    CompileExpressionStatement(expressionStatement);
                    break;
                case DeferStatement deferStatement:
                    CompileDefer(deferStatement);
                    break;
                case RuntoStatement runtoStatement:
                    CompileRunto(runtoStatement);
                    break;
                case MockStatement mockStatement:
                    CompileMock(mockStatement);
                    break;
                case MockExitMockStatement exitMock:
                    CompileExitMock(exitMock);
                    break;
                case MockForbidStatement forbid:
                    CompileForbid(forbid);
                    break;
                case ClearMockStatement clearMock:
                    CompileClearMock(clearMock);
                    break;
                case MacroTokenizeStatement _:
                    throw new CSharpTranspileException("macro tokenize blocks only run at compile time", statement);
                default:
                    throw new CSharpTranspileException("unhandled statement node " + statement, statement);
            }
        }

        // ---- declarations -------------------------------------------------------------------

        void CompileDeclaration(DeclarationStatement declaration, bool includeDefaultInitializer = false)
        {
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);

            if (declaration.ranks == null || declaration.ranks.Length == 0)
            {
                DeclareScalar(declaration);
            }
            else
            {
                var arrayVar = _scope.CreateArray(declaration.variable, declaration.ranks.Length, tc,
                    declaration.scopeType == DeclarationScopeType.Global);
                if (tc == TypeCodes.STRUCT)
                {
                    if (!(declaration.type is StructTypeReferenceNode arrayStructRefNode))
                        throw new CSharpTranspileException("array struct needs correct node type", declaration);
                    var typeName = arrayStructRefNode.variableNode.variableName;
                    if (!_types.TryGetValue(typeName, out var structType))
                        throw new CSharpTranspileException("Compiler: unknown type ref " + typeName, declaration);
                    arrayVar.byteSize = structType.byteSize;
                    arrayVar.structType = structType;
                }

                // rank expressions are evaluated last-to-first, each followed by its two registers
                var sizes = new string[declaration.ranks.Length];
                for (var i = declaration.ranks.Length - 1; i >= 0; i--)
                {
                    sizes[i] = RankSize(declaration.ranks[i]);
                    arrayVar.rankSizeRegisterAddresses[i] = _scope.AllocateRegister();
                    arrayVar.rankIndexScalerRegisterAddresses[i] = _scope.AllocateRegister();
                }

                EmitDim(arrayVar, sizes);
            }

            if (declaration.initializerExpression != null)
            {
                CompileAssignment(new AssignmentStatement
                {
                    startToken = declaration.startToken,
                    endToken = declaration.endToken,
                    expression = declaration.initializerExpression,
                    variable = new VariableRefNode(declaration.startToken, declaration.variable)
                });
            }
            else if (includeDefaultInitializer && declaration.ranks == null)
            {
                switch (tc)
                {
                    case TypeCodes.STRUCT:
                        break;
                    case TypeCodes.STRING:
                        CompileAssignment(new AssignmentStatement
                        {
                            expression = new LiteralStringExpression(declaration.startToken, ""),
                            variable = new VariableRefNode(declaration.startToken, declaration.variable)
                        });
                        break;
                    default:
                        CompileAssignment(new AssignmentStatement
                        {
                            expression = new LiteralIntExpression(declaration.startToken, 0),
                            variable = new VariableRefNode(declaration.startToken, declaration.variable)
                        });
                        break;
                }
            }
        }

        /// <summary>
        /// A rank size, evaluated into a temp (ranks are evaluated last-to-first). ALLOC reads the
        /// total as an INT, so only types that promote to INT with an int on the left work.
        /// </summary>
        string RankSize(IExpressionNode expr)
        {
            var ex = EmitRoot(expr);
            var temp = NewTemp(FT.Int);
            switch (ex.type.tc)
            {
                case TypeCodes.INT:
                case TypeCodes.BYTE:
                case TypeCodes.BOOL:
                case TypeCodes.DINT:
                case TypeCodes.DWORD:
                    Line($"{temp} = {Cast(ex, FT.Int).code};");
                    break;
                default:
                    Line($"{temp} = FadeRt.BadOp<{CsType(ex.type)}, int>({ex.code}, \"array size\", {ex.type.tc});");
                    break;
            }
            return temp;
        }

        void EmitDim(CompiledArrayVariable arrayVar, string[] sizes)
        {
            var access = ArrayAccess(arrayVar);
            Line($"({access} ?? ({access} = new {ArrayCsType(arrayVar)}({sizes.Length}))).Dim({string.Join(", ", sizes)});");
        }

        string ArrayCsType(CompiledArrayVariable a)
        {
            var elem = a.typeCode == TypeCodes.STRUCT ? FT.Struct(a.structType.typeName) : FT.Of(a.typeCode);
            return $"FadeArray<{CsType(elem)}>";
        }

        void CompileRedim(RedimStatement redimStatement)
        {
            if (!_scope.TryGetArray(redimStatement.variable.variableName, out var arrayVar))
            {
                throw new CSharpTranspileException("invalid array to redim", redimStatement);
            }

            var sizes = new string[redimStatement.ranks.Length];
            for (var i = redimStatement.ranks.Length - 1; i >= 0; i--)
            {
                sizes[i] = RankSize(redimStatement.ranks[i]);
            }
            if (sizes.Length != arrayVar.rankSizeRegisterAddresses.Length)
            {
                throw new CSharpTranspileException("redim must keep the array's rank", redimStatement);
            }
            EmitDim(arrayVar, sizes);
        }

        // ---- assignment ---------------------------------------------------------------------

        void CompileAssignment(AssignmentStatement assignment)
        {
            // the right side is always evaluated first, then the destination (and its indexes)
            var rhs = EmitRoot(assignment.expression);
            CompileStore(assignment.variable, rhs, assignment);
        }

        void CompileStore(IVariableNode variable, Ex rhs, IAstNode at)
        {
            switch (variable)
            {
                case ArrayIndexReference arrayRef:
                {
                    if (!_scope.TryGetArray(arrayRef.variableName, out var arrayVar))
                    {
                        throw new CSharpTranspileException("Compiler: cannot access array since it not declared" + arrayRef.variableName, at);
                    }
                    var value = SpillIfNeeded(Cast(rhs, ElementType(arrayVar)), arrayRef.rankExpressions);
                    var element = ArrayElement(arrayVar, arrayRef);
                    Line($"{element.code} = {value.code};");
                    break;
                }
                case VariableRefNode variableRef:
                {
                    if (_scope.TryGetArray(variableRef.variableName, out var targetArray))
                    {
                        // `a = b` between arrays copies b's memory; a's rank registers stay as they were
                        if (!rhs.isArray)
                        {
                            throw new CSharpTranspileException("cannot assign a non-array to an array", at);
                        }
                        var access = ArrayAccess(targetArray);
                        Line($"({access} ?? ({access} = new {ArrayCsType(targetArray)}({targetArray.rankSizeRegisterAddresses.Length}))).AssignFrom({rhs.code});");
                        break;
                    }

                    if (!_scope.TryGetVariable(variableRef.variableName, out var compiledVar))
                    {
                        var tc = VmUtil.GetTypeCode(variableRef.DefaultTypeByName);
                        compiledVar = _scope.Create(variableRef.variableName, tc, false);
                    }

                    var target = VarType(compiledVar);
                    if (target.IsStruct && (!rhs.type.IsStruct || rhs.type.structName != target.structName))
                    {
                        throw new CSharpTranspileException($"cannot assign {TypeName(rhs.type)} to struct {target.structName}", at);
                    }
                    Line($"{VarAccess(compiledVar)} = {Cast(rhs, target).code};");
                    break;
                }
                case StructFieldReference fieldRef:
                {
                    switch (fieldRef.left)
                    {
                        case ArrayIndexReference arrayRef:
                        {
                            if (!_scope.TryGetArray(arrayRef.variableName, out var arrayVar))
                            {
                                throw new CSharpTranspileException("Compiler: cannot access array since it not declared" + arrayRef.variableName, at);
                            }
                            if (arrayVar.structType == null)
                            {
                                throw new CSharpTranspileException("field access on a non-struct array", at);
                            }
                            var path = FieldPath(arrayVar.structType, fieldRef.right, out var fieldType);
                            var value = SpillIfNeeded(Cast(rhs, fieldType), arrayRef.rankExpressions);
                            var element = ArrayElement(arrayVar, arrayRef);
                            Line($"{element.code}{path} = {value.code};");
                            break;
                        }
                        case VariableRefNode leftVar:
                        {
                            if (!_scope.TryGetVariable(leftVar.variableName, out var compiledVar))
                            {
                                compiledVar = FakeDeclare(leftVar);
                            }
                            if (compiledVar.structType == null || !_types.TryGetValue(compiledVar.structType, out var type))
                            {
                                throw new CSharpTranspileException("field access on a non-struct variable " + leftVar.variableName, at);
                            }
                            var path = FieldPath(type, fieldRef.right, out var fieldType);
                            Line($"{VarAccess(compiledVar)}{path} = {Cast(rhs, fieldType).code};");
                            break;
                        }
                        default:
                            throw new CSharpTranspileException("unhandled left side of operation", at);
                    }
                    break;
                }
                default:
                    throw new CSharpTranspileException("Unsupported reference assignment", at);
            }
        }

        /// <summary>
        /// The VM evaluates an assignment's right side before the destination's index expressions.
        /// C# does the opposite for `a[i] = v`, so when the indexes could observe the right side's
        /// side effects, evaluate the right side into a temp first.
        /// </summary>
        Ex SpillIfNeeded(Ex value, List<IExpressionNode> indexes)
        {
            if (value.pure || indexes.All(IsConstant)) return value;
            var temp = NewTemp(value.type);
            Line($"{temp} = {value.code};");
            return new Ex(temp, value.type, pure: true);
        }

        static bool IsConstant(IExpressionNode e) =>
            e is LiteralIntExpression || e is LiteralRealExpression || e is LiteralStringExpression;

        string FieldPath(CompiledType baseType, IVariableNode right, out FT fieldType)
        {
            switch (right)
            {
                case VariableRefNode variableRefNode:
                {
                    if (!baseType.fields.TryGetValue(variableRefNode.variableName, out var member))
                    {
                        throw new CSharpTranspileException("Compiler: unknown member access " + variableRefNode.variableName, right);
                    }
                    fieldType = member.TypeCode == TypeCodes.STRUCT ? FT.Struct(member.Type.typeName) : FT.Of(member.TypeCode);
                    return "." + FieldName(variableRefNode.variableName);
                }
                case StructFieldReference structRef when structRef.left is VariableRefNode leftName:
                {
                    if (!baseType.fields.TryGetValue(leftName.variableName, out var leftMember) || leftMember.Type == null)
                    {
                        throw new CSharpTranspileException("Compiler: unknown member access " + leftName.variableName, right);
                    }
                    return "." + FieldName(leftName.variableName) + FieldPath(leftMember.Type, structRef.right, out fieldType);
                }
                default:
                    throw new CSharpTranspileException("Cannot compute offsets", right);
            }
        }

        // ---- labels, goto, gosub, end -------------------------------------------------------

        void CompileLabel(LabelDeclarationNode labelStatement)
        {
            var csLabel = NewLabel("L_" + Sanitize(labelStatement.label));
            _region.labelDefs[labelStatement.label] = csLabel;
            Label(csLabel);

            // RUNTO_YIELD: a main-body label that some test runs to hands control back to the
            // test when it is the current runto target.
            if ((_region.kind == RegionKind.Main || _region.kind == RegionKind.Routine) && !_region.isDeferBody
                && _runtoTargetLabels.Contains(labelStatement.label))
            {
                Line($"if (__RuntoReached({RuntoId(labelStatement.label)})) yield return CompiledFadeProgram.RuntoHit;");
            }
        }

        readonly Dictionary<string, int> _runtoIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        int RuntoId(string label)
        {
            if (!_runtoIds.TryGetValue(label, out var id))
            {
                _runtoIds[label] = id = _runtoIds.Count + 1;
            }
            return id;
        }

        void CompileGosub(GoSubStatement goSubStatement)
        {
            if (TryCompileRoutineCall(goSubStatement)) return;
            _region.usesGosub = true;
            var index = _region.gosubReturnLabels.Count;
            var ret = NewLabel("GR");
            _region.gosubReturnLabels.Add(ret);
            Line($"__gs.Push({index}); goto @@L:{goSubStatement.label}@@;");
            Label(ret);
        }

        void CompileGosubReturn()
        {
            if (_region.kind == RegionKind.Routine)
            {
                // the routine was entered by its gosub, which is this method's call
                Line(_region.isIterator ? "yield break;" : "return;");
                return;
            }
            _region.usesGosub = true;
            Line("if (__gs.Count > 0) goto __gosub_return;");
            switch (_region.kind)
            {
                case RegionKind.Main:
                    // RETURN on an empty stack is a no-op (gosub code reached without a gosub)
                    break;
                case RegionKind.TestBody:
                    // the launcher's frame is on top: RETURN ends this body
                    Line("yield break;");
                    break;
                default:
                    // In a function the VM would pop the function's own call frame without popping
                    // its scope, leaking the callee's registers into the caller. Refuse at runtime.
                    Line("throw new global::System.InvalidOperationException(\"gosub `return` with no gosub inside a function\");");
                    break;
            }
        }

        void CompileEnd()
        {
            HandleDeferExit();
            Line("throw FadeEndProgramException.Instance;");
        }

        // ---- functions ----------------------------------------------------------------------

        void CompileFunctionReturn(FunctionReturnStatement returnStatement)
        {
            if (_region.kind != RegionKind.Function || _region.isDeferBody)
            {
                throw new CSharpTranspileException("exitfunction outside of a function", returnStatement);
            }

            Ex value = null;
            if (returnStatement.returnExpression != null)
            {
                value = EmitRoot(returnStatement.returnExpression);
                if (!_observedReturnTypes.TryGetValue(_region.fn, out var list))
                {
                    _observedReturnTypes[_region.fn] = list = new List<FT>();
                }
                list.Add(value.type);

                // the value must survive the defer drain
                if (RegionHasDefers() || !value.pure)
                {
                    var temp = NewTemp(value.type);
                    Line($"{temp} = {value.code};");
                    value = new Ex(temp, value.type, pure: true);
                }
            }

            HandleDeferExit();
            EmitReturnLines(value);
        }

        void EmitReturnLines(Ex value)
        {
            var fn = _region.fn;
            var ret = fn.returnType;
            if (fn.iterator)
            {
                if (!ret.IsVoid)
                {
                    Line($"__r_{fn.csName} = {(value != null && value.type.Equals(ret) ? value.code : DefaultValue(ret))};");
                }
                Line("yield break;");
            }
            else if (ret.IsVoid)
            {
                Line("return;");
            }
            else
            {
                Line($"return {(value != null && value.type.Equals(ret) ? value.code : DefaultValue(ret))};");
            }
        }

        // ---- defer --------------------------------------------------------------------------
        // A defer pushes a site onto its scope's defer stack when it runs; leaving the scope runs
        // them newest first. Bodies are emitted as callable functions so the assert unwind can run
        // them too. Main and test bodies share the global scope's stack (an instance field); every
        // function call has its own.

        void CompileDefer(DeferStatement deferStatement)
        {
            var lines = new List<string>();
            var savedLines = _region.lines;
            var savedIterator = _region.isIterator;
            var savedDefer = _region.isDeferBody;
            var savedIndent = _region.indent;
            var savedLoops = _region.loops.ToArray();
            var savedTemps = _region.temps;
            var deferTemps = _region.IsGlobalScope ? new List<StorageDecl>() : _region.temps;
            _region.temps = deferTemps;
            _region.lines = lines;
            _region.isIterator = false;
            _region.isDeferBody = true;
            _region.indent = _region.IsGlobalScope ? 3 : 4;
            _region.loops.Clear();
            try
            {
                foreach (var statement in deferStatement.statements)
                {
                    CompileStatement(statement);
                }
            }
            finally
            {
                _region.lines = savedLines;
                _region.temps = savedTemps;
                _region.isIterator = savedIterator;
                _region.isDeferBody = savedDefer;
                _region.indent = savedIndent;
                _region.loops.Clear();
                for (var i = savedLoops.Length - 1; i >= 0; i--) _region.loops.Push(savedLoops[i]);
            }

            if (_region.IsGlobalScope)
            {
                var id = _globalDeferCount++;
                ResolveLabels(_region, lines);
                _members.AppendLine();
                _members.AppendLine($"        void __Defer_{id}()");
                _members.AppendLine("        { unchecked {");
                foreach (var t in deferTemps) _members.AppendLine($"            {t.csType} {t.name}{t.initializer};");
                foreach (var l in lines) _members.AppendLine(l);
                _members.AppendLine("        } }");
                Line($"__gdefers.Push({id});");
            }
            else
            {
                var id = _region.localDefers.Count;
                _region.localDefers.Add(lines);
                Line($"__defers.Push({id});");
            }
        }

        /// <summary>Runs every pending defer of the current scope, newest first.</summary>
        void HandleDeferExit()
        {
            if (!RegionHasDefers()) return;
            HiddenLines();
            Line(_region.IsGlobalScope ? "__DrainGlobalDefers();" : "__drain();");
        }

        bool RegionHasDefers()
        {
            IEnumerable<IStatementNode> statements;
            switch (_region.kind)
            {
                case RegionKind.Function:
                    statements = _region.fn.node.statements;
                    break;
                case RegionKind.MockBody:
                    statements = _region.mock.statement.body;
                    break;
                default:
                    return ProgramHasGlobalDefers();
            }
            return statements.Any(ContainsDefer);
        }

        bool? _programHasGlobalDefers;

        bool ProgramHasGlobalDefers()
        {
            if (_programHasGlobalDefers == null)
            {
                var found = _program.statements.Any(ContainsDefer);
                if (_includeTests)
                {
                    found |= _program.tests.Any(t => t.testProgram.statements.Any(ContainsDefer));
                }
                _programHasGlobalDefers = found;
            }
            return _programHasGlobalDefers.Value;
        }

        static bool ContainsDefer(IStatementNode statement)
        {
            var found = false;
            statement.Visit(n =>
            {
                if (n is DeferStatement) found = true;
            });
            return found;
        }

        // ---- tests: runto, mocks ------------------------------------------------------------

        void CompileRunto(RuntoStatement runto)
        {
            if (runto.maxCyclesExpression != null)
            {
                throw new CSharpTranspileException(
                    "`runto ... max cycles` counts VM instructions, which compiled code does not have", runto);
            }
            if (!_region.isIterator)
            {
                throw new CSharpTranspileException("runto cannot run here", runto);
            }
            // the target is always a main-body label (Compiler.CompileJumpReplacements)
            Line($"__RuntoPush({RuntoId(runto.targetLabel)}); yield return CompiledFadeProgram.RuntoRequest;");
        }

        void CompileClearMock(ClearMockStatement clear)
        {
            if (clear.commandName == null)
            {
                Line("__ClearMocks();");
                return;
            }
            foreach (var id in ResolveMockCommandIds(clear.commandName))
            {
                Line($"__ClearMock({id});");
            }
        }

        // ---- control flow -------------------------------------------------------------------
        // A region with no Fade labels, goto or gosub is emitted as structured C# (if/else,
        // while, break, continue); one with them stays flat, mirroring the bytecode's jumps, so a
        // goto can land anywhere. Both shapes evaluate exactly what the bytecode evaluates.

        void Open()
        {
            Line("{");
            _region.indent++;
        }

        void Close(string after = "")
        {
            _region.indent--;
            Line("}" + after);
        }

        void CompileBlock(IEnumerable<IStatementNode> statements)
        {
            Open();
            foreach (var s in statements) CompileStatement(s);
            Close();
        }

        void CompileIf(IfStatement ifStatement)
        {
            var cond = EmitRoot(ifStatement.condition);
            if (_region.structured)
            {
                Line($"if ({Bare(Truth(cond))})");
                CompileBlock(ifStatement.positiveStatements);
                if (ifStatement.negativeStatements != null && ifStatement.negativeStatements.Count > 0)
                {
                    Line("else");
                    CompileBlock(ifStatement.negativeStatements);
                }
                return;
            }

            var elseLabel = NewLabel("ELSE");
            var endLabel = NewLabel("ENDIF");
            Line($"if (!{Truth(cond)}) goto {elseLabel};");
            foreach (var s in ifStatement.positiveStatements) CompileStatement(s);
            Line($"goto {endLabel};");
            Label(elseLabel);
            if (ifStatement.negativeStatements != null)
            {
                foreach (var s in ifStatement.negativeStatements) CompileStatement(s);
            }
            Label(endLabel);
        }

        /// <summary>
        /// `while (true) {` with the condition evaluated as the loop's first statement, or, when the
        /// condition needs no statements of its own, `while (cond) {`.
        /// </summary>
        void OpenConditionLoop(IExpressionNode condition)
        {
            var mark = _region.lines.Count;
            Line("while (true)");
            Open();
            var cond = EmitRoot(condition);
            if (_region.lines.Count == mark + 2)
            {
                // nothing was emitted for the condition: fold it into the loop header
                _region.lines[mark] = new string(' ', (_region.indent - 1) * 4) + $"while ({Bare(Truth(cond))})";
            }
            else
            {
                Line($"if (!{Truth(cond)}) break;");
            }
        }

        void CompileWhile(WhileStatement whileStatement)
        {
            if (_region.structured)
            {
                OpenConditionLoop(whileStatement.condition);
                _region.loops.Push(new LoopLabels { exit = "break;", skip = "continue;" });
                foreach (var s in whileStatement.statements) CompileStatement(s);
                _region.loops.Pop();
                Close();
                return;
            }

            var top = NewLabel("WHILE");
            var end = NewLabel("WEND");
            Label(top);
            var cond = EmitRoot(whileStatement.condition);
            Line($"if (!{Truth(cond)}) goto {end};");
            _region.loops.Push(new LoopLabels { exit = $"goto {end};", skip = $"goto {top};" });
            foreach (var s in whileStatement.statements) CompileStatement(s);
            _region.loops.Pop();
            Line($"goto {top};");
            Label(end);
        }

        void CompileDoLoop(DoLoopStatement doLoopStatement)
        {
            if (_region.structured)
            {
                Line("while (true)");
                Open();
                _region.loops.Push(new LoopLabels { exit = "break;", skip = "continue;" });
                foreach (var s in doLoopStatement.statements) CompileStatement(s);
                _region.loops.Pop();
                Close();
                return;
            }

            var top = NewLabel("DO");
            var end = NewLabel("LOOP_END");
            Label(top);
            _region.loops.Push(new LoopLabels { exit = $"goto {end};", skip = $"goto {top};" });
            foreach (var s in doLoopStatement.statements) CompileStatement(s);
            _region.loops.Pop();
            Line($"goto {top};");
            Label(end);
        }

        void CompileRepeat(RepeatUntilStatement repeatStatement)
        {
            if (_region.structured)
            {
                // `skip` jumps to the condition, which may need statements of its own: a label at the
                // end of the body is the honest shape
                var until = NewLabel("UNTIL");
                Line("while (true)");
                Open();
                _region.loops.Push(new LoopLabels { exit = "break;", skip = $"goto {until};" });
                foreach (var s in repeatStatement.statements) CompileStatement(s);
                _region.loops.Pop();
                Label(until);
                var untilCond = EmitRoot(repeatStatement.condition);
                Line($"if ({Bare(Truth(untilCond))}) break;");
                Close();
                return;
            }

            var top = NewLabel("REPEAT");
            var skip = NewLabel("UNTIL");
            var end = NewLabel("REPEAT_END");
            Label(top);
            _region.loops.Push(new LoopLabels { exit = $"goto {end};", skip = $"goto {skip};" });
            foreach (var s in repeatStatement.statements) CompileStatement(s);
            _region.loops.Pop();
            Label(skip);
            var cond = EmitRoot(repeatStatement.condition);
            // CAST INT, NOT (x > 0 ? 0 : 1), JUMP_GT_ZERO top
            Line($"if (!{Truth(cond)}) goto {top};");
            Label(end);
        }

        void CompileFor(ForStatement forStatement)
        {
            // Mirrors Compiler.Compile(ForStatement): the start expression is evaluated again on
            // every iteration (as the "min"), the bounds are sorted at runtime (MIN_MAX_PUSH), and
            // the loop runs while min <= x <= max. The step is `x = x + step`.
            CompileAssignment(new AssignmentStatement
            {
                startToken = forStatement.startToken,
                expression = forStatement.startValueExpression,
                variable = forStatement.variableNode
            });

            var structured = _region.structured;
            var step = NewLabel("STEP");
            string top = null, end = null, leave;
            if (structured)
            {
                Line("while (true)");
                Open();
                leave = "break;";
            }
            else
            {
                top = NewLabel("FOR");
                end = NewLabel("NEXT_END");
                Label(top);
                leave = $"goto {end};";
            }

            var start = EmitRoot(forStatement.startValueExpression);
            start = SpillIfImpure(start);
            var stop = EmitRoot(forStatement.endValueExpression);
            stop = SpillIfImpure(stop);
            var p = Promote(start.type, stop.type);
            if (!(p.tc == TypeCodes.BYTE || p.tc == TypeCodes.WORD || p.tc == TypeCodes.INT || p.tc == TypeCodes.REAL))
            {
                Line($"throw new global::System.Exception(\"Unsupported add operation\");");
            }
            else
            {
                var lo = NewTemp(p);
                var hi = NewTemp(p);
                var a = Cast(start, p).code;
                var b = Cast(stop, p).code;
                // GetMinMax compares WORD as signed shorts
                var cmp = p.tc == TypeCodes.WORD ? $"(short){b} > (short){a}" : $"{b} > {a}";
                Line($"if ({cmp}) {{ {lo} = {a}; {hi} = {b}; }} else {{ {lo} = {b}; {hi} = {a}; }}");

                var x = EmitRoot(forStatement.variableNode);
                var loEx = new Ex(lo, p, pure: true);
                var hiEx = new Ex(hi, p, pure: true);
                var c1 = Compare(OperationType.LessThanOrEqualTo, loEx, x);
                Line($"if (!{Truth(c1)}) {leave}");
                x = EmitRoot(forStatement.variableNode);
                var c2 = Compare(OperationType.GreaterThanOrEqualTo, hiEx, x);
                Line($"if (!{Truth(c2)}) {leave}");
            }

            _region.loops.Push(new LoopLabels { exit = leave, skip = $"goto {step};" });
            foreach (var s in forStatement.statements) CompileStatement(s);
            _region.loops.Pop();

            Label(step);
            CompileAssignment(new AssignmentStatement
            {
                startToken = forStatement.startToken,
                expression = new BinaryOperandExpression
                {
                    startToken = forStatement.startToken,
                    operationType = OperationType.Add,
                    lhs = forStatement.variableNode,
                    rhs = forStatement.stepValueExpression
                },
                variable = forStatement.variableNode
            });
            if (structured)
            {
                Close();
            }
            else
            {
                Line($"goto {top};");
                Label(end);
            }
        }

        Ex SpillIfImpure(Ex ex)
        {
            if (ex.pure) return ex;
            var temp = NewTemp(ex.type);
            Line($"{temp} = {ex.code};");
            return new Ex(temp, ex.type, pure: true);
        }

        void CompileSwitch(SwitchStatement switchStatement)
        {
            var key = EmitRoot(switchStatement.expression);

            // JUMP_TABLE compares raw register bits (ReadSpanAsUInt) and finds the LAST pushed
            // matching case first. Strings compare as pointers: only the same interned literal matches.
            string keyTemp;
            if (key.type.tc == TypeCodes.STRING)
            {
                keyTemp = NewTemp(key.type);
                Line($"{keyTemp} = {key.code};");
            }
            else
            {
                keyTemp = NewTemp(FT.Of(TypeCodes.DINT));
                if (!KeyExpression(key, out var keyCode))
                {
                    throw new CSharpTranspileException("unsupported select type " + TypeName(key.type), switchStatement);
                }
                _region.temps[_region.temps.Count - 1].csType = "ulong";
                Line($"{keyTemp} = {keyCode};");
            }

            // one test per case: any of its values matches
            var tests = new List<string>();
            var keysSeen = new HashSet<string>();
            var duplicates = false;
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                var parts = new List<string>();
                foreach (var literal in switchStatement.cases[i].values)
                {
                    var lit = EmitExpression(literal);
                    string test;
                    if (key.type.tc == TypeCodes.STRING || lit.type.tc == TypeCodes.STRING)
                    {
                        test = key.type.tc == TypeCodes.STRING && lit.type.tc == TypeCodes.STRING
                            ? $"(object){keyTemp} == (object){lit.code}"
                            : "false";
                    }
                    else
                    {
                        if (!KeyExpression(lit, out var litKey))
                            throw new CSharpTranspileException("unsupported case literal", literal);
                        test = $"{keyTemp} == {litKey}";
                    }
                    if (!keysSeen.Add(lit.type.tc + ":" + lit.code)) duplicates = true;
                    parts.Add(test);
                }
                tests.Add(parts.Count == 0 ? "false" : string.Join(" || ", parts));
            }

            if (_region.structured)
            {
                // a later case wins a value two cases share, so test those in reverse
                var order = Enumerable.Range(0, switchStatement.cases.Count).ToList();
                if (duplicates) order.Reverse();
                var first = true;
                foreach (var i in order)
                {
                    Line($"{(first ? "" : "else ")}if ({tests[i]})");
                    CompileBlock(switchStatement.cases[i].statements);
                    first = false;
                }
                if (switchStatement.defaultCase != null && switchStatement.defaultCase.statements.Count > 0)
                {
                    if (!first) Line("else");
                    CompileBlock(switchStatement.defaultCase.statements);
                }
                return;
            }

            var end = NewLabel("SELECT_END");
            var defaultLabel = NewLabel("DEFAULT");
            var caseLabels = switchStatement.cases.Select(_ => NewLabel("CASE")).ToArray();
            for (var i = switchStatement.cases.Count - 1; i >= 0; i--)
            {
                Line($"if ({tests[i]}) goto {caseLabels[i]};");
            }
            Line($"goto {defaultLabel};");

            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                Label(caseLabels[i]);
                foreach (var s in switchStatement.cases[i].statements) CompileStatement(s);
                Line($"goto {end};");
            }

            Label(defaultLabel);
            if (switchStatement.defaultCase != null)
            {
                foreach (var s in switchStatement.defaultCase.statements) CompileStatement(s);
            }
            Label(end);
        }

        bool KeyExpression(Ex ex, out string code)
        {
            switch (ex.type.tc)
            {
                case TypeCodes.INT:
                case TypeCodes.DWORD:
                case TypeCodes.REAL:
                case TypeCodes.DINT:
                case TypeCodes.DFLOAT:
                case TypeCodes.WORD:
                case TypeCodes.BYTE:
                case TypeCodes.BOOL:
                    code = $"FadeRt.Key({ex.code})";
                    return true;
                default:
                    code = null;
                    return false;
            }
        }

        // ---- assert, expression statements --------------------------------------------------

        void CompileAssert(AssertStatement assertStatement)
        {
            var cond = EmitRoot(assertStatement.condition);
            // JUMP_GT_ZERO reads the condition as an INT; anything else is a VM error
            var check = cond.type.tc == TypeCodes.INT
                ? Truth(cond)
                : $"FadeRt.BadOp<{CsType(cond.type)}, bool>({cond.code}, \"assert\", {cond.type.tc})";
            var pass = NewLabel("ASSERT_OK");
            Line($"if ({check}) goto {pass};");
            var reason = assertStatement.reason != null ? EmitRoot(assertStatement.reason) : new Ex("\"\"", FT.Of(TypeCodes.STRING), true);
            Line($"__AssertFailed({reason.code}, {StringLiteral(assertStatement.sourceText ?? "")}, {Site});");
            Label(pass);
        }

        void CompileExpressionStatement(ExpressionStatement statement)
        {
            var ex = EmitRoot(statement.expression, asStatement: true);
            if (ex == null || ex.isStatementDone) return;
            if (ex.isCall)
            {
                Line(ex.code + ";");
            }
            else
            {
                Line($"_ = {ex.code};");
            }
        }
    }
}
