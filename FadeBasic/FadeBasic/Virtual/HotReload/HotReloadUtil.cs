using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Shared helpers for mapping between instruction indexes and source
    /// statements, using <see cref="DebugData.statementTokens"/>. A "statement"
    /// is identified by its start instruction index; statements are matched
    /// across program versions by (source line, ordinal-within-line).
    /// </summary>
    public static class HotReloadUtil
    {
        public struct Stmt { public int Start; public int Line; }

        public static List<Stmt> Statements(ProgramFacts facts)
        {
            var list = new List<Stmt>();
            if (facts.Debug == null) return list;
            var seen = new HashSet<int>();
            foreach (var t in facts.Debug.statementTokens.OrderBy(x => x.insIndex))
            {
                if (t.token == null) continue;
                if (!seen.Add(t.insIndex)) continue;
                list.Add(new Stmt { Start = t.insIndex, Line = t.token.lineNumber });
            }
            return list;
        }

        /// <summary>Largest statement start &lt;= <paramref name="ins"/>, or -1.</summary>
        public static int StatementStartForInstruction(ProgramFacts facts, int ins)
        {
            int best = -1;
            foreach (var s in Statements(facts))
                if (s.Start <= ins && s.Start > best) best = s.Start;
            return best;
        }

        public static bool TryGetStatementLineAndOrdinal(ProgramFacts facts, int stmtStart, out int line, out int ordinal)
        {
            line = 0; ordinal = 0;
            var stmts = Statements(facts);
            int foundLine = -1;
            foreach (var s in stmts)
                if (s.Start == stmtStart) { foundLine = s.Line; break; }
            if (foundLine < 0) return false;

            int ord = 0;
            foreach (var s in stmts.Where(x => x.Line == foundLine).OrderBy(x => x.Start))
            {
                if (s.Start == stmtStart) { line = foundLine; ordinal = ord; return true; }
                ord++;
            }
            return false;
        }

        public static bool TryGetStatementStartByLineOrdinal(ProgramFacts facts, int line, int ordinal, out int stmtStart)
        {
            stmtStart = -1;
            var onLine = Statements(facts).Where(x => x.Line == line).OrderBy(x => x.Start).ToList();
            if (ordinal < 0 || ordinal >= onLine.Count) return false;
            stmtStart = onLine[ordinal].Start;
            return true;
        }

        public static HashSet<int> StatementStartsOnLine(ProgramFacts facts, int line)
        {
            var set = new HashSet<int>();
            foreach (var s in Statements(facts))
                if (s.Line == line) set.Add(s.Start);
            return set;
        }

        /// <summary>End of the code region (start of the interned-data blob).</summary>
        public static int CodeEnd(ProgramFacts facts)
        {
            if (facts.Program == null || facts.Program.Length < 4) return facts.Program?.Length ?? 0;
            int e = System.BitConverter.ToInt32(facts.Program, 0);
            if (e <= 0 || e > facts.Program.Length) e = facts.Program.Length;
            return e;
        }

        /// <summary>Byte range [start, end) of the statement beginning at <paramref name="start"/>.</summary>
        public static bool TryGetStatementRange(ProgramFacts facts, int start, out int end)
        {
            end = start;
            var stmts = Statements(facts);
            int codeEnd = CodeEnd(facts);
            for (var i = 0; i < stmts.Count; i++)
            {
                if (stmts[i].Start != start) continue;
                end = (i + 1 < stmts.Count) ? stmts[i + 1].Start : codeEnd;
                if (end < start) end = start;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Is it safe to swap while <paramref name="ins"/> is an active location?
        /// - The statement must still exist in the new program (mappable).
        /// - If we sit exactly at the statement START (offset 0) we are re-entering
        ///   it fresh, so ANY edit to that statement is fine (we'll run the new one).
        /// - If we sit MID-statement (offset &gt; 0, e.g. a return address inside a
        ///   call) the statement's bytecode must be byte-identical, or the partially
        ///   executed state would continue into mismatched bytes.
        /// </summary>
        public static bool IsActiveLocationSafe(ProgramFacts oldFacts, ProgramFacts newFacts, int ins)
        {
            int start = StatementStartForInstruction(oldFacts, ins);
            if (start < 0) return false;
            if (!TryGetStatementLineAndOrdinal(oldFacts, start, out var line, out var ordinal)) return false;
            if (!TryGetStatementStartByLineOrdinal(newFacts, line, ordinal, out _)) return false;

            int offset = ins - start;
            if (offset == 0) return true; // re-entering at a statement boundary
            return IsStatementUnchanged(oldFacts, newFacts, start);
        }

        /// <summary>
        /// True if the statement beginning at <paramref name="oldStart"/> maps to a
        /// new statement (same line+ordinal) whose bytecode is byte-identical.
        /// This is the exact "is the active code unchanged?" test the control gate uses.
        /// </summary>
        public static bool IsStatementUnchanged(ProgramFacts oldFacts, ProgramFacts newFacts, int oldStart)
        {
            if (!TryGetStatementLineAndOrdinal(oldFacts, oldStart, out var line, out var ordinal)) return false;
            if (!TryGetStatementStartByLineOrdinal(newFacts, line, ordinal, out var newStart)) return false;
            if (!TryGetStatementRange(oldFacts, oldStart, out var oldEnd)) return false;
            if (!TryGetStatementRange(newFacts, newStart, out var newEnd)) return false;

            int la = oldEnd - oldStart, lb = newEnd - newStart;
            if (la != lb) return false;
            for (var i = 0; i < la; i++)
                if (oldFacts.Program[oldStart + i] != newFacts.Program[newStart + i]) return false;
            return true;
        }
    }
}
