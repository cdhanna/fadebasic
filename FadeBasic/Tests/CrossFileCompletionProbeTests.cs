// Cross-file completion: top-level variables declared in an earlier file
// must show up in completions at a position in a later file. Everything the
// Playground sends the LSP is ONE joined document, so this is really "earlier
// in the joined doc". Regression coverage for the bug where a throw in scope
// resolution aborted the whole pass and dropped every later variable (see
// ScopeResolutionDoesNotAbortTests) — here proven end-to-end on the real
// killcode project (`backgroundColor` defined in entry.fbasic, completed at a
// top-level position in main.fbasic).

using System.IO;
using System.Linq;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lib.Standard;
using FadeBasic.LSP.Core;
using FadeBasic.LSP.Core.Handlers;
using NUnit.Framework;

namespace Tests
{
    [TestFixture]
    public class CrossFileCompletionProbeTests
    {
        private static FadeDocument BuildDoc(string source)
        {
            var workspace = new FadeWorkspace(TestCommands.CommandsForTesting);
            return workspace.SetDocument("test://probe.fbasic", source);
        }

        // A variable first introduced by being passed as a `ref` arg to a
        // command (`reserve text id(THE_ID)`) must complete with its ORIGINAL
        // casing, not the lowercased symbol-table key. Uses the faithful command
        // set so `reserve text id` resolves as a ref command (not an array ref).
        [Test]
        public void RefArgVariable_CompletesWithOriginalCasing()
        {
            var commands = new CommandCollection(new StandardCommands(), new ConsoleCommands(), new MonoStubs());
            var doc = new FadeWorkspace(commands).SetDocument("test://casing.fbasic",
                "reserve text id(THE_ID)\nx = ");
            var labels = CompletionHandler.Compute(doc, 1, 4).Select(i => i.Label).ToList();
            TestContext.WriteLine("labels: " + string.Join(", ", labels.Take(15)));
            Assert.Multiple(() =>
            {
                Assert.That(labels, Does.Contain("THE_ID"), "ref-arg variable should keep original casing");
                Assert.That(labels, Does.Not.Contain("the_id"), "should not appear lowercased");
            });
        }

        private static (FadeDocument doc, int line, int ch) DocAt(string sourceWithCursor)
        {
            var cursorIdx = sourceWithCursor.IndexOf('|');
            var source = sourceWithCursor.Remove(cursorIdx, 1);
            int line = 0, ch = 0;
            for (var i = 0; i < cursorIdx; i++)
            {
                if (source[i] == '\n') { line++; ch = 0; } else { ch++; }
            }
            return (BuildDoc(source), line, ch);
        }

        private static void DumpTable(string name, SymbolTable table)
        {
            if (table == null) { TestContext.WriteLine($"  {name}: <null>"); return; }
            TestContext.WriteLine($"  {name} ({table.Count}): {string.Join(", ", table.Keys)}");
        }

        [Test]
        public void Probe_TopLevelVarsAcrossFunctionBoundary()
        {
            // beforeFunc: top-level, declared BEFORE the function (like killcode's
            // entry.fbasic vars). afterFunc: top-level, declared AFTER. probeVar's
            // RHS is the completion site (like typing `x = ` in a later file).
            var src =
                "beforeFunc = 1\n" +
                "function f()\n" +
                "    localX = 5\n" +
                "endfunction\n" +
                "afterFunc = 2\n" +
                "probeVar = |";

            var (doc, line, ch) = DocAt(src);

            var scope = doc.Program.scope;
            TestContext.WriteLine("=== SCOPE TABLES ===");
            DumpTable("globalVariables", scope.globalVariables);
            DumpTable("allGlobalVariables", scope.allGlobalVariables);
            TestContext.WriteLine($"  localVariables stack depth: {scope.localVariables.Count}");
            int i = 0;
            foreach (var t in scope.localVariables) DumpTable($"localVariables[{i++}]", t);
            TestContext.WriteLine($"  positionedVariables entries: {scope.positionedVariables.entries.Count}");
            foreach (var e in scope.positionedVariables.entries)
                DumpTable($"    entry[{e.start?.lineNumber}..{e.end?.lineNumber}] fn={e.value.Item2}", e.value.Item1);

            // What does the completion's entry lookup resolve to at the cursor?
            if (scope.positionedVariables.TryFindEntry(line, ch, out var found))
                DumpTable($"RESOLVED LocalScope @({line},{ch})", found.value.Item1);
            else
                TestContext.WriteLine($"RESOLVED LocalScope @({line},{ch}): <none>");

            var items = CompletionHandler.Compute(doc, line, ch);
            var labels = items.Select(x => x.Label).ToList();
            TestContext.WriteLine("=== COMPLETION LABELS ===");
            TestContext.WriteLine("  " + string.Join(", ", labels));

            Assert.Multiple(() =>
            {
                Assert.That(labels, Does.Contain("afterFunc"), "afterFunc (declared AFTER the function) should complete");
                Assert.That(labels, Does.Contain("beforeFunc"), "beforeFunc (declared BEFORE the function) should complete");
            });
        }

