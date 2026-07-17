using System.Collections.Generic;

namespace FadeBasic.Virtual.HotReload
{
    public enum Verdict
    {
        /// <summary>Nothing changed; reload is a no-op.</summary>
        NoChange,
        /// <summary>Safe to swap + migrate at the current safepoint.</summary>
        ApplicableNow,
        /// <summary>Data-compatible but the edited code is active; retry after it drains.</summary>
        PendingTransient,
        /// <summary>Incompatible data-layout change; waiting cannot help — restart required.</summary>
        PermanentlyRude,
    }

    /// <summary>
    /// The result of classifying an edit against the live VM: what to do, why,
    /// and the diff that produced it. Carries the blocking statements (for
    /// PendingTransient UI) and the rude reason (for PermanentlyRude UI).
    /// </summary>
    public sealed class ReconcilePlan
    {
        public Verdict Verdict;
        public EditSet Edits;

        /// <summary>Set for PermanentlyRude — why it can't apply live.</summary>
        public string RudeReason;

        /// <summary>Set for PendingTransient — old-program statement starts currently blocking.</summary>
        public List<int> BlockingStatements = new List<int>();

        public bool CanApply => Verdict == Verdict.ApplicableNow;
        public override string ToString() =>
            Verdict == Verdict.PermanentlyRude ? $"{Verdict}: {RudeReason}" : Verdict.ToString();
    }
}
