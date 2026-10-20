using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    public partial class CSharpTranspiler
    {
        /// <summary>The C# method behind a command, found from its generated executor.</summary>
        class CommandBinding
        {
            public string target;
            public ParameterInfo[] parameters;
            public Type returnType;
        }

        CommandBinding Bind(CommandInfo command, IAstNode at)
        {
            if (_commandBindings.TryGetValue(command.UniqueName, out var cached)) return cached;

            var executor = command.executor?.Method;
            if (executor == null)
            {
                throw new CSharpTranspileException($"command '{command.name}' has no executor to bind to", at);
            }

            // The source generator names executors Call_<Method>_<sig> and puts them next to the command.
            var type = executor.DeclaringType;
            var methodName = executor.Name;
            const string prefix = "Call_";
            var suffix = "_" + command.sig;
            if (methodName.StartsWith(prefix, StringComparison.Ordinal) && methodName.EndsWith(suffix, StringComparison.Ordinal))
            {
                methodName = methodName.Substring(prefix.Length, methodName.Length - prefix.Length - suffix.Length);
            }

            var candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == methodName && m.GetParameters().Length == command.args.Length)
                .Where(m => ParametersMatch(m.GetParameters(), command.args))
                .ToList();
            if (candidates.Count != 1)
            {
                throw new CSharpTranspileException(
                    $"could not find the C# method for command '{command.name}' ({candidates.Count} candidates named {type.FullName}.{methodName})", at);
            }

            var method = candidates[0];
            var binding = new CommandBinding
            {
                target = $"{CommandTypeAlias(type)}.{method.Name}",
                parameters = method.GetParameters(),
                returnType = method.ReturnType
            };
            _commandBindings[command.UniqueName] = binding;
            return binding;
        }

        /// <summary>
        /// A short name for a command class, declared once as a `using` alias: `__TextureCommands.X`
        /// instead of the full `global::` name at every call.
        /// </summary>
        string CommandTypeAlias(Type type)
        {
            if (_commandTypeAliases.TryGetValue(type, out var alias)) return alias;
            var baseName = "__" + Sanitize(type.Name.Split('`')[0]);
            alias = baseName;
            for (var n = 2; _commandTypeAliases.ContainsValue(alias); n++) alias = baseName + "_" + n;
            _commandTypeAliases[type] = alias;
            return alias;
        }

        static bool ParametersMatch(ParameterInfo[] ps, CommandArgInfo[] args)
        {
            for (var i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                var a = args[i];
                if (a.isVmArg != (p.ParameterType == typeof(VirtualMachine))) return false;
                if (a.isRef != p.ParameterType.IsByRef) return false;
                if (a.isParams && !p.ParameterType.IsArray) return false;
                var t = p.ParameterType.IsByRef ? p.ParameterType.GetElementType() : p.ParameterType;
                if (a.isParams || a.isVmArg || a.isRawArg) continue;
                if (FadeCsTypeOf(t) == null) return false;
                if (a.typeCode != TypeCodes.ANY && FadeCsTypeOf(t) != FadeCsTypeOf(CsTypeForCode(a.typeCode))) return false;
            }
            return true;
        }

        static Type CsTypeForCode(byte tc)
        {
            switch (tc)
            {
                case TypeCodes.INT: return typeof(int);
                case TypeCodes.REAL: return typeof(float);
                case TypeCodes.BOOL: return typeof(bool);
                case TypeCodes.BYTE: return typeof(byte);
                case TypeCodes.WORD: return typeof(ushort);
                case TypeCodes.DWORD: return typeof(uint);
                case TypeCodes.DINT: return typeof(long);
                case TypeCodes.DFLOAT: return typeof(double);
                case TypeCodes.STRING: return typeof(string);
                default: return typeof(object);
            }
        }

        /// <summary>The C# type a Fade value of this command parameter type is stored as here.</summary>
        static string FadeCsTypeOf(Type t)
        {
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "byte";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(ushort) || t == typeof(short)) return "ushort";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(long)) return "long";
            if (t == typeof(double)) return "double";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            return null;
        }

        static string CsTypeSyntax(Type t)
        {
            if (t == typeof(int)) return "int";
            if (t == typeof(float)) return "float";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(byte)) return "byte";
            if (t == typeof(ushort)) return "ushort";
            if (t == typeof(short)) return "short";
            if (t == typeof(uint)) return "uint";
            if (t == typeof(long)) return "long";
            if (t == typeof(double)) return "double";
            if (t == typeof(string)) return "string";
            if (t == typeof(object)) return "object";
            return "global::" + t.FullName.Replace('+', '.');
        }

        /// <summary>Converts a Fade value (already cast by the compiler's rules) to a C# parameter type.</summary>
        string ToParameter(Ex value, Type parameterType, IAstNode at)
        {
            if (parameterType == typeof(object))
            {
                if (value.type.IsStruct)
                {
                    return $"{Rt}.BadOp<{CsType(value.type)}, object>({value.code}, \"any\", {value.type.tc})";
                }
                return $"(object)({value.code})";
            }
            if (parameterType == typeof(bool)) return $"({value.code} != 0)";
            if (parameterType == typeof(short)) return $"((short)({value.code}))";
            var expected = FadeCsTypeOf(parameterType);
            if (expected == CsType(value.type)) return value.code;
            throw new CSharpTranspileException($"cannot pass a {TypeName(value.type)} to a C# {parameterType.Name} parameter", at);
        }

        string FromReturn(string call, Type returnType)
        {
            if (returnType == typeof(bool)) return $"{Rt}.FromBool({call})";
            if (returnType == typeof(short)) return $"((ushort)({call}))";
            return call;
        }

        void CompileCommandStatement(CommandStatement commandStatement)
        {
            var prev = _anf;
            _anf = _region.isIterator && commandStatement.args.Any(CanSuspend);
            try
            {
                var ex = EmitCommand(commandStatement.command, commandStatement.args, commandStatement.argMap, true, commandStatement);
                if (ex.isStatementDone) return;
                Line(ex.code + ";");
                EmitSuspendCheck(commandStatement.command);
            }
            finally
            {
                _anf = prev;
            }
        }

        void EmitSuspendCheck(CommandInfo command)
        {
            if (_region.isIterator && IsSuspending(command))
            {
                Line("if (__vm.isSuspendRequested) yield return null;");
            }
        }

        Ex EmitCommand(CommandInfo command, List<IExpressionNode> args, List<int> argMap, bool asStatement, IAstNode at)
        {
            var binding = Bind(command, at);
            var callArgs = new List<string>();
            var writebacks = new List<string>();
            var named = false;
            var argCounter = 0;
            for (var i = 0; i < command.args.Length; i++)
            {
                var desc = command.args[i];
                var parameter = binding.parameters[i];
                string code;

                if (desc.isVmArg)
                {
                    code = $"{Rt}.At(__vm, {++_siteCounter})";
                }
                else if (desc.isRawArg)
                {
                    throw new CSharpTranspileException($"command '{command.name}' takes a RawArg, which exposes VM addresses", at);
                }
                else if (desc.isParams)
                {
                    code = EmitParamsArgument(args, argCounter, parameter, at);
                    argCounter = args.Count;
                }
                else if (argCounter >= args.Count)
                {
                    if (!desc.isOptional)
                    {
                        throw new CSharpTranspileException("not enough arg expressions to meet the needs of the function", at);
                    }
                    named = true; // later arguments (e.g. a VM arg) must be passed by name
                    continue;
                }
                else if (desc.isRef)
                {
                    code = "ref " + EmitRefArgument(args[argCounter], parameter.ParameterType.GetElementType(), at, writebacks);
                    argCounter++;
                }
                else
                {
                    var value = EmitExpression(args[argCounter]);
                    var destinationTypeCode = command.args[argMap != null && argCounter < argMap.Count ? argMap[argCounter] : i].typeCode;
                    if (destinationTypeCode != TypeCodes.ANY)
                    {
                        value = Cast(value, FT.Of(destinationTypeCode));
                    }
                    code = Settle(new Ex(ToParameter(value, parameter.ParameterType, at), value.type, value.pure)).code;
                    argCounter++;
                }

                callArgs.Add(named ? $"{parameter.Name}: {code}" : code);
            }

            // with tests compiled in, calls go through the command's mock dispatcher, except the
            // mocked command's own calls inside its mock body (those reach the real command)
            var target = binding.target;
            if (_includeTests)
            {
                var id = CommandId(command, at);
                if (_region.mock == null || !_region.mock.bypassIds.Contains(id))
                {
                    _dispatchedCommands.Add(id);
                    target = $"__Cmd_{id}";
                }
            }
            var call = $"{target}({string.Join(", ", callArgs)})";

            if (binding.returnType == typeof(void))
            {
                if (_anf && !asStatement)
                {
                    throw new CSharpTranspileException($"command '{command.name}' does not return a value", at);
                }
                if (_anf || writebacks.Count > 0)
                {
                    if (!asStatement) throw new CSharpTranspileException($"command '{command.name}' does not return a value", at);
                    Line(call + ";");
                    foreach (var w in writebacks) Line(w);
                    EmitSuspendCheck(command);
                    return new Ex("", FT.Void, false) { isStatementDone = true };
                }
                return new Ex(call, FT.Void, false) { isCall = true };
            }

            var returnType = FT.Of(command.returnType);
            var converted = FromReturn(call, binding.returnType);
            if (CsType(returnType) != FadeCsTypeOf(binding.returnType))
            {
                throw new CSharpTranspileException($"command '{command.name}' returns {binding.returnType.Name} as type code {command.returnType}", at);
            }

            if (writebacks.Count > 0 && !_anf && !asStatement)
            {
                throw new CSharpTranspileException($"command '{command.name}' returns a value and writes back a ref of a different register type", at);
            }
            if (_anf || writebacks.Count > 0)
            {
                var temp = NewTemp(returnType);
                Line($"{temp} = {converted};");
                foreach (var w in writebacks) Line(w);
                EmitSuspendCheck(command);
                return new Ex(temp, returnType, true) { isStatementDone = asStatement };
            }

            return new Ex(converted, returnType, false) { isCall = binding.returnType == typeof(bool) || binding.returnType == typeof(short) ? false : true };
        }

        string EmitParamsArgument(List<IExpressionNode> args, int argCounter, ParameterInfo parameter, IAstNode at)
        {
            var elementType = parameter.ParameterType.GetElementType();
            var elementSyntax = CsTypeSyntax(elementType);
            var remaining = args.Count - argCounter;

            // spread: a single 1-rank array argument
            if (remaining == 1 && args[argCounter].ParsedType.IsArray && args[argCounter].ParsedType.rank == 1)
            {
                var array = EmitExpression(args[argCounter]);
                if (!array.isArray) throw new CSharpTranspileException("params spread needs an array", at);
                if (elementType == typeof(object)) return $"{Rt}.Box({array.code}.data)";
                if (FadeCsTypeOf(elementType) == CsType(array.type) && elementType != typeof(bool) && elementType != typeof(short))
                {
                    return $"{array.code}.Spread()";
                }
                throw new CSharpTranspileException("params spread of a mismatched array type", at);
            }

            if (remaining == 0) return $"global::System.Array.Empty<{elementSyntax}>()";

            // inline list: the compiler pushes these last-to-first, uncast
            var values = new string[remaining];
            var anyImpure = false;
            var exs = new Ex[remaining];
            if (_anf)
            {
                for (var j = args.Count - 1; j >= argCounter; j--)
                {
                    exs[j - argCounter] = Settle(EmitExpression(args[j]));
                }
            }
            else
            {
                for (var j = argCounter; j < args.Count; j++)
                {
                    exs[j - argCounter] = EmitExpression(args[j]);
                    anyImpure |= !exs[j - argCounter].pure;
                }
                if (anyImpure && remaining > 1)
                {
                    // keep the VM's right-to-left evaluation order
                    for (var j = remaining - 1; j >= 0; j--)
                    {
                        var temp = NewTemp(exs[j].type);
                        Line($"{temp} = {exs[j].code};");
                        exs[j] = new Ex(temp, exs[j].type, true);
                    }
                }
            }

            for (var k = 0; k < remaining; k++)
            {
                var ex = exs[k];
                if (elementType == typeof(object))
                {
                    values[k] = ex.type.IsStruct
                        ? $"{Rt}.BadOp<{CsType(ex.type)}, object>({ex.code}, \"any\", {ex.type.tc})"
                        : $"(object)({ex.code})";
                }
                else if (FadeCsTypeOf(elementType) == CsType(ex.type) && elementType != typeof(bool) && elementType != typeof(short))
                {
                    values[k] = ex.code;
                }
                else if (ex.type.tc != TypeCodes.STRING && !ex.type.IsStruct && elementType != typeof(string))
                {
                    // the executor reads the pushed bytes as the element type
                    values[k] = $"{Rt}.Reinterpret<{CsType(ex.type)}, {elementSyntax}>({ex.code})";
                }
                else
                {
                    throw new CSharpTranspileException("params argument type mismatch", at);
                }
            }

            return $"new {elementSyntax}[] {{ {string.Join(", ", values)} }}";
        }

        /// <summary>The l-value passed by `ref` (Compiler.Compile(AddressExpression)).</summary>
        string EmitRefArgument(IExpressionNode argExpr, Type elementType, IAstNode at, List<string> writebacks)
        {
            var variableNode = argExpr is AddressExpression addr ? addr.variableNode : argExpr as IVariableNode;
            if (variableNode == null)
            {
                throw new CSharpTranspileException("cannot use a ref parameter with an expr that isn't an address expr", at);
            }

            string lvalue;
            FT type;
            switch (variableNode)
            {
                case VariableRefNode refNode:
                {
                    if (!_scope.TryGetVariable(refNode.variableName, out var variable))
                    {
                        CompileDeclaration(new DeclarationStatement
                        {
                            startToken = refNode.startToken,
                            endToken = refNode.endToken,
                            ranks = null,
                            scopeType = DeclarationScopeType.Local,
                            variableNode = refNode,
                            type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
                        }, includeDefaultInitializer: true);
                        if (!_scope.TryGetVariable(refNode.variableName, out variable))
                        {
                            throw new CSharpTranspileException("cannot use reference to a variable that does not exist " + refNode, at);
                        }
                    }
                    lvalue = VarAccess(variable);
                    type = VarType(variable);
                    var exact = CsType(type) == FadeCsTypeOf(elementType) && elementType != typeof(bool) && elementType != typeof(short)
                                && (type.tc == TypeCodes.INT || type.tc == TypeCodes.REAL || type.tc == TypeCodes.DWORD);
                    if (!exact && type.tc != TypeCodes.STRING && !type.IsStruct && elementType != typeof(string))
                    {
                        // emulate the register round trip (MemoryMarshal.Read in, ToUInt32 out)
                        var paramSyntax = CsTypeSyntax(elementType);
                        var temp = $"__t{++_tempCounter}";
                        _region.temps.Add(new StorageDecl { csType = paramSyntax, name = temp, initializer = " = default" });
                        Line($"{temp} = {Rt}.RegRead<{CsType(type)}, {paramSyntax}>({lvalue});");
                        writebacks.Add($"{lvalue} = {Rt}.RegWrite<{paramSyntax}, {CsType(type)}>({temp});");
                        return temp;
                    }
                    break;
                }
                case ArrayIndexReference indexReference:
                {
                    if (!_scope.TryGetArray(indexReference.variableName, out var arrayVar))
                    {
                        throw new CSharpTranspileException("cannot access array since it not declared" + indexReference.variableName, at);
                    }
                    var element = ArrayElement(arrayVar, indexReference);
                    lvalue = element.code;
                    type = element.type;
                    break;
                }
                case StructFieldReference fieldRef:
                {
                    var read = EmitFieldRead(fieldRef);
                    lvalue = read.code;
                    type = read.type;
                    break;
                }
                default:
                    throw new CSharpTranspileException("cannot use the address of this expression", at);
            }

            if (CsType(type) != FadeCsTypeOf(elementType) || elementType == typeof(bool) || elementType == typeof(short))
            {
                throw new CSharpTranspileException($"ref argument of type {TypeName(type)} passed to a C# ref {elementType.Name}", at);
            }
            return lvalue;
        }
    }
}
