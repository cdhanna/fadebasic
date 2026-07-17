using System.Collections.Generic;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using FadeBasic.Virtual.HotReload;

namespace Tests.HotReload;

/// <summary>
/// Headless driver for hot-reload tests. Compiles Fade source through the real
/// Lexer/Parser/Compiler, builds a VirtualMachine, and drives reload flows.
/// Reuses the same pipeline the rest of the test suite uses (see TokenVm.Setup).
/// </summary>
public sealed class HotReloadHarness
{
    public Compiler Compiler;
    public ProgramFacts Facts;
    public VirtualMachine Vm;
    public ProgramNode Ast;

    public static CommandCollection Commands => TestCommands.CommandsForTesting;

    public static Compiler CompileToCompiler(string src, out ProgramNode ast, bool generateDebug = true)
    {
        var collection = Commands;
        var lexer = new Lexer();
        var tokens = lexer.Tokenize(src, collection);
        var parser = new Parser(new TokenStream(tokens), collection);
        ast = parser.ParseProgram();
        ast.AssertNoParseErrors();
        var compiler = new Compiler(collection, new CompilerOptions { GenerateDebugData = generateDebug });
        compiler.Compile(ast);
        return compiler;
    }

    public static ProgramFacts FactsFor(string src)
    {
        var compiler = CompileToCompiler(src, out _);
        return ProgramFacts.FromCompiler(compiler);
    }

    public static EditSet DiffSources(string oldSrc, string newSrc, StructuralDiffOptions options = null)
        => StructuralDiff.Diff(FactsFor(oldSrc), FactsFor(newSrc), options);

    public static HotReloadHarness Start(string src)
    {
        TestCommands.staticPrintBuffer.Clear();
        var h = new HotReloadHarness();
        h.Compiler = CompileToCompiler(src, out h.Ast);
        h.Facts = ProgramFacts.FromCompiler(h.Compiler);
        h.Vm = new VirtualMachine(h.Compiler.Program) { hostMethods = h.Compiler.methodTable };
        return h;
    }

    public void RunToCompletion() => Vm.Execute2(0);

    /// <summary>Adopt a new program's facts (after a migrate) so inspectors read by the new layout.</summary>
    public void AdoptFacts(ProgramFacts f) => Facts = f;

    public bool IsControlSafe(string newSrc)
        => ActiveSetAnalysis.IsControlSafe(Vm, Facts, FactsFor(newSrc));

    public ReconcilePlan Classify(string newSrc)
    {
        var newFacts = FactsFor(newSrc);
        var edits = StructuralDiff.Diff(Facts, newFacts, new StructuralDiffOptions { DetectRenames = true });
        return ReconcileClassifier.Classify(Vm, Facts, newFacts, edits);
    }

    public HotReloadSession NewSession(bool migrateHeap = true)
        => new HotReloadSession(Vm, Facts, s => CompileToCompiler(s, out _), migrateHeap);

    /// <summary>
    /// Attempt a Tier-A reload right now: if control-safe, remap globals, swap
    /// bytecode, remap the PC, and adopt the new facts. Returns false (and does
    /// nothing) if the active code changed under us. Heap struct migration is
    /// applied when <paramref name="migrateHeap"/> is set (Phase 6).
    /// </summary>
    public bool TryApplyNow(string newSrc, bool migrateHeap = false)
    {
        var oldFacts = Facts;
        var newCompiler = CompileToCompiler(newSrc, out _);
        var newFacts = ProgramFacts.FromCompiler(newCompiler);

        if (!ActiveSetAnalysis.IsControlSafe(Vm, oldFacts, newFacts)) return false;

        Migrator.RemapGlobals(Vm, oldFacts, newFacts);
        if (migrateHeap)
        {
            var edits = StructuralDiff.Diff(oldFacts, newFacts, new StructuralDiffOptions { DetectRenames = true });
            HeapMigrator.MigrateChangedTypes(Vm, edits);
        }
        Migrator.SwapProgram(Vm, newFacts);
        Migrator.RemapProgramCounter(Vm, oldFacts, newFacts);

        Compiler = newCompiler;
        AdoptFacts(newFacts);
        return true;
    }

    /// <summary>Run until the PC sits at the first statement on the given 1-based line.</summary>
    public bool RunToLine(int line)
    {
        var targets = StatementStartsOnLine(Facts, line);
        if (targets.Count == 0) return false;
        Vm.Execute2(0, ins => targets.Contains(ins));
        return targets.Contains(Vm.instructionIndex);
    }

    public static HashSet<int> StatementStartsOnLine(ProgramFacts facts, int line)
    {
        var set = new HashSet<int>();
        if (facts.Debug == null) return set;
        foreach (var t in facts.Debug.statementTokens)
            if (t.token != null && t.token.lineNumber == line) set.Add(t.insIndex);
        return set;
    }

    /// <summary>The start instruction index of the statement the PC currently sits in.</summary>
    public int CurrentStatementStart() => StatementStartForInstruction(Facts, Vm.instructionIndex);

    public static int StatementStartForInstruction(ProgramFacts facts, int ins)
    {
        int best = -1;
        if (facts.Debug == null) return best;
        foreach (var t in facts.Debug.statementTokens)
            if (t.insIndex <= ins && t.insIndex > best) best = t.insIndex;
        return best;
    }

    // ---- inspectors (globals) ----
    public ulong GlobalRaw(string name)
    {
        if (!Facts.Globals.TryGetValue(name, out var v))
            throw new KeyNotFoundException($"no global '{name}'");
        return Vm.globalScope.dataRegisters[v.registerAddress];
    }

    public int GlobalInt(string name) => VmUtil.ConvertToInt(GlobalRaw(name));
    public float GlobalFloat(string name) => VmUtil.ConvertToFloat(GlobalRaw(name));

    public bool HasGlobal(string name) => Facts.Globals.ContainsKey(name);

    // ---- inspectors (heap struct fields) ----
    public int StructFieldInt(string globalName, string fieldName)
    {
        var g = Facts.Globals[globalName];
        var type = Facts.TypesByName[g.structType];
        var member = type.fields[fieldName];
        var ptr = VmPtr.FromRaw(Vm.globalScope.dataRegisters[g.registerAddress]) + member.Offset;
        Vm.heap.Read(ptr, member.Length, out var bytes);
        return System.BitConverter.ToInt32(bytes, 0);
    }

    public int LiveInstances(string typeName)
        => ReconcileClassifier.LiveInstanceCount(Vm, Facts.TypesByName[typeName].typeId);

    public void CollectGarbage() => Vm.CollectGarbage();
}
