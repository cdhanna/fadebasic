using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    /// <summary>An emitted C# expression and the Fade type the VM would give its value.</summary>
    class Ex
    {
        public string code;
        public FT type;

        /// <summary>No side effects: evaluating it later (or twice) can't change the program.</summary>
        public bool pure;

        /// <summary>A bare array variable (array assignment, len, params spread).</summary>
        public bool isArray;

        /// <summary>A method call, so it can stand alone as a C# statement.</summary>
        public bool isCall;

        /// <summary>Already written out as statements (an iterator call made as a statement).</summary>
        public bool isStatementDone;

        /// <summary>
        /// A C# bool expression equal to `CAST INT, &gt; 0` of this value, evaluating the same things
        /// in the same order, when there is a shorter one than the 0/1 value: `a &lt; b` for the
        /// comparison `(a &lt; b ? 1 : 0)`. Conditions (if, while, until, assert) use it.
        /// </summary>
        public string condition;

        public Ex(string code, FT type, bool pure)
        {
            this.code = code;
            this.type = type;
            this.pure = pure;
        }
    }

    public partial class CSharpTranspiler
    {
        const string Rt = "FadeRt";

        /// <summary>
        /// True while emitting an expression that can suspend: every intermediate value goes into
        /// a temp, in the VM's evaluation order, so a `yield return` can sit between any two steps.
        /// </summary>
        bool _anf;
        int _patternCounter;

        Ex EmitRoot(IExpressionNode expr, bool asStatement = false)
        {
            var prev = _anf;
            _anf = CanSuspend(expr);
            try
            {
                return EmitExpression(expr, asStatement);
            }
            finally
            {
                _anf = prev;
            }
        }

        /// <summary>In ANF mode, evaluate the value now into a temp.</summary>
        Ex Settle(Ex ex)
        {
            if (!_anf || ex.isArray || ex.type.IsVoid) return ex;
            if (ex.pure && IsLiteralCode(ex.code)) return ex;
            var temp = NewTemp(ex.type);
            Line($"{temp} = {ex.code};");
            return new Ex(temp, ex.type, pure: true);
        }

        static bool IsLiteralCode(string code)
        {
            if (code.Length == 0) return false;
            var c = code[0];
            return char.IsDigit(c) || c == '"' || code.StartsWith("default(", StringComparison.Ordinal) ||
                   code.StartsWith("float.", StringComparison.Ordinal);
        }

        Ex EmitExpression(IExpressionNode expr, bool asStatement = false)
        {
            switch (expr)
            {
                case DefaultValueExpression defExpr:
                {
                    switch (defExpr.ParsedType.type)
                    {
                        case VariableType.String:
                            return new Ex("\"\"", FT.Of(TypeCodes.STRING), true);
                        case VariableType.Struct:
                            if (!_types.ContainsKey(defExpr.ParsedType.structName))
                                throw new CSharpTranspileException("unknown type reference" + defExpr.ParsedType.structName, expr);
                            return new Ex($"default({StructCsName(defExpr.ParsedType.structName)})", FT.Struct(defExpr.ParsedType.structName), true);
                        default:
                            var t = FT.Of(VmUtil.GetTypeCode(defExpr.ParsedType.type));
                            return new Ex($"default({CsType(t)})", t, true);
                    }
                }
                case CommandExpression commandExpr:
                    return EmitCommand(commandExpr.command, commandExpr.args, commandExpr.argMap, asStatement, expr);
                case LenExpression lenExpr:
                    return EmitLen(lenExpr);
                case DimsExpression dimsExpr:
                {
                    if (dimsExpr.inner is VariableRefNode dimsVarRef && _scope.TryGetArray(dimsVarRef.variableName, out var dimsArrayVar))
                    {
                        return new Ex((dimsArrayVar.rankSizeRegisterAddresses.Length - 1).ToString(CultureInfo.InvariantCulture), FT.Int, true);
                    }
                    return new Ex("0", FT.Int, true);
                }
                case BytesExpression bytesExpr:
                    return EmitBytes(bytesExpr);
                case CallCountExpression callCount:
                {
                    // invocation counts are only kept in test mode; the count spans every overload
                    var ids = callCount.commandName != null ? ResolveMockCommandIds(callCount.commandName) : new List<int>();
                    if (ids.Count == 0 || !_includeTests) return new Ex("0", FT.Int, true);
                    return Settle(new Ex("(" + string.Join(" + ", ids.Select(i => $"__CallCount({i})")) + ")", FT.Int, true));
                }
                case LiteralStringExpression literalString:
                    return new Ex(StringLiteral(literalString.value), FT.Of(TypeCodes.STRING), true);
                case LiteralRealExpression literalReal:
                    return new Ex(FloatLiteral(literalReal.value), FT.Of(TypeCodes.REAL), true);
                case LiteralIntExpression literalInt:
                    return new Ex(literalInt.value.ToString(CultureInfo.InvariantCulture), FT.Int, true);
                case ArrayIndexReference arrayRef:
                {
                    if (!_scope.TryGetArray(arrayRef.variableName, out var arrayVar))
                    {
                        return EmitFunctionCall(arrayRef, asStatement);
                    }
                    return Settle(ArrayElement(arrayVar, arrayRef));
                }
                case StructFieldReference structRef:
                    return Settle(EmitFieldRead(structRef));
                case VariableRefNode variableRef:
                {
                    if (_scope.TryGetArray(variableRef.variableName, out var arrayVar))
                    {
                        return new Ex(ArrayAccess(arrayVar), ElementType(arrayVar), true) { isArray = true };
                    }
                    if (!_scope.TryGetVariable(variableRef.variableName, out var compiledVar))
                    {
                        compiledVar = FakeDeclare(variableRef);
                    }
                    if (compiledVar.typeCode == TypeCodes.STRUCT && !_types.ContainsKey(compiledVar.structType ?? ""))
                    {
                        throw new CSharpTranspileException("Referencing type that does not exist yet. " + compiledVar.name, expr);
                    }
                    return Settle(new Ex(VarAccess(compiledVar), VarType(compiledVar), true));
                }
                case UnaryOperationExpression unary:
                {
                    var rhs = EmitExpression(unary.rhs);
                    switch (unary.operationType)
                    {
                        case UnaryOperationType.Not:
                            return Settle(Not(rhs));
                        case UnaryOperationType.Negate:
                            return Settle(Arithmetic(OperationType.Mult, rhs, new Ex("-1", FT.Int, true)));
                        case UnaryOperationType.BitwiseNot:
                            return Settle(BitwiseNot(rhs));
                        default:
                            throw new CSharpTranspileException("unsupported unary operation " + unary.operationType, expr);
                    }
                }
                case BinaryOperandExpression op:
                    return EmitBinary(op);
                case AddressExpression _:
                case DereferenceExpression _:
                    throw new CSharpTranspileException("pointers are not supported", expr);
                default:
                    throw new CSharpTranspileException("unknown expression " + expr?.GetType().Name, expr);
            }
        }

        // ---- reads ------------------------------------------------------------------------

        FT ElementType(CompiledArrayVariable a) =>
            a.typeCode == TypeCodes.STRUCT ? FT.Struct(a.structType.typeName) : FT.Of(a.typeCode);

        Ex ArrayElement(CompiledArrayVariable arrayVar, ArrayIndexReference arrayRef)
        {
            if (arrayRef.rankExpressions.Count != arrayVar.rankSizeRegisterAddresses.Length || arrayRef.rankExpressions.Count > 5)
            {
                throw new CSharpTranspileException($"array {arrayRef.variableName} indexed with the wrong number of ranks", arrayRef);
            }

            var access = ArrayAccess(arrayVar);
            var indexes = new List<string>();
            var pure = true;
            foreach (var rankExpr in arrayRef.rankExpressions)
            {
                var idx = EmitExpression(rankExpr);
                pure &= idx.pure;
                // BOUNDS_CHECK reads the index with ReadAsInt, which only accepts an INT
                indexes.Add(idx.type.tc == TypeCodes.INT
                    ? idx.code
                    : $"{Rt}.BadOp<{CsType(idx.type)}, int>({idx.code}, \"array index\", {idx.type.tc})");
            }
            return new Ex($"{access}.data[{access}.Idx({string.Join(", ", indexes)}, {Site})]", ElementType(arrayVar), pure);
        }

        Ex EmitFieldRead(StructFieldReference structRef)
        {
            switch (structRef.left)
            {
                case VariableRefNode variableRef:
                {
                    if (!_scope.TryGetVariable(variableRef.variableName, out var compiledVar))
                    {
                        compiledVar = FakeDeclare(variableRef);
                    }
                    if (compiledVar.structType == null || !_types.TryGetValue(compiledVar.structType, out var type))
                    {
                        throw new CSharpTranspileException("Unknown type reference for " + variableRef.variableName, structRef);
                    }
                    var path = FieldPath(type, structRef.right, out var fieldType);
                    return new Ex(VarAccess(compiledVar) + path, fieldType, true);
                }
                case ArrayIndexReference arrayRef:
                {
                    if (!_scope.TryGetArray(arrayRef.variableName, out var leftArrayVar) || leftArrayVar.structType == null)
                    {
                        throw new CSharpTranspileException("the referenced array has not been declared yet " + arrayRef.variableName, structRef);
                    }
                    var path = FieldPath(leftArrayVar.structType, structRef.right, out var fieldType);
                    var element = ArrayElement(leftArrayVar, arrayRef);
                    return new Ex(element.code + path, fieldType, element.pure);
                }
                default:
                    throw new CSharpTranspileException("Cannot eval left based nested struct pointer", structRef);
            }
        }

        Ex EmitLen(LenExpression lenExpr)
        {
            if (lenExpr.inner == null) return new Ex("0", FT.Int, true);

            if (lenExpr.inner is VariableRefNode lenVarRef && _scope.TryGetArray(lenVarRef.variableName, out var lenArrayVar))
            {
                var access = ArrayAccess(lenArrayVar);
                var rankCount = lenArrayVar.rankSizeRegisterAddresses.Length;
                if (lenExpr.dimension == null || lenExpr.dimension is LiteralIntExpression)
                {
                    var d = lenExpr.dimension is LiteralIntExpression litDim ? litDim.value : 0;
                    if (d < 0 || d >= rankCount)
                    {
                        throw new CSharpTranspileException($"len dimension [{d}] out of range for array with [{rankCount}] dimensions", lenExpr);
                    }
                    return Settle(new Ex($"({access} == null ? 0 : {access}.n{d})", FT.Int, true));
                }

                var dim = Cast(EmitExpression(lenExpr.dimension), FT.Int);
                _scope.AllocateRegister(); // the VM spills the dimension to a scratch register
                return Settle(new Ex($"{Rt}.LenDim({access}, {dim.code}, {rankCount}, {Site})", FT.Int, dim.pure));
            }

            var inner = EmitExpression(lenExpr.inner);
            if (inner.type.tc != TypeCodes.STRING)
            {
                return Settle(new Ex($"{Rt}.BadOp<{CsType(inner.type)}, int>({inner.code}, \"len\", {inner.type.tc})", FT.Int, false));
            }
            return Settle(new Ex($"{Rt}.Len({inner.code})", FT.Int, inner.pure));
        }

        Ex EmitBytes(BytesExpression bytesExpr)
        {
            if (bytesExpr.resolvedTypeName != null)
            {
                if (!_types.TryGetValue(bytesExpr.resolvedTypeName, out var bytesType))
                    throw new CSharpTranspileException("bytes() references unknown type " + bytesExpr.resolvedTypeName, bytesExpr);
                return new Ex(bytesType.byteSize.ToString(CultureInfo.InvariantCulture), FT.Int, true);
            }

            if (bytesExpr.inner is VariableRefNode bytesVarRef)
            {
                if (_scope.TryGetArray(bytesVarRef.variableName, out var bytesArrayVar))
                {
                    var access = ArrayAccess(bytesArrayVar);
                    return Settle(new Ex($"({access} == null ? 0 : {access}.data.Length * {bytesArrayVar.byteSize})", FT.Int, true));
                }
                if (_scope.TryGetVariable(bytesVarRef.variableName, out var bytesVar))
                {
                    if (bytesVar.typeCode == TypeCodes.STRUCT)
                    {
                        if (!_types.TryGetValue(bytesVar.structType, out var st))
                            throw new CSharpTranspileException("bytes() variable references unknown type " + bytesVar.structType, bytesExpr);
                        return new Ex(st.byteSize.ToString(CultureInfo.InvariantCulture), FT.Int, true);
                    }
                    if (bytesVar.typeCode == TypeCodes.STRING)
                    {
                        return Settle(new Ex($"({Rt}.Len({VarAccess(bytesVar)}) * 4)", FT.Int, true));
                    }
                    return new Ex(TypeCodes.GetByteSize(bytesVar.typeCode).ToString(CultureInfo.InvariantCulture), FT.Int, true);
                }
                if (_types.TryGetValue(bytesVarRef.variableName, out var fallbackType))
                {
                    return new Ex(fallbackType.byteSize.ToString(CultureInfo.InvariantCulture), FT.Int, true);
                }
                throw new CSharpTranspileException("bytes() argument is neither a variable nor a type, " + bytesVarRef.variableName, bytesExpr);
            }

            if (bytesExpr.inner == null) return new Ex("0", FT.Int, true);
            var inner = EmitExpression(bytesExpr.inner);
            switch (inner.type.tc)
            {
                case TypeCodes.STRING:
                    return Settle(new Ex($"({Rt}.Len({inner.code}) * 4)", FT.Int, inner.pure));
                case TypeCodes.STRUCT:
                    return Settle(new Ex($"{Rt}.Discard({inner.code}, {_types[inner.type.structName].byteSize})", FT.Int, inner.pure));
                default:
                    return Settle(new Ex($"{Rt}.Discard({inner.code}, {TypeCodes.GetByteSize(inner.type.tc)})", FT.Int, inner.pure));
            }
        }

        // ---- function calls ---------------------------------------------------------------

        Ex EmitFunctionCall(ArrayIndexReference call, bool asStatement)
        {
            if (!_functions.TryGetValue(call.variableName, out var fn))
            {
                throw new CSharpTranspileException("Compiler: unknown function location " + call.variableName, call);
            }

            _region.calls.Add(fn);
            var parameters = fn.node.parameters;
            if (parameters.Count != call.rankExpressions.Count)
            {
                throw new CSharpTranspileException($"function {fn.name} takes {parameters.Count} arguments, not {call.rankExpressions.Count}", call);
            }

            // arguments are evaluated in order; the callee casts each to its parameter type
            var args = new List<string>();
            for (var i = 0; i < call.rankExpressions.Count; i++)
            {
                var arg = EmitExpression(call.rankExpressions[i]);
                var paramType = ParamType(parameters[i]);
                var cast = Cast(arg, paramType);
                if (paramType.IsStruct && (!arg.type.IsStruct || arg.type.structName != paramType.structName))
                {
                    throw new CSharpTranspileException($"cannot pass {TypeName(arg.type)} as struct {paramType.structName}", call);
                }
                args.Add(Settle(cast).code);
            }

            var returnType = _returnTypeGuesses.TryGetValue(fn, out var guess) ? guess : FT.Void;
            var invocation = $"{fn.csName}({string.Join(", ", args)})";

            if (fn.iterator)
            {
                if (!_region.isIterator || !_anf)
                {
                    throw new CSharpTranspileException($"internal: suspending function {fn.name} called outside an iterator", call);
                }
                Line($"yield return {invocation};");
                if (returnType.IsVoid)
                {
                    if (!asStatement) throw new CSharpTranspileException($"function {fn.name} does not return a value", call);
                    return new Ex("", FT.Void, false) { isStatementDone = true };
                }
                var temp = NewTemp(returnType);
                Line($"{temp} = __r_{fn.csName};");
                return new Ex(temp, returnType, true) { isStatementDone = asStatement };
            }

            if (returnType.IsVoid)
            {
                if (!asStatement) throw new CSharpTranspileException($"function {fn.name} does not return a value", call);
                return new Ex(invocation, FT.Void, false) { isCall = true };
            }

            var result = new Ex(invocation, returnType, false) { isCall = true };
            if (asStatement) return result;
            return Settle(result);
        }

        FT ParamType(ParameterNode p)
        {
            var tc = VmUtil.GetTypeCode(p.type.variableType);
            if (tc == TypeCodes.STRUCT && p.type is StructTypeReferenceNode s) return FT.Struct(s.variableNode.variableName);
            return FT.Of(tc);
        }

        // ---- operators --------------------------------------------------------------------

        static FT Promote(FT lhs, FT rhs)
        {
            // VmUtil.ReadTwoValues: the higher ORDER_PREC wins; on a tie the left operand's type wins
            return TypeCodes.GetOrder(rhs.tc) > TypeCodes.GetOrder(lhs.tc) ? rhs : lhs;
        }

        static bool IsNumeric(byte tc) =>
            tc == TypeCodes.INT || tc == TypeCodes.REAL || tc == TypeCodes.BOOL || tc == TypeCodes.BYTE ||
            tc == TypeCodes.WORD || tc == TypeCodes.DWORD || tc == TypeCodes.DINT || tc == TypeCodes.DFLOAT;

        static bool IsIntegral(byte tc) =>
            tc == TypeCodes.INT || tc == TypeCodes.BOOL || tc == TypeCodes.BYTE ||
            tc == TypeCodes.WORD || tc == TypeCodes.DWORD || tc == TypeCodes.DINT;

        /// <summary>CastInlineSpan, as C#.</summary>
        Ex Cast(Ex ex, FT to)
        {
            var from = ex.type;
            if (from.tc == to.tc && (from.tc != TypeCodes.STRUCT || from.structName == to.structName)) return ex;
            if ((from.tc == TypeCodes.BOOL || from.tc == TypeCodes.BYTE) && (to.tc == TypeCodes.BOOL || to.tc == TypeCodes.BYTE))
            {
                return new Ex(ex.code, to, ex.pure);
            }

            var supported = false;
            if (IsNumeric(from.tc) && IsNumeric(to.tc))
            {
                // every numeric pair has a case, except REAL->REAL style no-ops handled above
                supported = true;
            }

            if (!supported)
            {
                if (to.tc == TypeCodes.STRUCT && from.tc == TypeCodes.STRUCT)
                {
                    throw new CSharpTranspileException($"cannot convert struct {from.structName} to {to.structName}");
                }
                return new Ex($"{Rt}.BadCast<{CsType(from)}, {CsType(to)}>({ex.code}, {from.tc}, {to.tc})", to, false);
            }

            return new Ex($"(({CsType(to)})({ex.code}))", to, ex.pure);
        }

        /// <summary>CAST INT then &gt; 0: how `if`, `while` and `until` read a condition.</summary>
        string Truth(Ex ex) => ex.condition ?? $"({Cast(ex, FT.Int).code} > 0)";

        /// <summary>`(a &lt; b)` as `a &lt; b`, for a spot that brings its own parentheses.</summary>
        static string Bare(string code)
        {
            if (code.Length < 2 || code[0] != '(' || code[code.Length - 1] != ')') return code;
            if (code.IndexOf('"') >= 0 || code.IndexOf('\'') >= 0) return code;
            var depth = 0;
            for (var i = 0; i < code.Length - 1; i++)
            {
                if (code[i] == '(') depth++;
                else if (code[i] == ')' && --depth == 0) return code; // the first ( closes early
            }
            return code.Substring(1, code.Length - 2);
        }

        string Logical(Ex ex) => $"({Truth(ex)} ? 1 : 0)";

        static string Narrow(byte tc, string code)
        {
            switch (tc)
            {
                case TypeCodes.BOOL:
                case TypeCodes.BYTE: return $"((byte)({code}))";
                case TypeCodes.WORD: return $"((ushort)({code}))";
                default: return $"({code})";
            }
        }

        Ex Arithmetic(OperationType op, Ex lhs, Ex rhs)
        {
            var p = Promote(lhs.type, rhs.type);
            var a = Cast(lhs, p);
            var b = Cast(rhs, p);
            var pure = a.pure && b.pure;
            string code;
            switch (op)
            {
                case OperationType.Add:
                    if (p.tc == TypeCodes.STRING) code = $"string.Concat({a.code}, {b.code})";
                    else if (IsNumeric(p.tc)) code = Narrow(p.tc, $"{a.code} + {b.code}");
                    else code = BadOp(a, b, "add", p);
                    break;
                case OperationType.Mult:
                    code = IsNumeric(p.tc) ? Narrow(p.tc, $"{a.code} * {b.code}") : BadOp(a, b, "multiply", p);
                    break;
                case OperationType.Divide:
                    code = IsNumeric(p.tc)
                        ? $"{Rt}.Div({TypedArg(a)}, {TypedArg(b)}, {Site})"
                        : BadOp(a, b, "divide", p);
                    break;
                case OperationType.Mod:
                    code = IsNumeric(p.tc)
                        ? $"{Rt}.Mod({TypedArg(a)}, {TypedArg(b)})"
                        : BadOp(a, b, "mod", p);
                    break;
                case OperationType.RaisePower:
                    switch (p.tc)
                    {
                        case TypeCodes.REAL:
                            code = $"{Rt}.Pow({a.code}, {b.code}, {Site})";
                            break;
                        case TypeCodes.DFLOAT:
                            code = $"global::System.Math.Pow({a.code}, {b.code})";
                            break;
                        default:
                            code = IsIntegral(p.tc)
                                ? $"(({CsType(p)})global::System.Math.Pow({a.code}, {b.code}))"
                                : BadOp(a, b, "power", p);
                            break;
                    }
                    break;
                default:
                    throw new CSharpTranspileException("not an arithmetic operation " + op);
            }
            return new Ex(code, p, pure);
        }

        /// <summary>Make sure overload resolution picks the overload for the value's own type.</summary>
        string TypedArg(Ex ex) => IsLiteralCode(ex.code) && ex.type.tc != TypeCodes.INT && ex.type.tc != TypeCodes.STRING
            ? $"({CsType(ex.type)})({ex.code})"
            : ex.code;

        string BadOp(Ex a, Ex b, string op, FT p) =>
            $"{Rt}.BadOp<{CsType(a.type)}, {CsType(b.type)}, int>({a.code}, {b.code}, \"{op}\", {p.tc})";

        Ex Compare(OperationType op, Ex lhs, Ex rhs)
        {
            var p = Promote(lhs.type, rhs.type);
            var a = Cast(lhs, p);
            var b = Cast(rhs, p);
            var pure = a.pure && b.pure;
            string code;
            if (p.tc == TypeCodes.STRING)
            {
                switch (op)
                {
                    case OperationType.GreaterThan: code = $"{Rt}.Gt({a.code}, {b.code})"; break;
                    case OperationType.LessThan: code = $"{Rt}.Gt({b.code}, {a.code})"; break;
                    case OperationType.GreaterThanOrEqualTo: code = $"{Rt}.Gte({a.code}, {b.code})"; break;
                    case OperationType.LessThanOrEqualTo: code = $"{Rt}.Gte({b.code}, {a.code})"; break;
                    case OperationType.EqualTo: code = $"{Rt}.Eq({a.code}, {b.code})"; break;
                    case OperationType.NotEqualTo: code = $"(1 - {Rt}.Eq({a.code}, {b.code}))"; break;
                    default: throw new CSharpTranspileException("not a comparison " + op);
                }
                return new Ex(code, FT.Int, pure);
            }

            if (!IsNumeric(p.tc))
            {
                return new Ex(BadOp(a, b, "comparison", p), FT.Int, false);
            }

            string condition;
            switch (op)
            {
                case OperationType.GreaterThan: condition = $"({a.code} > {b.code})"; break;
                case OperationType.LessThan: condition = $"({a.code} < {b.code})"; break;
                case OperationType.GreaterThanOrEqualTo: condition = $"({a.code} >= {b.code})"; break;
                case OperationType.LessThanOrEqualTo: condition = $"({a.code} <= {b.code})"; break;
                case OperationType.EqualTo:
                case OperationType.NotEqualTo:
                    if (IsIntegral(p.tc))
                    {
                        condition = op == OperationType.EqualTo ? $"({a.code} == {b.code})" : $"({a.code} != {b.code})";
                    }
                    else
                    {
                        var eq = $"{Rt}.Eq({TypedArg(a)}, {TypedArg(b)})"; // REAL: epsilon; DFLOAT: always 0
                        condition = op == OperationType.EqualTo ? $"({eq} > 0)" : $"({eq} <= 0)";
                    }
                    break;
                default:
                    throw new CSharpTranspileException("not a comparison " + op);
            }
            return new Ex($"({condition} ? 1 : 0)", FT.Int, pure) { condition = condition };
        }

        Ex Not(Ex rhs)
        {
            string code;
            switch (rhs.type.tc)
            {
                case TypeCodes.BYTE: code = $"(byte)({rhs.code} > 0 ? 0 : 1)"; break;
                case TypeCodes.WORD: code = $"(ushort)(((short)({rhs.code})) > 0 ? 0 : 1)"; break;
                case TypeCodes.INT:
                    var falsy = rhs.condition != null ? $"!{rhs.condition}" : $"({rhs.code} <= 0)";
                    return new Ex($"({falsy} ? 1 : 0)", FT.Int, rhs.pure) { condition = $"({falsy})" };
                case TypeCodes.REAL: code = $"({rhs.code} > 0 ? 0f : 1f)"; break;
                default:
                    code = $"{Rt}.BadOp<{CsType(rhs.type)}, {CsType(rhs.type)}>({rhs.code}, \"not\", {rhs.type.tc})";
                    break;
            }
            return new Ex(code, rhs.type, rhs.pure);
        }

        Ex BitwiseNot(Ex rhs)
        {
            if (rhs.type.tc == TypeCodes.INT) return new Ex($"(~{rhs.code})", FT.Int, rhs.pure);
            return new Ex($"{Rt}.BadOp<{CsType(rhs.type)}, int>({rhs.code}, \"Bitwise NOT\", {rhs.type.tc})", FT.Int, false);
        }

        Ex Bitwise(OperationType op, Ex lhs, Ex rhs)
        {
            var p = Promote(lhs.type, rhs.type);
            var a = Cast(lhs, p);
            var b = Cast(rhs, p);
            var pure = a.pure && b.pure;
            string code = null;
            switch (op)
            {
                case OperationType.Bitwise_And:
                    if (p.tc == TypeCodes.INT) code = $"({a.code} & {b.code})";
                    break;
                case OperationType.Bitwise_Or:
                    if (p.tc == TypeCodes.INT) code = $"({a.code} | {b.code})";
                    break;
                case OperationType.Bitwise_Xor:
                    if (p.tc == TypeCodes.INT) code = $"({a.code} ^ {b.code})";
                    else if (p.tc == TypeCodes.BYTE) code = $"((byte)({a.code} ^ {b.code}))";
                    break;
                case OperationType.Bitwise_LeftShift:
                    if (p.tc == TypeCodes.INT) code = $"({a.code} << {b.code})";
                    break;
                case OperationType.Bitwise_RightShift:
                    if (p.tc == TypeCodes.INT) code = $"({a.code} >> {b.code})";
                    break;
            }
            if (code == null)
            {
                return new Ex(BadOp(a, b, "bitwise", p), FT.Int, false);
            }
            return new Ex(code, p, pure);
        }

        Ex EmitBinary(BinaryOperandExpression op)
        {
            switch (op.operationType)
            {
                case OperationType.And:
                case OperationType.Or:
                    return EmitShortCircuit(op);
                case OperationType.Bitwise_Not:
                {
                    // DarkBasic parity: a right-hand side is parsed but never compiled
                    var lhsOnly = EmitExpression(op.lhs);
                    return Settle(BitwiseNot(lhsOnly));
                }
            }

            var lhs = EmitExpression(op.lhs);
            var rhs = EmitExpression(op.rhs);
            switch (op.operationType)
            {
                case OperationType.Add:
                case OperationType.Mult:
                case OperationType.Divide:
                case OperationType.Mod:
                case OperationType.RaisePower:
                    return Settle(Arithmetic(op.operationType, lhs, rhs));
                case OperationType.Subtract:
                {
                    // a - b compiles as a + (b * -1)
                    var negated = Settle(Arithmetic(OperationType.Mult, rhs, new Ex("-1", FT.Int, true)));
                    return Settle(Arithmetic(OperationType.Add, lhs, negated));
                }
                case OperationType.GreaterThan:
                case OperationType.LessThan:
                case OperationType.GreaterThanOrEqualTo:
                case OperationType.LessThanOrEqualTo:
                case OperationType.EqualTo:
                case OperationType.NotEqualTo:
                    return Settle(Compare(op.operationType, lhs, rhs));
                case OperationType.Xor:
                    return Settle(new Ex($"(({Logical(lhs)} ^ {Logical(rhs)}) >= 1 ? 1 : 0)", FT.Int, lhs.pure && rhs.pure));
                case OperationType.Bitwise_And:
                case OperationType.Bitwise_Or:
                case OperationType.Bitwise_Xor:
                case OperationType.Bitwise_LeftShift:
                case OperationType.Bitwise_RightShift:
                    return Settle(Bitwise(op.operationType, lhs, rhs));
                default:
                    throw new CSharpTranspileException("unknown operation " + op.operationType, op);
            }
        }

        /// <summary>
        /// `a or b` / `a and b`. The VM keeps the left value on the stack when it short-circuits,
        /// so `5 or x` is 5, not 1; otherwise the result is a normalized INT 0/1.
        /// OR:  castInt(a) &gt; 0 ? a : (castInt(b) &gt; 0 ? 1 : 0)
        /// AND: castInt(a) == 0 ? a : (castInt(a) &gt; 0 &amp;&amp; castInt(b) &gt; 0 ? 1 : 0)
        /// </summary>
        Ex EmitShortCircuit(BinaryOperandExpression op)
        {
            var isOr = op.operationType == OperationType.Or;
            var lhs = EmitExpression(op.lhs);
            var resultType = lhs.type;
            if (!IsNumeric(resultType.tc)) resultType = FT.Int;

            if (_anf)
            {
                var left = Settle(lhs);
                var result = NewTemp(resultType);
                var done = NewLabel("SC");
                var leftInt = Cast(left, FT.Int).code;
                if (isOr)
                {
                    Line($"if ({leftInt} > 0) {{ {result} = {Cast(left, resultType).code}; goto {done}; }}");
                }
                else
                {
                    Line($"if ({leftInt} == 0) {{ {result} = {Cast(left, resultType).code}; goto {done}; }}");
                }
                var rhs = EmitExpression(op.rhs);
                var normalized = isOr
                    ? $"({Truth(rhs)} ? 1 : 0)"
                    : $"(({leftInt} > 0 && {Truth(rhs)}) ? 1 : 0)";
                Line($"{result} = {Cast(new Ex(normalized, FT.Int, true), resultType).code};");
                Label(done);
                return new Ex(result, resultType, true);
            }

            var pv = $"__p{++_patternCounter}";
            // its truthiness: the kept left value is truthy exactly when CAST INT of it is, so
            // `a or b` reads as `Truth(a) || Truth(b)`, `a and b` as `Truth(a) && Truth(b)`
            var leftTruth = IsNumeric(lhs.type.tc) ? Truth(lhs) : null;
            var pvEx = new Ex(pv, lhs.type, true);
            var pvInt = Cast(pvEx, FT.Int).code;
            var rhsEx = EmitExpression(op.rhs);
            var keep = Cast(pvEx, resultType).code;
            string code;
            if (isOr)
            {
                var other = Cast(new Ex($"({Truth(rhsEx)} ? 1 : 0)", FT.Int, true), resultType).code;
                code = $"({lhs.code} is {CsType(lhs.type)} {pv} ? ({pvInt} > 0 ? {keep} : {other}) : default({CsType(resultType)}))";
            }
            else
            {
                var other = Cast(new Ex($"(({pvInt} > 0 && {Truth(rhsEx)}) ? 1 : 0)", FT.Int, true), resultType).code;
                code = $"({lhs.code} is {CsType(lhs.type)} {pv} ? ({pvInt} == 0 ? {keep} : {other}) : default({CsType(resultType)}))";
            }
            return new Ex(code, resultType, lhs.pure && rhsEx.pure)
            {
                condition = leftTruth == null ? null : $"({leftTruth} {(isOr ? "||" : "&&")} {Truth(rhsEx)})"
            };
        }
    }
}
