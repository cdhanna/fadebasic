using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// Turns an <see cref="EditSet"/> + the live VM state into a <see cref="ReconcilePlan"/>.
    /// Two gates:
    ///  - DATA gate (not fixable by waiting): retyped live globals, struct growth
    ///    with live instances, transitive nested-type changes → PermanentlyRude.
    ///  - CONTROL gate (fixable by waiting): an active statement changed → PendingTransient.
    /// Passing both → ApplicableNow.
    /// </summary>
    public static class ReconcileClassifier
    {
        public static ReconcilePlan Classify(VirtualMachine vm, ProgramFacts oldFacts, ProgramFacts newFacts, EditSet edits)
        {
            var plan = new ReconcilePlan { Edits = edits };

            if (edits.IsEmpty)
            {
                plan.Verdict = Verdict.NoChange;
                return plan;
            }

            var rude = DetectPermanentlyRude(vm, edits);
            if (rude != null)
            {
                plan.Verdict = Verdict.PermanentlyRude;
                plan.RudeReason = rude;
                return plan;
            }

            if (ActiveSetAnalysis.IsControlSafe(vm, oldFacts, newFacts))
            {
                plan.Verdict = Verdict.ApplicableNow;
                return plan;
            }

            plan.Verdict = Verdict.PendingTransient;
            plan.BlockingStatements = ActiveSetAnalysis.BlockingStatements(vm, oldFacts, newFacts);
            return plan;
        }

        static string DetectPermanentlyRude(VirtualMachine vm, EditSet edits)
        {
            // 1. A retyped global that already holds a value can't be reinterpreted.
            var retyped = edits.VariableEdits.FirstOrDefault(e => e.Kind == VarEditKind.Retyped);
            if (retyped != null)
                return $"global '{retyped.Name}' changed type ({retyped.Old?.typeCode} -> {retyped.New?.typeCode})";

            // 2. Struct layout changes that in-place migration can't handle when
            //    instances are live: growth, or a transitive nested-type change.
            var changedTypeNames = new HashSet<string>(edits.LayoutChangedTypes.Select(t => t.TypeName));
            foreach (var te in edits.LayoutChangedTypes)
            {
                if (te.Added) continue; // brand-new type, no instances

                int liveCount = te.Old != null ? LiveInstanceCount(vm, te.Old.typeId) : 0;
                if (liveCount == 0) continue; // no instances → nothing to migrate → fine

                if (te.Removed)
                    continue; // instances become unreachable garbage; not our problem to migrate

                // growth needs relocation + pointer fixup (deferred)
                if (te.New != null && te.Old != null && te.New.byteSize > te.Old.byteSize)
                    return $"type '{te.TypeName}' grew ({te.Old.byteSize} -> {te.New.byteSize} bytes) with {liveCount} live instance(s); relocation not yet supported";

                // transitive: a field whose type is itself a changed struct
                foreach (var fe in te.FieldEdits)
                {
                    var nested = fe.New.Type ?? fe.Old.Type;
                    if (fe.HasNew && fe.New.TypeCode == TypeCodes.STRUCT && nested != null && changedTypeNames.Contains(nested.typeName))
                        return $"type '{te.TypeName}' contains changed struct field of type '{nested.typeName}'; transitive migration not yet supported";
                    if (fe.HasOld && fe.Old.TypeCode == TypeCodes.STRUCT && nested != null && changedTypeNames.Contains(nested.typeName))
                        return $"type '{te.TypeName}' contains changed struct field of type '{nested.typeName}'; transitive migration not yet supported";
                }
            }

            return null;
        }

        public static int LiveInstanceCount(VirtualMachine vm, int typeId)
        {
            int count = 0;
            foreach (var a in vm.heap.SnapshotAllocations())
                if (a.format.IsStruct() && a.format.typeId == typeId) count++;
            return count;
        }
    }
}
