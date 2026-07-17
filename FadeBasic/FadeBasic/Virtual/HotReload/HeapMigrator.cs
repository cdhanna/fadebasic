using System;
using System.Collections.Generic;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Migrates live heap struct instances of changed types to the new field
    /// layout. In-place, shrink-or-equal only: each field's bytes are copied from
    /// its old offset to its new offset (matched by name, honoring renames);
    /// added fields default; removed fields drop. Growth and transitive nested
    /// changes are classified <see cref="Verdict.PermanentlyRude"/> upstream and
    /// never reach here.
    ///
    /// Must run BEFORE <see cref="Migrator.SwapProgram"/> so the reshaped bytes
    /// are then read back through the new type layout.
    /// </summary>
    public static class HeapMigrator
    {
        public static void MigrateChangedTypes(VirtualMachine vm, EditSet edits)
        {
            foreach (var te in edits.LayoutChangedTypes)
            {
                if (te.Added || te.Removed || te.Old == null || te.New == null) continue;
                if (te.New.byteSize > te.Old.byteSize) continue; // growth is rude; skip defensively
                MigrateOneType(vm, te);
            }
        }

        static void MigrateOneType(VirtualMachine vm, TypeEdit te)
        {
            // new field name -> source (old) field name, honoring renames
            var source = new Dictionary<string, string>();
            foreach (var kvp in te.New.fields) source[kvp.Key] = kvp.Key; // identity by default
            foreach (var fe in te.FieldEdits)
                if (fe.Kind == FieldEditKind.Renamed && fe.Name != null && fe.OldName != null)
                    source[fe.Name] = fe.OldName;

            int newSize = te.New.byteSize;

            foreach (var alloc in vm.heap.SnapshotAllocations())
            {
                if (!alloc.format.IsStruct()) continue;
                if (alloc.format.typeId != te.Old.typeId) continue;

                vm.heap.Read(alloc.ptr, alloc.length, out var oldBytes);
                var newBytes = new byte[Math.Max(newSize, 0)];

                foreach (var nf in te.New.fields)
                {
                    var newMember = nf.Value;
                    if (!source.TryGetValue(nf.Key, out var oldName)) continue;
                    if (!te.Old.fields.TryGetValue(oldName, out var oldMember)) continue;   // added → default
                    if (oldMember.TypeCode != newMember.TypeCode) continue;                 // retyped → default
                    int len = Math.Min(oldMember.Length, newMember.Length);
                    if (oldMember.Offset + len > oldBytes.Length) continue;
                    if (newMember.Offset + len > newBytes.Length) continue;
                    Array.Copy(oldBytes, oldMember.Offset, newBytes, newMember.Offset, len);
                }

                // in-place: newSize <= alloc.length (guaranteed by the growth guard)
                vm.heap.WriteSpan(alloc.ptr, Math.Min(newSize, alloc.length), newBytes);
            }
        }
    }
}
