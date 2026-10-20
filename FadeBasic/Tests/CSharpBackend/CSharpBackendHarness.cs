using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Compiled;
using FadeBasic.CSharp;
using FadeBasic.Virtual;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Tests.CSharpBackend;

/// <summary>
/// Runs a Fade program on the bytecode VM and through the C# backend, then compares what a
/// program can observe: console/print output, whether it raised a runtime error, and the final
/// value of every global-scope register. The VM is the reference; any difference is a backend bug
/// (or a VM quirk the backend deliberately does not copy, which the test must say).
/// </summary>
public static class CSharpBackendHarness
{
    public const int VmInstructionBudget = 20_000_000;

    public class Case
    {
        public string Name = "";
        public string Source = "";
        public CommandCollection Commands = TestCommands.CommandsForTesting;
        public long VmBudget = VmInstructionBudget;

        // filled by Prepare
        public string? SkipReason;
        public string? TranspileError;
        public string? CSharpSource;
        public string ClassName = "";
        public VmRun? Vm;
        public Type? CompiledType;
        public string? CSharpCompileError;
    }

    /// <summary>One test block's run.</summary>
    public class TestRun
    {
        public string Name = "";
        public string Output = "";
        public string? Failure;
        public string? Error;
        public int Suspensions;
        public bool Completed = true;
        public object? State; // VirtualMachine or CompiledFadeProgram, for register comparison
    }

    public class VmRun
    {
        public List<TestRun> Tests = new();
        public int Suspensions;
        public string Output = "";
        public string? Error;
        public bool Completed;
        public VirtualMachine? Machine;
        public Compiler? Compiler;
    }

    public class CompiledRun
    {
        public int Suspensions;
        public string Output = "";
        public string? Error;
        public CompiledFadeProgram? Program;
    }

    static int _counter;

    public static ProgramNode? Parse(string src, CommandCollection commands, out string? error)
    {
        error = null;
        var lexer = new Lexer();
        var lex = lexer.TokenizeWithErrors(src, commands);
        if (lex.tokenErrors.Count > 0)
        {
            error = "lex errors: " + string.Join("; ", lex.tokenErrors.Select(e => e.Display));
            return null;
        }
        var parser = new Parser(new TokenStream(lex.tokens), commands);
        var program = parser.ParseProgram();
        var errors = program.GetAllErrors();
        if (errors.Count > 0)
        {
            error = "parse errors: " + string.Join("; ", errors.Take(3).Select(e => e.Display));
            return null;
        }
        return program;
    }

    /// <summary>Parses, runs on the VM, and transpiles. Compilation of the C# is batched separately.</summary>
    public static void Prepare(Case c)
    {
        var vmAst = Parse(c.Source, c.Commands, out var parseError);
        if (vmAst == null)
        {
            c.SkipReason = parseError;
            return;
        }

        if (vmAst.tests.Count > 0)
        {
            // test blocks stay on the VM; the main program still must agree
        }

        c.Vm = RunVm(vmAst, c.Commands, c.VmBudget);
        if (!c.Vm.Completed)
        {
            c.SkipReason = "VM did not finish within the instruction budget (or suspended)";
            return;
        }

        var csAst = Parse(c.Source, c.Commands, out _)!;
        c.ClassName = "P" + Interlocked.Increment(ref _counter);
        var result = CSharpTranspiler.Transpile(csAst, c.Commands, new CSharpTranspileOptions
        {
            Namespace = "FadeCorpus." + c.ClassName,
            ClassName = c.ClassName
        });
        if (!result.Success)
        {
            c.TranspileError = string.Join("; ", result.Errors);
            return;
        }
        c.CSharpSource = result.Source;
    }

