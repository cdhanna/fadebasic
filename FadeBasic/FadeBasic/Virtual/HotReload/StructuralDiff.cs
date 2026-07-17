using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Pure diff of two <see cref="ProgramFacts"/> → <see cref="EditSet"/>. No VM.
    /// Matches everything by NAME (register/offset addresses are unstable across
    /// recompiles, so name-keying is mandatory).
    /// </summary>
    public static class StructuralDiff
    {
        public static EditSet Diff(ProgramFacts oldSide, ProgramFacts newSide, StructuralDiffOptions options = null)
        {
            options ??= StructuralDiffOptions.Default;
            var edits = new EditSet();

            DiffGlobals(oldSide, newSide, edits);
            DiffTypes(oldSide, newSide, edits, options);
            DiffStatements(oldSide, newSide, edits);
            DiffFunctions(oldSide, newSide, edits);

            return edits;
        }

        static void DiffGlobals(ProgramFacts o, ProgramFacts n, EditSet edits)
        {
            foreach (var kvp in o.Globals)
            {
                if (!n.Globals.TryGetValue(kvp.Key, out var nv))
                {
                    edits.VariableEdits.Add(new VariableEdit { Kind = VarEditKind.Removed, Name = kvp.Key, Old = kvp.Value });
                    continue;
                }
                var ov = kvp.Value;
                if (ov.typeCode != nv.typeCode || !string.Equals(ov.structType, nv.structType))
                {
                    edits.VariableEdits.Add(new VariableEdit { Kind = VarEditKind.Retyped, Name = kvp.Key, Old = ov, New = nv });
                }
                else if (ov.registerAddress != nv.registerAddress)
                {
                    edits.VariableEdits.Add(new VariableEdit { Kind = VarEditKind.Reordered, Name = kvp.Key, Old = ov, New = nv });
                }
            }
            foreach (var kvp in n.Globals)
            {
                if (!o.Globals.ContainsKey(kvp.Key))
                    edits.VariableEdits.Add(new VariableEdit { Kind = VarEditKind.Added, Name = kvp.Key, New = kvp.Value });
            }
        }

        static void DiffTypes(ProgramFacts o, ProgramFacts n, EditSet edits, StructuralDiffOptions options)
        {
            foreach (var kvp in o.TypesByName)
            {
                if (!n.TypesByName.ContainsKey(kvp.Key))
                    edits.TypeEdits.Add(new TypeEdit { TypeName = kvp.Key, Removed = true, Old = kvp.Value });
            }
            foreach (var kvp in n.TypesByName)
            {
                if (!o.TypesByName.TryGetValue(kvp.Key, out var oldType))
                {
                    edits.TypeEdits.Add(new TypeEdit { TypeName = kvp.Key, Added = true, New = kvp.Value });
                    continue;
                }
                var te = DiffOneType(oldType, kvp.Value, options);
                if (te.HasLayoutChange) edits.TypeEdits.Add(te);
            }
        }

        static TypeEdit DiffOneType(CompiledType oldType, CompiledType newType, StructuralDiffOptions options)
        {
            var te = new TypeEdit { TypeName = newType.typeName, Old = oldType, New = newType };
            var removed = new List<KeyValuePair<string, CompiledTypeMember>>();
            var added = new List<KeyValuePair<string, CompiledTypeMember>>();

            foreach (var f in oldType.fields)
            {
                if (!newType.fields.TryGetValue(f.Key, out var nf))
                {
                    removed.Add(f);
                    continue;
                }
                var of = f.Value;
                if (of.TypeCode != nf.TypeCode ||
                    !string.Equals(of.Type?.typeName, nf.Type?.typeName))
                {
                    te.FieldEdits.Add(new FieldEdit { Kind = FieldEditKind.Retyped, Name = f.Key, Old = of, New = nf, HasOld = true, HasNew = true });
                }
                else if (of.Offset != nf.Offset || of.Length != nf.Length)
                {
                    te.FieldEdits.Add(new FieldEdit { Kind = FieldEditKind.Reordered, Name = f.Key, Old = of, New = nf, HasOld = true, HasNew = true });
                }
            }
            foreach (var f in newType.fields)
            {
                if (!oldType.fields.ContainsKey(f.Key)) added.Add(f);
            }

            // Optional rename recovery pairs leftover removed+added by similarity.
            if (options.DetectRenames && (removed.Count > 0 || added.Count > 0))
            {
                RenameHeuristic.PairFields(removed, added, te);
            }

            foreach (var r in removed)
                te.FieldEdits.Add(new FieldEdit { Kind = FieldEditKind.Removed, Name = r.Key, Old = r.Value, HasOld = true });
            foreach (var a in added)
                te.FieldEdits.Add(new FieldEdit { Kind = FieldEditKind.Added, Name = a.Key, New = a.Value, HasNew = true });

            return te;
        }

        // A statement is a (startInsIndex, lineNumber). Its byte range is
        // [startInsIndex, nextStatementStart). We match old->new statements by
        // (lineNumber, ordinal-within-line) and compare bytecode ranges. Changed
        // statements' OLD start indexes go into ChangedStatementInstructions (S).
        static void DiffStatements(ProgramFacts o, ProgramFacts n, EditSet edits)
        {
            if (o.Debug == null || n.Debug == null || o.Program == null || n.Program == null)
            {
                // Can't localize; fall back to coarse "did the code change at all".
                edits.CoarseBodyChanged = !BytesEqual(o.Program, n.Program);
                return;
            }

            var oldStmts = BuildStatements(o);
            var newStmts = BuildStatements(n);

            // group new statements by line for ordinal matching
            var newByLine = new Dictionary<int, List<Stmt>>();
            foreach (var s in newStmts)
            {
                if (!newByLine.TryGetValue(s.Line, out var list)) newByLine[s.Line] = list = new List<Stmt>();
                list.Add(s);
            }
            var lineCursor = new Dictionary<int, int>();

            foreach (var os in oldStmts)
            {
                Stmt match = null;
                if (newByLine.TryGetValue(os.Line, out var candidates))
                {
                    var idx = lineCursor.TryGetValue(os.Line, out var c) ? c : 0;
                    if (idx < candidates.Count) { match = candidates[idx]; lineCursor[os.Line] = idx + 1; }
                }
                if (match == null)
                {
                    edits.ChangedStatementInstructions.Add(os.Start); // line gone/shifted
                    continue;
                }
                if (!RangeEquals(o.Program, os.Start, os.End, n.Program, match.Start, match.End))
                    edits.ChangedStatementInstructions.Add(os.Start);
            }

            if (edits.ChangedStatementInstructions.Count == 0 && !BytesEqual(o.Program, n.Program))
                edits.CoarseBodyChanged = true; // change we couldn't localize to a statement
        }

        sealed class Stmt { public int Start; public int End; public int Line; }

        static List<Stmt> BuildStatements(ProgramFacts f)
        {
            var starts = f.Debug.statementTokens
                .Where(t => t.token != null)
                .Select(t => new { t.insIndex, t.token.lineNumber })
                .GroupBy(x => x.insIndex).Select(g => g.First())
                .OrderBy(x => x.insIndex).ToList();

            int codeEnd = f.Program.Length >= 4 ? BitConverter.ToInt32(f.Program, 0) : f.Program.Length;
            if (codeEnd <= 0 || codeEnd > f.Program.Length) codeEnd = f.Program.Length;

            var list = new List<Stmt>(starts.Count);
            for (var i = 0; i < starts.Count; i++)
            {
                int start = starts[i].insIndex;
                int end = (i + 1 < starts.Count) ? starts[i + 1].insIndex : codeEnd;
                if (end < start) end = start;
                list.Add(new Stmt { Start = start, End = end, Line = starts[i].lineNumber });
            }
            return list;
        }

        static void DiffFunctions(ProgramFacts o, ProgramFacts n, EditSet edits)
        {
            var oldFns = FunctionNames(o);
            var newFns = FunctionNames(n);
            foreach (var name in oldFns)
                if (!newFns.Contains(name))
                    edits.FunctionEdits.Add(new FunctionEdit { Kind = FunctionEditKind.Removed, Name = name });
            foreach (var name in newFns)
                if (!oldFns.Contains(name))
                    edits.FunctionEdits.Add(new FunctionEdit { Kind = FunctionEditKind.Added, Name = name });
            // Body/signature changes surface via ChangedStatementInstructions within the
            // function's instruction range; the classifier handles those via S.
        }

        static HashSet<string> FunctionNames(ProgramFacts f)
        {
            var set = new HashSet<string>();
            if (f.Debug == null) return set;
            foreach (var kvp in f.Debug.insToFunction)
                if (kvp.Value?.token?.caseInsensitiveRaw != null) set.Add(kvp.Value.token.caseInsensitiveRaw);
                else if (kvp.Value?.token?.raw != null) set.Add(kvp.Value.token.raw);
            return set;
        }

        static bool RangeEquals(byte[] a, int aStart, int aEnd, byte[] b, int bStart, int bEnd)
        {
            int la = aEnd - aStart, lb = bEnd - bStart;
            if (la != lb) return false;
            for (var i = 0; i < la; i++)
                if (a[aStart + i] != b[bStart + i]) return false;
            return true;
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    public sealed class StructuralDiffOptions
    {
        public bool DetectRenames = false;
        public static readonly StructuralDiffOptions Default = new StructuralDiffOptions();
    }
}
