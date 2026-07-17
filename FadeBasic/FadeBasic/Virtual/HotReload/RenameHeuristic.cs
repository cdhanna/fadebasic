using System.Collections.Generic;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Headless (no-IDE) rename recovery. Given the leftover removed/added fields
    /// of a type after name-matching, pair them into renames by structural
    /// similarity. Conservative: only pairs when there is exactly one plausible
    /// counterpart (same type, same byte length) — ambiguity means "not a rename",
    /// which safely degrades to delete+add (new field defaults).
    ///
    /// Matched pairs are REMOVED from the removed/added lists and emitted as
    /// <see cref="FieldEditKind.Renamed"/> edits on the type edit.
    /// </summary>
    public static class RenameHeuristic
    {
        public static void PairFields(
            List<KeyValuePair<string, CompiledTypeMember>> removed,
            List<KeyValuePair<string, CompiledTypeMember>> added,
            TypeEdit te)
        {
            // Candidate = same TypeCode + same struct type name + same Length.
            // If exactly one added candidate matches a removed field (and vice
            // versa, 1:1), treat it as a rename.
            var pairedRemoved = new List<int>();
            var pairedAdded = new List<int>();

            for (var ri = 0; ri < removed.Count; ri++)
            {
                var r = removed[ri].Value;
                int onlyMatch = -1;
                int matchCount = 0;
                for (var ai = 0; ai < added.Count; ai++)
                {
                    if (pairedAdded.Contains(ai)) continue;
                    var a = added[ai].Value;
                    if (IsCandidate(r, a)) { matchCount++; onlyMatch = ai; }
                }
                if (matchCount != 1) continue;

                // reverse-check: this removed field must also be the unique
                // candidate for the added field, else it's ambiguous.
                var chosen = added[onlyMatch].Value;
                int reverseCount = 0;
                for (var ri2 = 0; ri2 < removed.Count; ri2++)
                {
                    if (pairedRemoved.Contains(ri2)) continue;
                    if (IsCandidate(removed[ri2].Value, chosen)) reverseCount++;
                }
                if (reverseCount != 1) continue;

                te.FieldEdits.Add(new FieldEdit
                {
                    Kind = FieldEditKind.Renamed,
                    OldName = removed[ri].Key,
                    Name = added[onlyMatch].Key,
                    Old = r,
                    New = chosen,
                    HasOld = true,
                    HasNew = true,
                });
                pairedRemoved.Add(ri);
                pairedAdded.Add(onlyMatch);
            }

            // strip paired entries (descending so indexes stay valid)
            pairedRemoved.Sort((x, y) => y - x);
            foreach (var i in pairedRemoved) removed.RemoveAt(i);
            pairedAdded.Sort((x, y) => y - x);
            foreach (var i in pairedAdded) added.RemoveAt(i);
        }

        static bool IsCandidate(CompiledTypeMember r, CompiledTypeMember a)
        {
            if (r.TypeCode != a.TypeCode) return false;
            if (r.Length != a.Length) return false;
            if (!string.Equals(r.Type?.typeName, a.Type?.typeName)) return false;
            return true;
        }
    }
}
