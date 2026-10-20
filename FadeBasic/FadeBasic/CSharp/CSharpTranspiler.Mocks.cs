using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    /// <summary>
    /// Test-only command interception. When a program has tests, every command call goes through
    /// a per-command dispatcher (`__Cmd_&lt;id&gt;`) that counts the call (in test mode) and runs an
    /// installed mock body instead of the real command, like CALL_HOST's mock-table lookup.
    /// A `mock` statement compiles its body once per matching overload (Compiler.CompileMockBodyForOverload)
    /// and installs it when the statement runs.
    /// </summary>
    public partial class CSharpTranspiler
    {
        class MockContext
        {
            public MockStatement statement;
            public CommandInfo command;
            public int commandId;
            public CommandBinding binding;
            public HashSet<int> bypassIds;
            public readonly List<MockRefBinding> refBindings = new List<MockRefBinding>();
        }

        struct MockRefBinding
        {
            public CompiledVariable value;
            public string parameter;
            public Type elementType;
            public byte typeCode;
        }

        readonly Dictionary<int, List<int>> _mocksByCommand = new Dictionary<int, List<int>>();

        List<int> ResolveMockCommandIds(string commandName)
        {
            var ids = new List<int>();
            for (var i = 0; i < _commands.Commands.Count; i++)
            {
                if (string.Equals(_commands.Commands[i].name, commandName, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(i);
                }
            }
            return ids;
        }

        int CommandId(CommandInfo command, IAstNode at)
        {
            if (!_commandIds.TryGetValue(command.UniqueName, out var id))
            {
                throw new CSharpTranspileException("compiler: could not find method address: " + command.name, at);
            }
            return id;
        }

        void CompileMock(MockStatement mockStatement)
        {
            var allIds = ResolveMockCommandIds(mockStatement.commandName);
            foreach (var id in allIds)
            {
                if (mockStatement.parameters.Count > 0)
                {
                    var realCount = (_commands.Commands[id].args ?? Array.Empty<CommandArgInfo>()).Count(a => !a.isVmArg);
                    if (realCount != mockStatement.parameters.Count) continue;
                }

                var mockIndex = CompileMockBody(mockStatement, id);
                Line($"__InstallMock({id}, {mockIndex});");
            }
        }

        int CompileMockBody(MockStatement mockStatement, int commandId)
        {
            var command = _commands.Commands[commandId];
            var binding = Bind(command, mockStatement);
            var index = _mockCounter++;
            if (!_mocksByCommand.TryGetValue(commandId, out var list)) _mocksByCommand[commandId] = list = new List<int>();
            if (!list.Contains(index)) list.Add(index);

            var context = new MockContext
            {
                statement = mockStatement,
                command = command,
                commandId = commandId,
                binding = binding,
                bypassIds = new HashSet<int>(ResolveMockCommandIds(mockStatement.commandName))
            };
            var region = new Region
            {
                name = $"__Mock_{index}",
                kind = RegionKind.MockBody,
                mock = context,
                isIterator = false
            };
            region.structured = CanStructure(region.kind, mockStatement.body);

            // signature: the real method's parameters, minus the VM
            var parts = new List<string>();
            for (var i = 0; i < binding.parameters.Length; i++)
            {
                if (command.args[i].isVmArg) continue;
                var p = binding.parameters[i];
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                parts.Add($"{(p.ParameterType.IsByRef ? "ref " : "")}{CsTypeSyntax(t)} p{i}");
            }
            var returnSyntax = binding.returnType == typeof(void) ? "void" : CsTypeSyntax(binding.returnType);
            region.signature = $"{returnSyntax} __Mock_{index}({string.Join(", ", parts)})";

            var previous = _region;
            _region = region;
            _scope.EnterRegion(CompileScope.RegionKind.Function);
            try
            {
                // bind args last-to-first, as the body prelude pops them
                var real = Enumerable.Range(0, command.args.Length).Where(i => !command.args[i].isVmArg).ToList();
                for (var r = real.Count - 1; r >= 0; r--)
                {
                    var argIndex = real[r];
                    var argInfo = command.args[argIndex];
                    if (r >= mockStatement.parameters.Count) continue; // unnamed: discarded
                    var paramRef = mockStatement.parameters[r];
                    var parameter = binding.parameters[argIndex];
                    var elementType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType;

                    if (argInfo.isParams)
                    {
                        if (argInfo.typeCode == TypeCodes.ANY) continue;
                        var arrayVar = _scope.CreateArray(paramRef.variableName, 1, argInfo.typeCode, false);
                        arrayVar.rankSizeRegisterAddresses[0] = _scope.AllocateRegister();
                        arrayVar.rankIndexScalerRegisterAddresses[0] = _scope.AllocateRegister();
                        var elem = parameter.ParameterType.GetElementType();
                        if (FadeCsTypeOf(elem) != CsType(FT.Of(argInfo.typeCode)) || elem == typeof(bool) || elem == typeof(short))
                        {
                            throw new CSharpTranspileException("mock params argument of a converted type", mockStatement);
                        }
                        Line($"{ArrayAccess(arrayVar)} = FadeArray<{CsType(FT.Of(argInfo.typeCode))}>.FromValues(p{argIndex});");
                        continue;
                    }

                    if (!VmUtil.TryGetVariableType(argInfo.typeCode, out var valueType))
                    {
                        throw new CSharpTranspileException($"mock parameter {paramRef.variableName} binds an untyped argument", mockStatement);
                    }

                    if (argInfo.isRef)
                    {
                        // a hidden register for the caller's address, then the visible value
                        CompileDeclaration(new DeclarationStatement
                        {
                            variableNode = new VariableRefNode(paramRef.startToken, "$$mockptr_" + paramRef.variableName),
                            scopeType = DeclarationScopeType.Local,
                            type = new TypeReferenceNode(VariableType.Integer, paramRef.startToken)
                        });
                    }

                    CompileDeclaration(new DeclarationStatement
                    {
                        variableNode = paramRef,
                        scopeType = DeclarationScopeType.Local,
                        type = new TypeReferenceNode(valueType, paramRef.startToken)
                    });
                    var incoming = new Ex(FromCsValue($"p{argIndex}", elementType), FT.Of(argInfo.typeCode), true);
                    CompileStore(paramRef, Cast(incoming, FT.Of(argInfo.typeCode)), mockStatement);

                    if (argInfo.isRef)
                    {
                        _scope.TryGetVariable(paramRef.variableName, out var valueVar);
                        context.refBindings.Add(new MockRefBinding
                        {
                            value = valueVar,
                            parameter = $"p{argIndex}",
                            elementType = elementType,
                            typeCode = argInfo.typeCode
                        });
                    }
                }

                foreach (var statement in mockStatement.body)
                {
                    CompileStatement(statement);
                }

                EmitMockExit(mockStatement.endmockExpression, mockStatement);
                FinishRegion(region);
            }
            finally
            {
                _scope.ExitRegion();
                _region = previous;
            }

            _extraRegions.Add(region);
            return index;
        }

        void CompileExitMock(MockExitMockStatement exitMock)
        {
            if (_region.kind != RegionKind.MockBody || _region.isDeferBody)
            {
                throw new CSharpTranspileException("exitmock outside of a mock body", exitMock);
            }
            EmitMockExit(exitMock.expression, exitMock);
        }

        /// <summary>`exitmock` / `endmock`: the value (cast to the command's type), ref write-backs, defers, return.</summary>
        void EmitMockExit(IExpressionNode valueExpression, IAstNode at)
        {
            var context = _region.mock;
            var returnType = context.binding.returnType;
            string value = null;
            if (returnType != typeof(void))
            {
                if (valueExpression != null)
                {
                    var ex = EmitRoot(valueExpression);
                    if (context.command.returnType != 0 && context.command.returnType != TypeCodes.VOID)
                    {
                        ex = Cast(ex, FT.Of(context.command.returnType));
                    }
                    var temp = NewTemp(ex.type);
                    Line($"{temp} = {ex.code};");
                    value = ToCsValue(new Ex(temp, ex.type, true), returnType, at);
                }
                else
                {
                    // the VM returns with nothing on the stack; a caller reading it gets garbage
                    value = $"default({CsTypeSyntax(returnType)})";
                }
            }
            else if (valueExpression != null)
            {
                EmitRoot(valueExpression); // evaluated, then discarded with the frame
            }

            foreach (var rb in context.refBindings)
            {
                var current = Cast(new Ex(VarAccess(rb.value), VarType(rb.value), true), FT.Of(rb.typeCode));
                Line($"{rb.parameter} = {ToCsValue(current, rb.elementType, at)};");
            }

            HandleDeferExit();
            Line(value == null ? "return;" : $"return {value};");
        }

        void CompileForbid(MockForbidStatement forbid)
        {
            if (_region.kind != RegionKind.MockBody)
            {
                throw new CSharpTranspileException("forbid outside of a mock body", forbid);
            }
            var reason = forbid.reason != null ? EmitRoot(forbid.reason) : new Ex("\"\"", FT.Of(TypeCodes.STRING), true);
            var source = "forbidden command was called: " + (_region.mock.command.name ?? "<unknown>");
            Line($"__AssertFailed({reason.code}, {StringLiteral(source)}, {Site});");
        }

        /// <summary>A C# argument value as the Fade value the VM would have pushed for it.</summary>
        static string FromCsValue(string code, Type t)
        {
            if (t == typeof(bool)) return $"FadeRt.FromBool({code})";
            if (t == typeof(short)) return $"((ushort)({code}))";
            return code;
        }

        /// <summary>A Fade value as the C# type a command method takes or returns.</summary>
        string ToCsValue(Ex value, Type t, IAstNode at)
        {
            if (t == typeof(bool)) return $"({value.code} != 0)";
            if (t == typeof(short)) return $"((short)({value.code}))";
            if (t == typeof(object)) return $"(object)({value.code})";
            if (FadeCsTypeOf(t) == CsType(value.type)) return value.code;
            throw new CSharpTranspileException($"cannot convert a {TypeName(value.type)} to {t.Name}", at);
        }

        static string DefaultLiteral(object value, Type t)
        {
            switch (value)
            {
                case null: return "default";
                case string str: return StringLiteral(str);
                case bool b: return b ? "true" : "false";
                case float f: return FloatLiteral(f);
                case double d: return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d";
                case char ch: return "'\\u" + ((int)ch).ToString("x4") + "'";
                default:
                    return $"({CsTypeSyntax(t)})({Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)})";
            }
        }

        /// <summary>Per-command dispatchers for programs with tests (see class summary).</summary>
        void WriteDispatchers(System.Text.StringBuilder sb)
        {
            foreach (var id in _dispatchedCommands.OrderBy(i => i))
            {
                var command = _commands.Commands[id];
                var binding = Bind(command, null);
                var ps = binding.parameters;
                var decl = new List<string>();
                var callAll = new List<string>();
                var callMock = new List<string>();
                for (var i = 0; i < ps.Length; i++)
                {
                    var p = ps[i];
                    var isRef = p.ParameterType.IsByRef;
                    var t = isRef ? p.ParameterType.GetElementType() : p.ParameterType;
                    var name = "@" + p.Name;
                    var defaultValue = p.HasDefaultValue ? " = " + DefaultLiteral(p.DefaultValue, t) : "";
                    var paramsMod = p.IsDefined(typeof(ParamArrayAttribute), false) ? "params " : "";
                    decl.Add($"{paramsMod}{(isRef ? "ref " : "")}{CsTypeSyntax(t)} {name}{defaultValue}");
                    callAll.Add($"{(isRef ? "ref " : "")}{name}");
                    if (!command.args[i].isVmArg) callMock.Add($"{(isRef ? "ref " : "")}{name}");
                }

                var isVoid = binding.returnType == typeof(void);
                var ret = isVoid ? "void" : CsTypeSyntax(binding.returnType);
                sb.AppendLine();
                sb.AppendLine($"        {ret} __Cmd_{id}({string.Join(", ", decl)})");
                sb.AppendLine("        {");
                sb.AppendLine($"            __CountCall({id});");
                if (_mocksByCommand.TryGetValue(id, out var mocks) && mocks.Count > 0)
                {
                    sb.AppendLine($"            if (__mocks != null && __mocks.TryGetValue({id}, out var __m))");
                    sb.AppendLine("            {");
                    sb.AppendLine("                switch (__m)");
                    sb.AppendLine("                {");
                    foreach (var m in mocks)
                    {
                        var call = $"__Mock_{m}({string.Join(", ", callMock)})";
                        sb.AppendLine(isVoid
                            ? $"                    case {m}: {call}; return;"
                            : $"                    case {m}: return {call};");
                    }
                    sb.AppendLine("                }");
                    sb.AppendLine("            }");
                }
                var real = $"{binding.target}({string.Join(", ", callAll)})";
                sb.AppendLine(isVoid ? $"            {real};" : $"            return {real};");
                sb.AppendLine("        }");
            }
        }
    }
}
