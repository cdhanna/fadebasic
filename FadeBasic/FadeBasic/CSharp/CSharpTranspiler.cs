using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using FadeBasic.Ast;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    public class CSharpTranspileOptions
    {
        public string Namespace = "FadeGenerated";
        public string ClassName = "FadeProgram";

        /// <summary>
        /// When set, every Fade statement is preceded by a <c>#line</c> directive pointing at its
        /// original file and line, so the .NET debugger steps through .fbasic source and exception
        /// stack traces name Fade lines.
        /// </summary>
        public SourceMap SourceMap;

        /// <summary>
        /// Used for <c>#line</c> directives when there is no <see cref="SourceMap"/> (single-file programs).
        /// </summary>
        public string SourceFileName;

        /// <summary>
        /// Decides whether a command may suspend the program (by calling <see cref="VirtualMachine.Suspend"/>).
        /// Functions that can reach such a command become C# iterators. The default treats every
        /// command that takes a <c>[FromVm] VirtualMachine</c> as suspending, which is always safe.
        /// </summary>
        public Func<CommandInfo, bool> IsSuspendingCommand;

        /// <summary>
        /// Compile the program's `test` blocks. Defaults to true when the program has any; set false
        /// to leave them out (the generated program then has no mock hooks on command calls).
        /// </summary>
        public bool? IncludeTests;
    }

    public class CSharpTranspileResult
    {
        public string Source;
        public string ClassFullName;
        public List<string> Errors = new List<string>();
        public bool Success => Errors.Count == 0;

        /// <summary>Gosub routines that stayed in the main body, and why (see CSharpTranspiler.Routines).</summary>
        public List<string> InlineRoutines = new List<string>();
    }

    /// <summary>
    /// Thrown when a program uses something the C# backend can't reproduce faithfully. The VM
    /// is still the reference implementation, so the backend refuses rather than guessing.
    /// </summary>
    public class CSharpTranspileException : Exception
    {
        public CSharpTranspileException(string message, IAstNode node = null)
            : base(node?.StartToken != null ? $"{message} (line {node.StartToken.lineNumber + 1})" : message)
        {
        }
    }

    /// <summary>
    /// The C# backend for Fade. It walks the program in exactly the order the bytecode
    /// <see cref="Compiler"/> does, allocating registers from the same <see cref="CompileScope"/>,
    /// so every variable gets the same register number it has in the VM. Instead of bytecode it
    /// emits flat C# (labels and gotos, like the bytecode's jumps), with statically typed values
    /// that follow the VM's promotion and cast rules. See CSharpBackend.md for the design.
    /// </summary>
    public partial class CSharpTranspiler
    {
        readonly ProgramNode _program;
        readonly CommandCollection _commands;
        readonly CSharpTranspileOptions _options;
        readonly bool _includeTests;

        // ---- compile-order state, rebuilt every pass (mirrors Compiler) ----
        CompileScope _scope;
        Dictionary<string, CompiledType> _types;
        Dictionary<int, CompiledType> _typeTable;
        Region _region;
        int _siteCounter;
        int _tempCounter;
        int _globalDeferCount;
        int _mockCounter;
        readonly List<Region> _extraRegions = new List<Region>();
        readonly StringBuilder _members = new StringBuilder();
        readonly HashSet<int> _dispatchedCommands = new HashSet<int>();

        // ---- program-wide declarations ----
        readonly Dictionary<ulong, StorageDecl> _globals = new Dictionary<ulong, StorageDecl>();
        readonly List<FnInfo> _allFunctions = new List<FnInfo>();
        readonly Dictionary<FunctionStatement, FnInfo> _fnByNode = new Dictionary<FunctionStatement, FnInfo>();
        readonly Dictionary<string, FnInfo> _functions = new Dictionary<string, FnInfo>(StringComparer.Ordinal);
        readonly Dictionary<string, CommandBinding> _commandBindings = new Dictionary<string, CommandBinding>();
        readonly Dictionary<Type, string> _commandTypeAliases = new Dictionary<Type, string>();

        /// <summary>How many functions were left out of the output because nothing calls them.</summary>
        public int UncalledFunctions { get; private set; }
        readonly Dictionary<string, int> _commandIds = new Dictionary<string, int>();
        readonly HashSet<string> _runtoTargetLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<FnInfo, FT> _returnTypeGuesses = new Dictionary<FnInfo, FT>();
        Dictionary<FnInfo, List<FT>> _observedReturnTypes;

        CSharpTranspiler(ProgramNode program, CommandCollection commands, CSharpTranspileOptions options)
        {
            _program = program;
            _commands = commands;
            _options = options ?? new CSharpTranspileOptions();
            if (_options.IsSuspendingCommand == null)
            {
                _options.IsSuspendingCommand = c => c.args != null && c.args.Any(a => a.isVmArg);
            }
            _includeTests = _options.IncludeTests ?? program.tests.Count > 0;

            // command ids are positions in the method table (Compiler._commandToPtr: last one wins)
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                _commandIds[commands.Commands[i].UniqueName] = i;
            }
        }

        public static CSharpTranspileResult Transpile(ProgramNode program, CommandCollection commands,
            CSharpTranspileOptions options = null)
        {
            var result = new CSharpTranspileResult();
            var t = new CSharpTranspiler(program, commands, options);
            result.ClassFullName = $"{t._options.Namespace}.{t._options.ClassName}";
            try
            {
                result.Source = t.Run();
                result.InlineRoutines = t.InlineRoutineReasons;
            }
            catch (CSharpTranspileException ex)
            {
                result.Errors.Add(ex.Message);
            }
            catch (Exception ex)
            {
                // The bytecode compiler throws on the same inputs (unknown labels, undeclared arrays...).
                result.Errors.Add($"{ex.GetType().Name}: {ex.Message}");
            }

            return result;
        }

        string Run()
        {
            DiscoverFunctions();
            CollectRuntoTargets();
            ComputeSuspendability();
            AnalyzeRoutines();

            // first guess: the parser's inferred return types
            foreach (var fn in _allFunctions)
            {
                var parsed = fn.node.ParsedType;
                if (fn.node.hasNoReturnExpression || parsed.unset || parsed.type == VariableType.Void || parsed.type == VariableType.Any)
                {
                    _returnTypeGuesses[fn] = FT.Void;
                }
                else if (parsed.type == VariableType.Struct)
                {
                    _returnTypeGuesses[fn] = FT.Struct(parsed.structName);
                }
                else
                {
                    _returnTypeGuesses[fn] = FT.Of(VmUtil.GetTypeCode(parsed.type));
                }
            }

            // Function return types are not declared in a way the VM honors: a call evaluates to
            // whatever type the returned expression had. Callers need that type to pick the right
            // arithmetic, so iterate: emit everything, record the types each function actually
            // returns, and repeat until the guesses are stable.
            for (var pass = 0; pass < 8; pass++)
            {
                _observedReturnTypes = new Dictionary<FnInfo, List<FT>>();
                var source = EmitProgram();

                var next = new Dictionary<FnInfo, FT>();
                foreach (var fn in _allFunctions)
                {
                    next[fn] = ResolveReturnType(fn);
                }

                var stable = next.All(kvp => _returnTypeGuesses.TryGetValue(kvp.Key, out var prev) && prev.Equals(kvp.Value));
                _returnTypeGuesses = next;
                if (stable) return source;
            }

            throw new CSharpTranspileException("function return types did not settle");
        }

        FT ResolveReturnType(FnInfo fn)
        {
            if (!_observedReturnTypes.TryGetValue(fn, out var types) || types.Count == 0)
            {
                return FT.Void;
            }

            var first = types[0];
            foreach (var t in types)
            {
                if (!t.Equals(first))
                {
                    throw new CSharpTranspileException(
                        $"function {fn.name} returns values of different types ({TypeName(first)} and {TypeName(t)}); " +
                        "the VM would hand callers a different type depending on the path taken", fn.node);
                }
            }

            return first;
        }

        // =====================================================================================
        //  Function discovery and suspension analysis
        // =====================================================================================

        IEnumerable<FunctionStatement> FunctionsInCompileOrder()
        {
            foreach (var fn in _program.functions) yield return fn;
            if (!_includeTests) yield break;
            foreach (var test in Compiler.TestsParentsFirst(_program.tests))
            {
                foreach (var fn in test.testProgram.functions) yield return fn;
            }
        }

        void DiscoverFunctions()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fn in FunctionsInCompileOrder())
            {
                var csName = "F_" + Sanitize(fn.name);
                var unique = csName;
                for (var n = 2; !used.Add(unique); n++) unique = csName + "_" + n;
                var info = new FnInfo
                {
                    name = fn.name,
                    node = fn,
                    csName = unique
                };
                _allFunctions.Add(info);
                _fnByNode[fn] = info;
                // a call binds to the last function compiled under that name (Compiler._functionTable)
                _functions[fn.name] = info;
            }
        }

        void CollectRuntoTargets()
        {
            if (!_includeTests) return;
            foreach (var test in _program.tests)
            {
                test.Visit(node =>
                {
                    if (node is RuntoStatement runto)
                    {
                        _runtoTargetLabels.Add(runto.targetLabel);
                    }
                });
            }
        }

        void ComputeSuspendability()
        {
            // direct: the body calls a suspending command, or runs `runto`
            var callees = new Dictionary<FnInfo, HashSet<string>>();
            foreach (var fn in _allFunctions)
            {
                var set = new HashSet<string>();
                callees[fn] = set;
                foreach (var statement in fn.node.statements)
                {
                    statement.Visit(node =>
                    {
                        switch (node)
                        {
                            case CommandStatement cs when IsSuspending(cs.command):
                                fn.iterator = true;
                                break;
                            case CommandExpression ce when IsSuspending(ce.command):
                                fn.iterator = true;
                                break;
                            case RuntoStatement _:
                                fn.iterator = true;
                                break;
                            case ArrayIndexReference air when _functions.ContainsKey(air.variableName):
                                set.Add(air.variableName);
                                break;
                        }
                    });
                }
            }

            // transitive: calls a suspending function
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var fn in _allFunctions)
                {
                    if (fn.iterator) continue;
                    foreach (var callee in callees[fn])
                    {
                        if (_functions.TryGetValue(callee, out var c) && c.iterator)
                        {
                            fn.iterator = true;
                            changed = true;
                            break;
                        }
                    }
                }
            }
        }

        bool IsSuspending(CommandInfo command) => _options.IsSuspendingCommand(command);

        /// <summary>
        /// Whether evaluating this expression can suspend the program, in which case (inside an
        /// iterator region) it is lowered to one temp per sub-expression so a `yield` can sit
        /// between any two evaluation steps.
        /// </summary>
        bool CanSuspend(IExpressionNode expr)
        {
            if (expr == null || _region == null) return false;
            var found = false;
            expr.Visit(node =>
            {
                switch (node)
                {
                    case CommandExpression ce when IsSuspending(ce.command):
                        found = true;
                        break;
                    case ArrayIndexReference air when !_scope.TryGetArray(air.variableName, out _)
                                                      && _functions.TryGetValue(air.variableName, out var fn)
                                                      && fn.iterator:
                        found = true;
                        break;
                }
            });
            if (found && !_region.isIterator)
            {
                throw new CSharpTranspileException(_region.isDeferBody
                    ? "a defer body cannot suspend the program"
                    : "this code cannot suspend the program here (mock bodies run synchronously)", expr);
            }
            return found;
        }

        // =====================================================================================
        //  Program emission
        // =====================================================================================

        string EmitProgram()
        {
            _scope = new CompileScope();
            _types = new Dictionary<string, CompiledType>();
            _typeTable = new Dictionary<int, CompiledType>();
            _globals.Clear();
            _extraRegions.Clear();
            _members.Clear();
            _dispatchedCommands.Clear();
            _mocksByCommand.Clear();
            _runtoIds.Clear();
            _siteCounter = 0;
            _tempCounter = 0;
            _globalDeferCount = 0;
            _mockCounter = 0;
            _patternCounter = 0;

            var structs = new StringBuilder();
            foreach (var typeDef in _program.typeDefinitions)
            {
                CompileType(typeDef, structs);
            }

            // main body
            var main = new Region { name = "__Main", kind = RegionKind.Main, isIterator = true };
            main.structured = CanStructure(main.kind, _program.statements.Where((st, i) =>
                !(st is FunctionStatement) && !_routines.Values.Any(r => i >= r.start && i <= r.end)));
            _region = main;
            _routineLabelNames.Clear();
            for (var i = 0; i < _program.statements.Count; i++)
            {
                if (_routineAt.TryGetValue(i, out var routine))
                {
                    i = CompileRoutine(routine) - 1;
                    continue;
                }
                CompileStatement(_program.statements[i]);
            }
            CompileEnd();
            FinishRegion(main);
            foreach (var target in _runtoTargetLabels)
            {
                // runto always targets a main-body label (Compiler.CompileJumpReplacements)
                if (!main.labelDefs.ContainsKey(target) && !_routineLabelNames.Contains(target))
                {
                    throw new CSharpTranspileException("Compiler: unknown runto target label " + target);
                }
            }

            var regions = new List<Region>();
            foreach (var fn in _program.functions)
            {
                regions.Add(CompileFunction(fn));
            }

            // tests: each body, then its test-scoped functions; then one launcher per test
            var testBodies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var testIndex = 0;
            if (_includeTests)
            {
                foreach (var test in Compiler.TestsParentsFirst(_program.tests))
                {
                    var body = new Region
                    {
                        name = $"__TestBody_{testIndex++}_{Sanitize(test.name ?? "anon")}",
                        kind = RegionKind.TestBody,
                        isIterator = true
                    };
                    body.structured = CanStructure(body.kind, test.testProgram.statements);
                    _region = body;
                    _scope.EnterTestRegion(test.name, Compiler.TestAncestorNames(test, _program.tests));
                    foreach (var statement in test.testProgram.statements)
                    {
                        CompileStatement(statement);
                    }
                    _scope.ExitRegion();
                    FinishRegion(body);
                    regions.Add(body);
                    if (test.name != null) testBodies[test.name] = body.name;

                    foreach (var fn in test.testProgram.functions)
                    {
                        regions.Add(CompileFunction(fn));
                    }
                }
            }
            _region = null;

            // ---- assemble the class ----
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("//   Generated by the Fade Basic C# backend (FadeBasic.CSharp.CSharpTranspiler).");
            sb.AppendLine("//   Do not edit; edit the .fbasic sources instead.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine("#pragma warning disable");
            sb.AppendLine($"namespace {_options.Namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    using global::FadeBasic.Compiled;");
            sb.AppendLine("    using IEnumerator = global::System.Collections.IEnumerator;");
            foreach (var kvp in _commandTypeAliases.OrderBy(k => k.Value, StringComparer.Ordinal))
            {
                sb.AppendLine($"    using {kvp.Value} = global::{kvp.Key.FullName.Replace('+', '.')};");
            }
            sb.AppendLine($"    public sealed partial class {_options.ClassName} : CompiledFadeProgram");
            sb.AppendLine("    {");
            sb.Append(structs);

            sb.AppendLine("        // ---- global scope registers ----");
            foreach (var kvp in _globals.OrderBy(k => k.Key))
            {
                sb.AppendLine($"        public {kvp.Value.csType} {kvp.Value.name}{kvp.Value.initializer};");
            }
            sb.AppendLine();

            // A function nothing calls is still compiled (it allocates registers, and the VM
            // checks it), but not written out.
            var allRegions = new List<Region> { main };
            allRegions.AddRange(regions);
            allRegions.AddRange(_extraRegions);
            var called = new HashSet<FnInfo>();
            var byFn = allRegions.Where(r => r.fn != null).GroupBy(r => r.fn).ToDictionary(g => g.Key, g => g.ToList());
            var work = new Stack<Region>(allRegions.Where(r => r.fn == null));
            while (work.Count > 0)
            {
                foreach (var fn in work.Pop().calls)
                {
                    if (!called.Add(fn) || !byFn.TryGetValue(fn, out var fnRegions)) continue;
                    foreach (var r in fnRegions) work.Push(r);
                }
            }
            UncalledFunctions = allRegions.Count(r => r.fn != null && !called.Contains(r.fn));

            foreach (var fn in _allFunctions.Where(f => f.iterator && !f.returnType.IsVoid && called.Contains(f)))
            {
                sb.AppendLine($"        {CsType(fn.returnType)} __r_{fn.csName};");
            }

            WriteRegion(sb, main, "protected override IEnumerator __Main()");
            foreach (var r in regions.Concat(_extraRegions))
            {
                if (r.fn != null && !called.Contains(r.fn)) continue;
                WriteRegion(sb, r, r.signature ?? $"IEnumerator {r.name}()");
            }

            if (_globalDeferCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine("        protected override void __DrainGlobalDefers()");
                sb.AppendLine("        {");
                sb.AppendLine("            while (__gdefers.Count > 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                switch (__gdefers.Pop())");
                sb.AppendLine("                {");
                for (var i = 0; i < _globalDeferCount; i++)
                {
                    sb.AppendLine($"                    case {i}: __Defer_{i}(); break;");
                }
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine("        }");
            }

            if (_includeTests)
            {
                WriteTestLaunchers(sb, testBodies);
                WriteDispatchers(sb);
            }

            sb.Append(_members);
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return Compact(sb.ToString());
        }

        /// <summary>
        /// Two size savings over the whole source. `#line 12 "fish_routines.fbasic"` becomes
        /// `#line 12` when the file is the one the last directive named: Roslyn keeps the mapped
        /// file for a directive without one (also across `#line hidden`), until `#line default`.
        /// And indentation is tabs, not four spaces.
        /// </summary>
        static string Compact(string source)
        {
            var sb = new StringBuilder(source.Length);
            string currentFile = null;
            var at = 0;
            while (at < source.Length)
            {
                var next = source.IndexOf('\n', at);
                var end = next < 0 ? source.Length : next + 1;
                if (string.CompareOrdinal(source, at, "#line ", 0, 6) == 0)
                {
                    var line = source.Substring(at, end - at).TrimEnd('\r', '\n');
                    var quote = line.IndexOf('"');
                    if (line.StartsWith("#line default", StringComparison.Ordinal))
                    {
                        currentFile = null;
                    }
                    else if (quote > 0)
                    {
                        var file = line.Substring(quote);
                        if (file == currentFile)
                        {
                            sb.Append(line, 0, quote - 1).Append(source, at + line.Length, end - at - line.Length);
                            at = end;
                            continue;
                        }
                        currentFile = file;
                    }
                }
                var spaces = 0;
                while (at + spaces < end && source[at + spaces] == ' ') spaces++;
                sb.Append('\t', spaces / 4);
                sb.Append(source, at + spaces - spaces % 4, end - at - spaces + spaces % 4);
                at = end;
            }
            return sb.ToString();
        }

        void WriteTestLaunchers(StringBuilder sb, Dictionary<string, string> testBodies)
        {
            // A test runs its `from` chain root-first, every body sharing this instance, then the
            // global scope's defers drain (Compiler.CompileTestLauncher).
            sb.AppendLine();
            sb.AppendLine("        protected override IEnumerator __CreateTest(string name)");
            sb.AppendLine("        {");
            sb.AppendLine("            switch ((name ?? \"\").ToLowerInvariant())");
            sb.AppendLine("            {");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var test in _program.tests)
            {
                if (test.name == null || !seen.Add(test.name)) continue;
                sb.AppendLine($"                case {StringLiteral(test.name.ToLowerInvariant())}: return __TestLaunch_{Sanitize(test.name)}();");
            }
            sb.AppendLine("            }");
            sb.AppendLine("            return null;");
            sb.AppendLine("        }");

            seen.Clear();
            foreach (var test in _program.tests)
            {
                if (test.name == null || !seen.Add(test.name)) continue;
                sb.AppendLine();
                sb.AppendLine($"        IEnumerator __TestLaunch_{Sanitize(test.name)}()");
                sb.AppendLine("        {");
                foreach (var member in ResolveTestFromChain(test))
                {
                    if (member.name != null && testBodies.TryGetValue(member.name, out var bodyName))
                    {
                        sb.AppendLine($"            yield return {bodyName}();");
                    }
                }
                if (_globalDeferCount > 0) sb.AppendLine("            __DrainGlobalDefers();");
                sb.AppendLine("        }");
            }
        }

        List<TestNode> ResolveTestFromChain(TestNode test)
        {
            var byName = new Dictionary<string, TestNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in _program.tests)
            {
                if (t.name != null) byName[t.name] = t;
            }
            var chain = new List<TestNode> { test };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (test.name != null) visited.Add(test.name);
            var cursor = test;
            while (cursor.fromParent != null && byName.TryGetValue(cursor.fromParent, out var parent))
            {
                if (parent.name != null && !visited.Add(parent.name))
                {
                    return new List<TestNode> { test };
                }
                chain.Add(parent);
                cursor = parent;
            }
            chain.Reverse();
            return chain;
        }

        void CompileType(TypeDefinitionStatement typeDefinition, StringBuilder structs)
        {
            // mirrors Compiler.Compile(TypeDefinitionStatement): packed fields, sizes, type ids
            var typeName = typeDefinition.name.variableName;
            var type = new CompiledType
            {
                typeId = _typeTable.Count + 1,
                typeName = typeName
            };

            var fields = new StringBuilder();
            int totalSize = 0;
            foreach (var decl in typeDefinition.declarations)
            {
                var member = new CompiledTypeMember { Offset = totalSize };
                string csType;
                switch (decl.type)
                {
                    case TypeReferenceNode typeRef:
                        var tc = VmUtil.GetTypeCode(typeRef.variableType);
                        member.Length = TypeCodes.GetByteSize(tc);
                        member.TypeCode = tc;
                        csType = CsType(FT.Of(tc));
                        break;
                    case StructTypeReferenceNode structTypeRef:
                        if (!_types.TryGetValue(structTypeRef.variableNode.variableName, out var structType))
                        {
                            throw new CSharpTranspileException("Referencing type that does not exist yet. " + structTypeRef.variableNode, typeDefinition);
                        }
                        member.Length = structType.byteSize;
                        member.TypeCode = TypeCodes.STRUCT;
                        member.Type = structType;
                        csType = StructCsName(structType.typeName);
                        break;
                    default:
                        throw new CSharpTranspileException("unknown type member", decl);
                }

                totalSize += member.Length;
                type.fields.Add(decl.name.variableName, member);
                fields.AppendLine($"            public {csType} {FieldName(decl.name.variableName)};");
            }

            type.byteSize = totalSize;
            _types[typeName] = type;
            _typeTable[type.typeId] = type;

            structs.AppendLine($"        public struct {StructCsName(typeName)}");
            structs.AppendLine("        {");
            structs.Append(fields);
            structs.AppendLine("        }");
            structs.AppendLine();
        }

        Region CompileFunction(FunctionStatement functionStatement)
        {
            var fn = _fnByNode[functionStatement];
            var region = new Region
            {
                name = fn.csName,
                fn = fn,
                kind = RegionKind.Function,
                isIterator = fn.iterator
            };
            region.structured = CanStructure(region.kind, functionStatement.statements);
            var previous = _region;
            _region = region;
            _scope.EnterRegion(CompileScope.RegionKind.Function);
            fn.returnType = _returnTypeGuesses.TryGetValue(fn, out var guess) ? guess : FT.Void;

            // Parameters are bound in reverse order, each through a fake declaration, which is
            // what allocates their registers (Compiler.Compile(FunctionStatement)).
            var paramNames = new string[functionStatement.parameters.Count];
            var paramTypes = new FT[functionStatement.parameters.Count];
            for (var i = functionStatement.parameters.Count - 1; i >= 0; i--)
            {
                var arg = functionStatement.parameters[i];
                var fakeDecl = new DeclarationStatement
                {
                    variableNode = arg.variable,
                    scopeType = DeclarationScopeType.Local,
                    type = arg.type,
                    startToken = arg.startToken,
                    endToken = arg.endToken
                };
                var compiledVar = DeclareScalar(fakeDecl, emitStructDefault: false);
                paramTypes[i] = VarType(compiledVar);
                paramNames[i] = LocalName(compiledVar.registerAddress, compiledVar.name);
                region.parameters.Add(compiledVar.registerAddress);
            }

            fn.paramTypes = paramTypes;
            var sig = new StringBuilder();
            if (fn.iterator)
            {
                sig.Append("IEnumerator ");
            }
            else
            {
                sig.Append(fn.returnType.IsVoid ? "void " : CsType(fn.returnType) + " ");
            }
            sig.Append(fn.csName).Append("(");
            for (var i = 0; i < paramNames.Length; i++)
            {
                if (i > 0) sig.Append(", ");
                sig.Append(CsType(paramTypes[i])).Append(' ').Append(paramNames[i]);
            }
            sig.Append(")");
            region.signature = sig.ToString();

            foreach (var statement in functionStatement.statements)
            {
                CompileStatement(statement);
            }

            // falling off the end: drain defers, return nothing
            HandleDeferExit();
            EmitReturnLines(null);
            FinishRegion(region);
            _scope.ExitRegion();
            _region = previous;
            return region;
        }

        // =====================================================================================
        //  Regions (a method body: main, a test body, a function, or a mock body)
        // =====================================================================================

        enum RegionKind
        {
            Main,
            Routine, // a gosub routine of the main program, moved into its own method
            TestBody,
            Function,
            MockBody
        }

        class Region
        {
            public string name;
            public string signature;
            public RegionKind kind;
            public bool isIterator;
            public bool isDeferBody;
            // no goto or inline gosub: loops and ifs are emitted as C# blocks (see CompileIf)
            public bool structured;
            public FnInfo fn;
            public MockContext mock;
            public List<string> lines = new List<string>();
            public readonly Dictionary<ulong, StorageDecl> locals = new Dictionary<ulong, StorageDecl>();
            public readonly List<ulong> parameters = new List<ulong>();
            public List<StorageDecl> temps = new List<StorageDecl>();
            public int labelCounter;
            public readonly Dictionary<string, string> labelDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> gosubReturnLabels = new List<string>();
            public bool usesGosub;
            public readonly List<List<string>> localDefers = new List<List<string>>();
            public readonly Stack<LoopLabels> loops = new Stack<LoopLabels>();
            public readonly HashSet<FnInfo> calls = new HashSet<FnInfo>();
            public int currentLine = -1;
            public int lastDirectiveLine = -2;
            public string lastDirectiveFile;
            public int indent = 3;

            /// <summary>Main and test bodies run in the VM's global scope: their registers are fields.</summary>
            public bool IsGlobalScope => kind == RegionKind.Main || kind == RegionKind.Routine || kind == RegionKind.TestBody;
        }

        struct LoopLabels
        {
            public string exit;
            public string skip;
        }

        class StorageDecl
        {
            public string csType;
            public string name;
            public string initializer;
        }

        class FnInfo
        {
            public string name;
            public string csName;
            public FunctionStatement node;
            public bool iterator;
            public FT returnType = FT.Void;
            public FT[] paramTypes;
        }

        /// <summary>
        /// Whether a region can be emitted as structured C#: nothing in it jumps to a label (a
        /// C# goto can't enter a block). A gosub to an extracted routine is a call, not a jump.
        /// </summary>
        bool CanStructure(RegionKind kind, IEnumerable<IStatementNode> statements)
        {
            var ok = true;
            foreach (var statement in statements)
            {
                statement.Visit(node =>
                {
                    switch (node)
                    {
                        case GotoStatement _:
                            ok = false;
                            break;
                        case GoSubStatement g when !((kind == RegionKind.Main || kind == RegionKind.Routine) && _routines.ContainsKey(g.label)):
                            ok = false;
                            break;
                    }
                });
                if (!ok) return false;
            }
            return true;
        }

        string NewLabel(string hint) => $"{hint}_{++_region.labelCounter}";

        void Line(string code)
        {
            _region.lines.Add(new string(' ', _region.indent * 4) + code);
        }

        void Label(string label)
        {
            _region.lines.Add(new string(' ', (_region.indent - 1) * 4) + label + ": ;");
        }

        void FinishRegion(Region region)
        {
            // resolve label placeholders (last declaration wins, like Compiler._labelToInstructionIndex)
            ResolveLabels(region, region.lines);
            foreach (var d in region.localDefers) ResolveLabels(region, d);
        }

        static void ResolveLabels(Region region, List<string> lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var start = line.IndexOf("@@L:", StringComparison.Ordinal);
                while (start >= 0)
                {
                    var end = line.IndexOf("@@", start + 4, StringComparison.Ordinal);
                    var labelName = line.Substring(start + 4, end - start - 4);
                    if (!region.labelDefs.TryGetValue(labelName, out var csLabel))
                    {
                        throw new CSharpTranspileException($"Compiler: unknown label location {labelName}");
                    }
                    line = line.Substring(0, start) + csLabel + line.Substring(end + 2);
                    start = line.IndexOf("@@L:", StringComparison.Ordinal);
                }
                lines[i] = line;
            }
        }

        void WriteRegion(StringBuilder sb, Region region, string signature)
        {
            sb.AppendLine();
            sb.AppendLine($"        {(region.kind == RegionKind.Main ? "" : "public ")}{signature}");
            // Fade arithmetic wraps: one unchecked block per method, not one per expression
            sb.AppendLine("        { unchecked {");
            foreach (var kvp in region.locals.OrderBy(k => k.Key))
            {
                if (region.parameters.Contains(kvp.Key)) continue;
                sb.AppendLine($"            {kvp.Value.csType} {kvp.Value.name}{kvp.Value.initializer};");
            }
            foreach (var t in region.temps)
            {
                sb.AppendLine($"            {t.csType} {t.name}{t.initializer};");
            }
            if (region.usesGosub)
            {
                sb.AppendLine("            var __gs = new FadeIntStack(8);");
            }

            var hasLocalDefers = region.localDefers.Count > 0;
            if (hasLocalDefers)
            {
                sb.AppendLine("            var __defers = new FadeIntStack(4);");
                // an assert in a test unwinds every live scope's defers (the VM's assert trampoline)
                sb.AppendLine("            try");
                sb.AppendLine("            {");
            }

            foreach (var line in region.lines)
            {
                sb.AppendLine(line);
            }

            if (region.isIterator)
            {
                // guarantees the method is an iterator even when nothing in it yields
                sb.AppendLine("            yield break;");
            }

            // shared dispatch block for gosub `return`
            if (region.gosubReturnLabels.Count > 0)
            {
                if (LineDirectivesEnabled) sb.AppendLine("#line hidden");
                sb.AppendLine("        __gosub_return:");
                sb.AppendLine("            switch (__gs.Pop())");
                sb.AppendLine("            {");
                for (var i = 0; i < region.gosubReturnLabels.Count; i++)
                {
                    sb.AppendLine($"                case {i}: goto {region.gosubReturnLabels[i]};");
                }
                sb.AppendLine("            }");
                sb.AppendLine("            throw new global::System.InvalidOperationException(\"bad gosub return\");");
            }

            if (hasLocalDefers)
            {
                sb.AppendLine("            }");
                sb.AppendLine("            finally");
                sb.AppendLine("            {");
                sb.AppendLine("                if (__IsUnwinding) __drain();");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            void __drain()");
                sb.AppendLine("            {");
                sb.AppendLine("                while (__defers.Count > 0)");
                sb.AppendLine("                {");
                sb.AppendLine("                    switch (__defers.Pop())");
                sb.AppendLine("                    {");
                for (var i = 0; i < region.localDefers.Count; i++)
                {
                    sb.AppendLine($"                        case {i}: __defer_{i}(); break;");
                }
                sb.AppendLine("                    }");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                for (var i = 0; i < region.localDefers.Count; i++)
                {
                    sb.AppendLine();
                    sb.AppendLine($"            void __defer_{i}()");
                    sb.AppendLine("            {");
                    foreach (var line in region.localDefers[i]) sb.AppendLine(line);
                    sb.AppendLine("            }");
                }
            }

            sb.AppendLine("        } }");
        }

        // =====================================================================================
        //  Variables and storage (mirrors CompileScope usage in Compiler)
        // =====================================================================================

        CompiledVariable DeclareScalar(DeclarationStatement declaration, bool emitStructDefault = true)
        {
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);
            var compiledVar = _scope.Create(declaration.variable, tc, declaration.scopeType == DeclarationScopeType.Global);
            if (tc == TypeCodes.STRUCT)
            {
                if (!(declaration.type is StructTypeReferenceNode structTypeNode))
                {
                    throw new CSharpTranspileException("compiler cannot handle non struct type ref ", declaration);
                }
                if (!_types.ContainsKey(structTypeNode.variableNode.variableName))
                {
                    throw new CSharpTranspileException("Compiler: unknown type ref " + structTypeNode.variableNode, declaration);
                }
                compiledVar.structType = structTypeNode.variableNode.variableName;

                // the VM allocates fresh struct memory every time the declaration runs
                if (emitStructDefault)
                {
                    Line($"{VarAccess(compiledVar)} = default({StructCsName(compiledVar.structType)});");
                }
                else
                {
                    VarAccess(compiledVar); // make sure storage exists
                }
            }
            return compiledVar;
        }

        FT VarType(CompiledVariable v) =>
            v.typeCode == TypeCodes.STRUCT ? FT.Struct(v.structType) : FT.Of(v.typeCode);

        /// <summary>
        /// The C# l-value for a scalar register. Main and test bodies run in the global scope;
        /// in a function or mock body, a non-global register is a fresh local.
        /// </summary>
        string VarAccess(CompiledVariable v)
        {
            var type = VarType(v);
            return Storage(v.registerAddress, v.name, CsType(type), DefaultInitializer(type), v.isGlobal);
        }

        string ArrayAccess(CompiledArrayVariable a)
        {
            var elem = a.typeCode == TypeCodes.STRUCT ? FT.Struct(a.structType.typeName) : FT.Of(a.typeCode);
            return Storage(a.registerAddress, a.name, $"FadeArray<{CsType(elem)}>", "", a.isGlobal);
        }

        string Storage(ulong register, string fadeName, string csType, string initializer, bool isGlobal)
        {
            if (_region.IsGlobalScope || isGlobal)
            {
                if (!_globals.TryGetValue(register, out var decl))
                {
                    _globals[register] = decl = new StorageDecl
                    {
                        csType = csType,
                        name = $"g{register}_{Sanitize(fadeName)}",
                        initializer = initializer
                    };
                }
                return decl.name;
            }

            if (!_region.locals.TryGetValue(register, out var local))
            {
                _region.locals[register] = local = new StorageDecl
                {
                    csType = csType,
                    name = LocalName(register, fadeName),
                    // a fresh scope's registers are zero
                    initializer = string.IsNullOrEmpty(initializer) ? " = default" : initializer
                };
            }
            return local.name;
        }

        static string LocalName(ulong register, string fadeName) => $"l{register}_{Sanitize(fadeName)}";

        CompiledVariable FakeDeclare(VariableRefNode refNode)
        {
            var fakeDecl = new DeclarationStatement
            {
                startToken = refNode.startToken,
                endToken = refNode.endToken,
                ranks = null,
                scopeType = DeclarationScopeType.Local,
                variableNode = refNode,
                type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
            };
            CompileDeclaration(fakeDecl);
            if (!_scope.TryGetVariable(refNode.variableName, out var compiledVar))
            {
                throw new CSharpTranspileException("the referenced variable has not been declared yet " + refNode.variableName, refNode);
            }
            return compiledVar;
        }

        string NewTemp(FT type)
        {
            var name = $"__t{++_tempCounter}";
            _region.temps.Add(new StorageDecl { csType = CsType(type), name = name, initializer = " = " + DefaultValue(type) });
            return name;
        }

        // =====================================================================================
        //  Helpers: names and types
        // =====================================================================================

        static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var sb = new StringBuilder(name.Length + 2);
            foreach (var c in name)
            {
                if (c == '#') sb.Append("_f");
                else if (c == '$') sb.Append("_s");
                else if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        static string FieldName(string fadeName) => "f_" + Sanitize(fadeName);
        static string StructCsName(string typeName) => "S_" + Sanitize(typeName);

        string CsType(FT t)
        {
            switch (t.tc)
            {
                case TypeCodes.INT: return "int";
                case TypeCodes.REAL: return "float";
                case TypeCodes.BOOL: return "byte";
                case TypeCodes.BYTE: return "byte";
                case TypeCodes.WORD: return "ushort";
                case TypeCodes.DWORD: return "uint";
                case TypeCodes.DINT: return "long";
                case TypeCodes.DFLOAT: return "double";
                case TypeCodes.STRING: return "string";
                case TypeCodes.STRUCT: return StructCsName(t.structName);
                case TypeCodes.VOID: return "void";
                case TypeCodes.ANY: return "object";
                default: throw new CSharpTranspileException("no C# type for type code " + t.tc);
            }
        }

        string DefaultValue(FT t)
        {
            switch (t.tc)
            {
                case TypeCodes.STRING: return "\"\"";
                case TypeCodes.STRUCT: return $"default({StructCsName(t.structName)})";
                default: return $"default({CsType(t)})";
            }
        }

        string DefaultInitializer(FT t) => t.tc == TypeCodes.STRING ? " = \"\"" : "";

        static string TypeName(FT t) => t.tc == TypeCodes.STRUCT ? t.structName : t.tc.ToString();

        static string StringLiteral(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\0': sb.Append("\\0"); break;
                    default:
                        if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        static string FloatLiteral(float f)
        {
            if (float.IsNaN(f)) return "float.NaN";
            if (float.IsPositiveInfinity(f)) return "float.PositiveInfinity";
            if (float.IsNegativeInfinity(f)) return "float.NegativeInfinity";
            return f.ToString("R", CultureInfo.InvariantCulture) + "f";
        }
    }

    /// <summary>A Fade type as the VM sees it at runtime: a type code, plus the struct name for structs.</summary>
    public struct FT : IEquatable<FT>
    {
        public byte tc;
        public string structName;

        public static FT Of(byte tc) => new FT { tc = tc };
        public static FT Struct(string name) => new FT { tc = TypeCodes.STRUCT, structName = name };
        public static readonly FT Void = new FT { tc = TypeCodes.VOID };
        public static readonly FT Int = new FT { tc = TypeCodes.INT };

        public bool IsVoid => tc == TypeCodes.VOID;
        public bool IsStruct => tc == TypeCodes.STRUCT;

        public bool Equals(FT other) => tc == other.tc && string.Equals(structName, other.structName, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is FT other && Equals(other);
        public override int GetHashCode() => tc * 397 ^ (structName?.GetHashCode() ?? 0);
        public override string ToString() => IsStruct ? structName : tc.ToString();
    }
}