        static string KillcodeDir()
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            for (var i = 0; i < 8 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, "Fixtures", "killcode", "code");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        [Test]
        public void Probe_Killcode_BackgroundColorInMain()
        {
            var dir = KillcodeDir();
            Assert.That(dir, Is.Not.Null, "killcode fixture not found");
            var order = new[] { "entry", "snippets", "assets", "postits", "setups", "animatics", "main", "menu", "countdown", "animatics_Intro_BinSkull" };

            // Join like the Playground's ProjectSourceMap.build: each file's
            // lines, dropping a trailing empty, each terminated with '\n'.
            // Insert a probe assignment (`zzprobe = <cursor>`) right after
            // main.fbasic's `_main:` label — a definitely-top-level position.
            const char CURSOR = '';
            var sb = new StringBuilder();
            foreach (var n in order)
            {
                var text = File.ReadAllText(Path.Combine(dir, n + ".fbasic"));
                if (n == "main")
                {
                    var idx = text.IndexOf("_main:", System.StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var nl = text.IndexOf('\n', idx);
                        if (nl >= 0) text = text.Substring(0, nl + 1) + "zzprobe = " + CURSOR + "\n" + text.Substring(nl + 1);
                    }
                }
                var lines = text.Split('\n').ToList();
                if (lines.Count > 0 && lines[lines.Count - 1] == "") lines.RemoveAt(lines.Count - 1);
                foreach (var l in lines) sb.Append(l).Append('\n');
            }
            var joined = sb.ToString();

            var cursorIdx = joined.IndexOf(CURSOR);
            Assert.That(cursorIdx, Is.GreaterThanOrEqualTo(0), "probe cursor not inserted");
            joined = joined.Remove(cursorIdx, 1);
            int line = 0, ch = 0;
            for (var i = 0; i < cursorIdx; i++) { if (joined[i] == '\n') { line++; ch = 0; } else { ch++; } }

            // FAITHFUL command set: StandardCommands + ConsoleCommands +
            // MonoStubs (mirrors the real MonoGame command signatures the
            // Playground uses). With these, killcode's `reserve text id(...)`
            // etc. parse as COMMAND CALLS — not array refs — so this reproduces
            // the Playground's actual parse, unlike TestCommands.CommandsForTesting.
            var commands = new CommandCollection(new StandardCommands(), new ConsoleCommands(), new MonoStubs());
            var doc = new FadeWorkspace(commands).SetDocument("test://killcode.fbasic", joined);
            var scope = doc.Program.scope;

            TestContext.WriteLine($"joined lines={joined.Count(c => c == '\n')}, probe at ({line},{ch})");
            TestContext.WriteLine($"diagnostics/parse errors: program has {CountErrors(doc.Program)} error nodes (sampled)");
            TestContext.WriteLine($"globalVariables ({scope.globalVariables.Count}) has backgroundColor={scope.globalVariables.ContainsKey("backgroundcolor")}");
            TestContext.WriteLine($"allGlobalVariables ({scope.allGlobalVariables.Count})");
            TestContext.WriteLine($"positionedVariables entries: {scope.positionedVariables.entries.Count}");

            if (scope.positionedVariables.TryFindEntry(line, ch, out var found))
            {
                var t = found.value.Item1;
                TestContext.WriteLine($"RESOLVED LocalScope @({line},{ch}) fn={found.value.Item2} count={t?.Count} range={found.start?.lineNumber}..{found.end?.lineNumber}");
                TestContext.WriteLine($"  has backgroundColor={t != null && t.ContainsKey("backgroundcolor")}, has loadingText={t != null && t.ContainsKey("loadingtext")}, has zzprobe={t != null && t.ContainsKey("zzprobe")}");
                if (t != null) TestContext.WriteLine("  first 40 keys: " + string.Join(", ", t.Keys.Take(40)));
            }
            else TestContext.WriteLine("RESOLVED LocalScope: <none>");

            var items = CompletionHandler.Compute(doc, line, ch);
            var labels = items.Select(x => x.Label).ToList();
            TestContext.WriteLine($"COMPLETION returned {labels.Count} items");
            TestContext.WriteLine("  var-ish labels (first 40): " + string.Join(", ", labels.Take(40)));

            Assert.Multiple(() =>
            {
                Assert.That(scope.positionedVariables.TryFindEntry(line, ch, out var e) && e.value.Item1 != null
                            && e.value.Item1.ContainsKey("backgroundcolor"),
                    Is.True, "backgroundColor (entry.fbasic top-level) must be in scope at a main.fbasic position");
                // Correct casing preserved, and completed cross-file.
                Assert.That(labels, Does.Contain("backgroundColor"),
                    "backgroundColor must appear in completions at a main.fbasic top-level position");
                Assert.That(labels, Does.Contain("loadingText"),
                    "loadingText must appear too (same cross-file case)");
            });
        }

        private static int CountErrors(ProgramNode program)
        {
            var n = 0;
            program.Visit(node => { if (node is IAstNode a && a.Errors != null && a.Errors.Count > 0) n += a.Errors.Count; });
            return n;
        }
    }
}