    public static VmRun RunVm(ProgramNode ast, CommandCollection commands, long budget = VmInstructionBudget)
    {
        var run = new VmRun();
        var compiler = new Compiler(commands, new CompilerOptions { GenerateDebugData = false });
        try
        {
            compiler.Compile(ast);
        }
        catch (Exception ex)
        {
            run.Error = "compile: " + ex.Message;
            run.Completed = true;
            return run;
        }
        run.Compiler = compiler;

        foreach (var entry in compiler.TestManifest)
        {
            if (entry.isAbstract) continue;
            var testVm = new VirtualMachine(compiler.Program.ToArray(), entry.entryPointAddress)
            {
                hostMethods = compiler.methodTable,
                isTestExecution = true
            };
            var test = new TestRun { Name = entry.name, State = testVm };
            test.Output = Capture(() =>
            {
                try
                {
                    long spent = 0;
                    while (testVm.instructionIndex < testVm.program.Length && spent < budget)
                    {
                        testVm.isSuspendRequested = false;
                        testVm.Execute2(10_000);
                        spent += 10_000;
                        if (testVm.isSuspendRequested) test.Suspensions++;
                    }
                    test.Completed = testVm.instructionIndex >= testVm.program.Length;
                }
                catch (Exception ex)
                {
                    test.Error = Describe(ex);
                }
            });
            if (testVm.assertionFailure != null)
            {
                test.Failure = testVm.assertionFailure.sourceText + " | " + testVm.assertionFailure.reason;
            }
            run.Tests.Add(test);
        }

        var vm = new VirtualMachine(compiler.Program) { hostMethods = compiler.methodTable };
        run.Machine = vm;
        run.Output = Capture(() =>
        {
            try
            {
                long spent = 0;
                while (vm.instructionIndex < vm.program.Length && spent < budget)
                {
                    vm.isSuspendRequested = false;
                    vm.Execute2(10_000);
                    spent += 10_000;
                    if (vm.isSuspendRequested) run.Suspensions++;
                }
                run.Completed = vm.instructionIndex >= vm.program.Length;
            }
            catch (Exception ex)
            {
                run.Error = Describe(ex);
                run.Completed = true;
            }
        });
        return run;
    }

