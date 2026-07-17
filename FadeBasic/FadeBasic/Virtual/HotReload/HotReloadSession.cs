using System;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// The deferred-commit state machine. A host arms the latest source; the
    /// session re-classifies against the live VM whenever <see cref="Tick"/> is
    /// called (drive it from a safepoint — e.g. the Execute3 breakpoint callback)
    /// and commits the moment the verdict is <see cref="Verdict.ApplicableNow"/>.
    ///
    /// - PendingTransient: stays armed; keep running and it will drain.
    /// - PermanentlyRude: stays armed but never applies; the host should offer restart.
    /// - Re-arming with newer source supersedes the pending target ("latest wins").
    ///
    /// The host supplies a compile delegate so core stays decoupled from the
    /// lexer/parser wiring (which needs the CommandCollection).
    /// </summary>
    public sealed class HotReloadSession
    {
        readonly VirtualMachine _vm;
        readonly Func<string, Compiler> _compile;
        readonly bool _migrateHeap;

        public ProgramFacts CurrentFacts { get; private set; }
        public string PendingSource { get; private set; }
        public bool HasPending => PendingSource != null;

        /// <summary>Fired when an armed edit is committed to the live VM.</summary>
        public event Action<ReconcilePlan> OnCommitted;
        /// <summary>Fired when the armed edit currently can't apply (transient or rude).</summary>
        public event Action<ReconcilePlan> OnBlocked;

        public HotReloadSession(VirtualMachine vm, ProgramFacts current, Func<string, Compiler> compile, bool migrateHeap = true)
        {
            _vm = vm;
            CurrentFacts = current;
            _compile = compile;
            _migrateHeap = migrateHeap;
        }

        /// <summary>Arm the latest source. Supersedes any previously-armed target.</summary>
        public void Arm(string newSource) => PendingSource = newSource;

        /// <summary>Discard the armed edit, leaving the VM untouched.</summary>
        public void Cancel() => PendingSource = null;

        /// <summary>Classify the pending edit against the current VM WITHOUT applying.</summary>
        public ReconcilePlan Poll()
        {
            if (!HasPending)
                return new ReconcilePlan { Verdict = Verdict.NoChange, Edits = new EditSet() };
            var newFacts = ProgramFacts.FromCompiler(_compile(PendingSource));
            var edits = StructuralDiff.Diff(CurrentFacts, newFacts, new StructuralDiffOptions { DetectRenames = true });
            return ReconcileClassifier.Classify(_vm, CurrentFacts, newFacts, edits);
        }

        /// <summary>
        /// Re-classify and, if applicable, apply + commit. Returns the plan. Call
        /// at safepoints. On commit, <see cref="CurrentFacts"/> advances and the
        /// pending edit clears.
        /// </summary>
        public ReconcilePlan Tick()
        {
            if (!HasPending)
                return new ReconcilePlan { Verdict = Verdict.NoChange, Edits = new EditSet() };

            var newCompiler = _compile(PendingSource);
            var newFacts = ProgramFacts.FromCompiler(newCompiler);
            var edits = StructuralDiff.Diff(CurrentFacts, newFacts, new StructuralDiffOptions { DetectRenames = true });
            var plan = ReconcileClassifier.Classify(_vm, CurrentFacts, newFacts, edits);

            if (plan.Verdict != Verdict.ApplicableNow)
            {
                OnBlocked?.Invoke(plan);
                return plan;
            }

            Apply(CurrentFacts, newFacts, edits);
            CurrentFacts = newFacts;
            PendingSource = null;
            OnCommitted?.Invoke(plan);
            return plan;
        }

        void Apply(ProgramFacts oldFacts, ProgramFacts newFacts, EditSet edits)
        {
            Migrator.RemapGlobals(_vm, oldFacts, newFacts);
            if (_migrateHeap) HeapMigrator.MigrateChangedTypes(_vm, edits);
            Migrator.SwapProgram(_vm, newFacts);
            Migrator.RemapProgramCounter(_vm, oldFacts, newFacts);
        }
    }
}
