using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.CSharp
{
    /// <summary>
    /// Gosub routines as methods. In the bytecode every gosub routine is just code in the main body
    /// reached through a jump, and the first version of this backend copied that: the whole main
    /// program, routines and all, was one iterator method. In Fish Fish Pop that method was 312 KB of
    /// IL, far past the size the .NET JIT optimizes, so most of the game's per-frame code ran
    /// unoptimized.
    ///
    /// A routine is moved into its own method when nothing can tell the difference:
    /// <list type="bullet">
    /// <item>its label is a top-level statement of the main program, defined once, and only ever
    ///   entered by `gosub` (no `goto` from outside, and the statement before it cannot fall
    ///   through into it);</item>
    /// <item>it ends at the first top-level `return` after the label;</item>
    /// <item>no `goto` crosses its boundary, and every routine it gosubs is extracted too.</item>
    /// </list>
    /// Its body is still compiled at its place in the main program, so registers are allocated in the
    /// VM compiler's order. A routine is a plain method unless it can suspend, calls a suspending
    /// function or routine, or holds a label a test runs to; then it is an iterator.
    /// </summary>
    public partial class CSharpTranspiler
    {
        class RoutineInfo
        {
            public string label;
            public int start;   // index of the label statement in the main program
            public int end;     // index of the top-level return that ends it
            public string csName;
            public bool iterator;
            public HashSet<string> gosubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        readonly Dictionary<string, RoutineInfo> _routines = new Dictionary<string, RoutineInfo>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<int, RoutineInfo> _routineAt = new Dictionary<int, RoutineInfo>();

        /// <summary>Why each gosub routine that stayed in the main body was not given its own method.</summary>
        public readonly List<string> InlineRoutineReasons = new List<string>();

        void KeepInline(string label, string why) => InlineRoutineReasons.Add($"{label}: {why}");

        void AnalyzeRoutines()
        {
            var statements = _program.statements;

            // where every main-program label is defined, and where every goto/gosub is (by top-level index)
            var labelDefs = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var gotos = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            var gosubs = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            void Add(Dictionary<string, List<int>> map, string key, int at)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>();
                list.Add(at);
            }
            for (var i = 0; i < statements.Count; i++)
            {
                if (statements[i] is FunctionStatement) continue;
                var index = i;
                statements[i].Visit(node =>
                {
                    switch (node)
                    {
                        case LabelDeclarationNode label: Add(labelDefs, label.label, index); break;
                        case GotoStatement g: Add(gotos, g.label, index); break;
                        case GoSubStatement g: Add(gosubs, g.label, index); break;
                    }
                });
            }

            // candidates
            for (var i = 0; i < statements.Count; i++)
            {
                if (!(statements[i] is LabelDeclarationNode labelNode)) continue;
                var name = labelNode.label;
                if (!gosubs.ContainsKey(name)) continue;
                if (labelDefs[name].Count != 1) { KeepInline(name, "the label is defined more than once"); continue; }

                // Nothing falls into it. Walking back, statements that hold no label anything jumps to
                // can't be reached (they follow a return/end/goto), so they can't fall through either:
                // `return` / `time# = 0` / `update_frame:` is still a clean entry.
                var p = i - 1;
                var fallsThrough = false;
                string fallReason = null;
                for (; p >= 0; p--)
                {
                    var prev = statements[p];
                    if (prev is ReturnStatement || prev is EndProgramStatement || prev is GotoStatement) break;
                    if (prev is CommentStatement || prev is FunctionStatement) continue;
                    var reachable = false;
                    prev.Visit(node =>
                    {
                        if (node is LabelDeclarationNode inner && IsJumpTarget(inner.label)) reachable = true;
                    });
                    if (reachable)
                    {
                        fallsThrough = true;
                        fallReason = $"code before it ({prev.GetType().Name}, line {prev.StartToken?.lineNumber + 1}) can be jumped to and falls through into it";
                        break;
                    }
                }
                if (p < 0) { fallsThrough = true; fallReason = "the program can run into it from the start"; }
                if (fallsThrough) { KeepInline(name, fallReason); continue; }

                bool IsJumpTarget(string label) =>
                    gotos.ContainsKey(label) || gosubs.ContainsKey(label) || _runtoTargetLabels.Contains(label);

                // it ends at the first top-level return
                var end = -1;
                for (var j = i + 1; j < statements.Count; j++)
                {
                    if (statements[j] is ReturnStatement) { end = j; break; }
                    if (statements[j] is FunctionStatement) break;
                }
                if (end < 0) { KeepInline(name, "no top-level `return` ends it"); continue; }

                bool Inside(int at) => at > i && at <= end;
                var clean = true;

                // labels defined in its body are only reached from inside it
                foreach (var kvp in labelDefs)
                {
                    if (!kvp.Value.Any(Inside) && !string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (kvp.Value.Count != 1) { KeepInline(name, $"label {kvp.Key} inside it is defined more than once"); clean = false; break; }
                    if (gotos.TryGetValue(kvp.Key, out var refs) && refs.Any(r => !Inside(r))) { KeepInline(name, $"label {kvp.Key} is the target of a goto from outside"); clean = false; break; }
                    if (!string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase)
                        && gosubs.ContainsKey(kvp.Key)) { KeepInline(name, $"another routine ({kvp.Key}) starts inside it"); clean = false; break; }
                }
                if (!clean) continue;

                // its gotos stay inside it
                foreach (var kvp in gotos)
                {
                    if (!kvp.Value.Any(Inside)) continue;
                    if (!labelDefs.TryGetValue(kvp.Key, out var defs) || !defs.All(d => Inside(d) || d == i)) { KeepInline(name, $"it has a goto to {kvp.Key}, outside it"); clean = false; break; }
                }
                if (!clean) continue;

                var info = new RoutineInfo
                {
                    label = name,
                    start = i,
                    end = end,
                    csName = "G_" + Sanitize(name)
                };
                foreach (var kvp in gosubs)
                {
                    if (kvp.Value.Any(Inside)) info.gosubs.Add(kvp.Key);
                }
                _routines[name] = info;
            }

            // every routine it gosubs must be extracted too
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var r in _routines.Values.ToList())
                {
                    if (r.gosubs.All(g => _routines.ContainsKey(g))) continue;
                    KeepInline(r.label, "it gosubs a routine that stays inline (" + string.Join(", ", r.gosubs.Where(g => !_routines.ContainsKey(g))) + ")");
                    _routines.Remove(r.label);
                    changed = true;
                }
            }

            // which are iterators
            foreach (var r in _routines.Values)
            {
                if (_runtoTargetLabels.Contains(r.label)) r.iterator = true;
                for (var j = r.start + 1; j <= r.end && !r.iterator; j++)
                {
                    statements[j].Visit(node =>
                    {
                        switch (node)
                        {
                            case CommandStatement cs when IsSuspending(cs.command):
                            case CommandExpression ce when IsSuspending(ce.command):
                                r.iterator = true;
                                break;
                            case LabelDeclarationNode label when _runtoTargetLabels.Contains(label.label):
                                r.iterator = true;
                                break;
                            case ArrayIndexReference air when _functions.TryGetValue(air.variableName, out var fn) && fn.iterator:
                                r.iterator = true;
                                break;
                        }
                    });
                }
            }
            changed = true;
            while (changed)
            {
                changed = false;
                foreach (var r in _routines.Values)
                {
                    if (r.iterator) continue;
                    if (r.gosubs.Any(g => _routines[g].iterator))
                    {
                        r.iterator = true;
                        changed = true;
                    }
                }
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in _routines.Values.OrderBy(x => x.start))
            {
                var csName = r.csName;
                for (var n = 2; !used.Add(csName); n++) csName = r.csName + "_" + n;
                r.csName = csName;
                _routineAt[r.start] = r;
            }
        }

        /// <summary>Compiles the routine starting at its label into its own method; returns where main resumes.</summary>
        int CompileRoutine(RoutineInfo routine)
        {
            var statements = _program.statements;
            var region = new Region
            {
                name = routine.csName,
                kind = RegionKind.Routine,
                isIterator = routine.iterator,
                signature = (routine.iterator ? "IEnumerator " : "void ") + routine.csName + "()"
            };
            region.structured = CanStructure(region.kind, statements.Skip(routine.start + 1).Take(routine.end - routine.start - 1));
            var previous = _region;
            _region = region;
            try
            {
                // the label itself: a goto back to the top of the routine, and a runto stop
                CompileStatement(statements[routine.start]);
                for (var j = routine.start + 1; j < routine.end; j++)
                {
                    CompileStatement(statements[j]);
                }
                // the closing `return`
                _region.currentLine = statements[routine.end].StartToken?.lineNumber ?? _region.currentLine;
                LineDirective(statements[routine.end].StartToken);
                Line(routine.iterator ? "yield break;" : "return;");
                FinishRegion(region);
                foreach (var label in region.labelDefs.Keys) _routineLabelNames.Add(label);
            }
            finally
            {
                _region = previous;
            }
            _extraRegions.Add(region);
            return routine.end + 1;
        }

        readonly HashSet<string> _routineLabelNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A gosub to an extracted routine is a call.</summary>
        bool TryCompileRoutineCall(GoSubStatement goSub)
        {
            if (!(_region.kind == RegionKind.Main || _region.kind == RegionKind.Routine)) return false;
            if (!_routines.TryGetValue(goSub.label, out var routine)) return false;
            if (routine.iterator)
            {
                if (!_region.isIterator)
                {
                    throw new CSharpTranspileException($"internal: suspending routine {routine.label} gosubbed from a plain method", goSub);
                }
                Line($"yield return {routine.csName}();");
            }
            else
            {
                Line($"{routine.csName}();");
            }
            return true;
        }
    }
}