    public static CompiledRun RunCompiled(Type type)
    {
        var run = new CompiledRun();
        var program = (CompiledFadeProgram)Activator.CreateInstance(type)!;
        run.Program = program;
        Exception? failure = null;
        var output = "";
        var thread = new Thread(() =>
        {
            output = Capture(() =>
            {
                try
                {
                    while (program.Run()) run.Suspensions++;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
        }, 64 * 1024 * 1024) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            run.Error = "TIMEOUT";
            return run;
        }
        run.Output = output;
        if (failure != null) run.Error = Describe(failure);
        return run;
    }

    public static TestRun RunCompiledTest(Type type, string name)
    {
        var test = new TestRun { Name = name };
        var program = (CompiledFadeProgram)Activator.CreateInstance(type)!;
        test.State = program;
        if (!program.StartTest(name))
        {
            test.Error = "no such test";
            return test;
        }
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            test.Output = Capture(() =>
            {
                try
                {
                    while (program.Run()) test.Suspensions++;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
        }, 64 * 1024 * 1024) { IsBackground = true };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            test.Error = "TIMEOUT";
            return test;
        }
        if (failure != null) test.Error = Describe(failure);
        if (program.AssertionFailure != null)
        {
            test.Failure = program.AssertionFailure.sourceText + " | " + program.AssertionFailure.reason;
        }
        return test;
    }

    static string Describe(Exception ex)
    {
        while (ex is TargetInvocationException tie && tie.InnerException != null) ex = tie.InnerException;
        if (ex is VirtualRuntimeException vre) return "runtime:" + vre.Error.type;
        return "exception:" + ex.GetType().Name;
    }

    static readonly object CaptureLock = new();

    static string Capture(Action action)
    {
        lock (CaptureLock)
        {
            var prev = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            TestCommands.staticPrintBuffer.Clear();
            try
            {
                action();
            }
            finally
            {
                Console.SetOut(prev);
            }
            var sb = new StringBuilder(writer.ToString().Replace("\r\n", "\n"));
            foreach (var s in TestCommands.staticPrintBuffer) sb.Append("[static]").Append(s).Append('\n');
            return sb.ToString();
        }
    }

    // ---- compilation ---------------------------------------------------------------------

    static List<MetadataReference>? _references;

    static List<MetadataReference> References()
    {
        if (_references != null) return _references;
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "").Split(Path.PathSeparator);
        foreach (var p in tpa.Where(p => p.Length > 0)) paths[Path.GetFileName(p)] = p;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic || string.IsNullOrEmpty(asm.Location)) continue;
            paths[Path.GetFileName(asm.Location)] = asm.Location;
        }
        paths[Path.GetFileName(typeof(VirtualMachine).Assembly.Location)] = typeof(VirtualMachine).Assembly.Location;
        paths[Path.GetFileName(typeof(TestCommands).Assembly.Location)] = typeof(TestCommands).Assembly.Location;
        _references = paths.Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        return _references;
    }

    /// <summary>
    /// Compiles every prepared case into one in-memory assembly. A case whose C# does not
    /// compile is dropped (and keeps its diagnostics) so the rest still run.
    /// </summary>
    public static void CompileAll(IEnumerable<Case> cases)
    {
        var pending = cases.Where(c => c.CSharpSource != null && c.SkipReason == null).ToList();
        while (pending.Count > 0)
        {
            var trees = pending.ToDictionary(
                c => c,
                c => CSharpSyntaxTree.ParseText(c.CSharpSource!, new CSharpParseOptions(LanguageVersion.CSharp11), path: c.ClassName + ".cs"));
            var compilation = CSharpCompilation.Create(
                "FadeCorpus" + Interlocked.Increment(ref _counter),
                trees.Values,
                References(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false));

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (emit.Success)
            {
                var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
                foreach (var c in pending)
                {
                    c.CompiledType = asm.GetType($"FadeCorpus.{c.ClassName}.{c.ClassName}");
                }
                return;
            }

            var broken = new HashSet<Case>();
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var c = pending.FirstOrDefault(p => trees[p] == d.Location.SourceTree);
                if (c == null) throw new Exception("C# compile failed outside a program: " + d);
                c.CSharpCompileError ??= d.ToString();
                broken.Add(c);
            }
            pending = pending.Where(p => !broken.Contains(p)).ToList();
        }
    }

    // ---- comparison ----------------------------------------------------------------------

    /// <summary>Returns null when the two runs agree, otherwise a description of the first difference.</summary>
    public static string? Compare(Case c, CompiledRun compiled)
    {
        var vm = c.Vm!;
        var diffs = new List<string>();
        if (vm.Error != compiled.Error)
        {
            diffs.Add($"error: vm=[{vm.Error ?? "none"}] cs=[{compiled.Error ?? "none"}]");
        }
        if (vm.Suspensions != compiled.Suspensions)
        {
            diffs.Add($"suspensions: vm={vm.Suspensions} cs={compiled.Suspensions}");
        }
        if (vm.Output != compiled.Output)
        {
            diffs.Add($"output:\n--- vm ---\n{vm.Output}\n--- cs ---\n{compiled.Output}");
        }

        // When a run stopped with an error the registers reflect a half-done program; still
        // compare them, since both backends stop at the same statement.
        if (vm.Machine != null && compiled.Program != null && compiled.Error != "TIMEOUT")
        {
            CompareRegisters(vm.Machine, compiled.Program, diffs, "");
        }

        foreach (var vmTest in vm.Tests)
        {
            if (!vmTest.Completed) continue;
            var csTest = RunCompiledTest(c.CompiledType!, vmTest.Name);
            if (vmTest.Failure != csTest.Failure)
                diffs.Add($"test {vmTest.Name} failure: vm=[{vmTest.Failure ?? "pass"}] cs=[{csTest.Failure ?? "pass"}]");
            if (vmTest.Error != csTest.Error)
                diffs.Add($"test {vmTest.Name} error: vm=[{vmTest.Error ?? "none"}] cs=[{csTest.Error ?? "none"}]");
            if (vmTest.Output != csTest.Output)
                diffs.Add($"test {vmTest.Name} output:\n--- vm ---\n{vmTest.Output}\n--- cs ---\n{csTest.Output}");
            if (vmTest.Suspensions != csTest.Suspensions)
                diffs.Add($"test {vmTest.Name} suspensions: vm={vmTest.Suspensions} cs={csTest.Suspensions}");
            if (csTest.Error != "TIMEOUT" && vmTest.State is VirtualMachine tvm && csTest.State is CompiledFadeProgram tcs)
            {
                CompareRegisters(tvm, tcs, diffs, $"test {vmTest.Name} ");
            }
        }

        return diffs.Count == 0 ? null : string.Join("\n", diffs);
    }

    static void CompareRegisters(VirtualMachine vm, CompiledFadeProgram program, List<string> diffs, string prefix)
    {
        foreach (var field in program.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!field.Name.StartsWith("g")) continue;
            var underscore = field.Name.IndexOf('_');
            if (underscore < 2 || !ulong.TryParse(field.Name.AsSpan(1, underscore - 1), out var reg)) continue;
            var csValue = FormatCompiled(field.GetValue(program), field.FieldType);
            var vmValue = FormatVm(vm, reg, field.FieldType);
            if (csValue != vmValue)
            {
                diffs.Add($"{prefix}register {field.Name}: vm=[{vmValue}] cs=[{csValue}]");
            }
        }
    }

    static string FormatCompiled(object? value, Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FadeArray<>))
        {
            if (value == null) return "null-array";
            var data = (Array)type.GetField("data")!.GetValue(value)!;
            var elem = type.GetGenericArguments()[0];
            var sb = new StringBuilder("[");
            foreach (var item in data) sb.Append(FormatCompiled(item, elem)).Append(',');
            return sb.Append(']').ToString();
        }
        if (type == typeof(string)) return "\"" + ((string?)value ?? "") + "\"";
        if (type == typeof(float)) return ((float)value!).ToString("R", CultureInfo.InvariantCulture);
        if (type == typeof(double)) return ((double)value!).ToString("R", CultureInfo.InvariantCulture);
        if (type.IsPrimitive) return Convert.ToString(value, CultureInfo.InvariantCulture)!;
        if (type.IsValueType)
        {
            var sb = new StringBuilder("{");
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                sb.Append(FormatCompiled(f.GetValue(value), f.FieldType)).Append(';');
            }
            return sb.Append('}').ToString();
        }
        return value?.ToString() ?? "null";
    }

    static string FormatVm(VirtualMachine vm, ulong reg, Type type)
    {
        var scope = vm.globalScope;
        if (reg >= (ulong)scope.dataRegisters.Length) return "no-register";
        var data = scope.dataRegisters[reg];
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FadeArray<>))
        {
            var elem = type.GetGenericArguments()[0];
            var ptr = VmPtr.FromRaw(data);
            if (data == 0 && !vm.heap.TryGetAllocationSize(ptr, out _)) return "null-array";
            if (!vm.heap.TryGetAllocationSize(ptr, out var size)) return "null-array";
            vm.heap.Read(ptr, size, out var bytes);
            var elemSize = SizeOf(elem);
            var sb = new StringBuilder("[");
            for (var off = 0; off + elemSize <= bytes.Length; off += elemSize)
            {
                sb.Append(FormatBytes(vm, bytes, off, elem)).Append(',');
            }
            return sb.Append(']').ToString();
        }
        // a register that was never stored still has type INT and value 0
        if (type == typeof(string)) return scope.typeRegisters[reg] == TypeCodes.STRING ? FormatString(vm, data) : "\"\"";
        if (type.IsValueType && !type.IsPrimitive)
        {
            var ptr = VmPtr.FromRaw(data);
            if (!vm.heap.TryGetAllocationSize(ptr, out var size)) return FormatCompiled(Activator.CreateInstance(type), type);
            vm.heap.Read(ptr, size, out var bytes);
            return FormatBytes(vm, bytes, 0, type);
        }
        return FormatBytes(vm, BitConverter.GetBytes(data), 0, type);
    }

    static string FormatString(VirtualMachine vm, ulong raw)
    {
        var ptr = VmPtr.FromRaw(raw);
        if (!vm.heap.TryGetAllocationSize(ptr, out var size)) return "\"\"";
        vm.heap.Read(ptr, size, out var bytes);
        return "\"" + VmConverter.ToString(bytes) + "\"";
    }

    static int SizeOf(Type t)
    {
        if (t == typeof(int) || t == typeof(float) || t == typeof(uint)) return 4;
        if (t == typeof(byte)) return 1;
        if (t == typeof(ushort)) return 2;
        if (t == typeof(long) || t == typeof(double) || t == typeof(string)) return 8;
        return t.GetFields(BindingFlags.Public | BindingFlags.Instance).Sum(f => SizeOf(f.FieldType));
    }

    static string FormatBytes(VirtualMachine vm, byte[] bytes, int offset, Type t)
    {
        if (t == typeof(int)) return BitConverter.ToInt32(bytes, offset).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(uint)) return BitConverter.ToUInt32(bytes, offset).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(float)) return BitConverter.ToSingle(bytes, offset).ToString("R", CultureInfo.InvariantCulture);
        if (t == typeof(double)) return BitConverter.ToDouble(bytes, offset).ToString("R", CultureInfo.InvariantCulture);
        if (t == typeof(long)) return BitConverter.ToInt64(bytes, offset).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(ushort)) return BitConverter.ToUInt16(bytes, offset).ToString(CultureInfo.InvariantCulture);
        if (t == typeof(byte)) return bytes[offset].ToString(CultureInfo.InvariantCulture);
        if (t == typeof(string)) return FormatString(vm, BitConverter.ToUInt64(bytes, offset));
        var sb = new StringBuilder("{");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sb.Append(FormatBytes(vm, bytes, offset, f.FieldType)).Append(';');
            offset += SizeOf(f.FieldType);
        }
        return sb.Append('}').ToString();
    }
}
