using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual.HotReload
{
    public enum VarEditKind { Added, Removed, Reordered, Retyped }
    public enum FieldEditKind { Added, Removed, Reordered, Retyped, Renamed }

    /// <summary>A change to a single global variable, matched by name.</summary>
    public sealed class VariableEdit
    {
        public VarEditKind Kind;
        public string Name;
        public CompiledVariable Old;   // null when Added
        public CompiledVariable New;   // null when Removed
        public override string ToString() => $"{Kind} global '{Name}'";
    }

    /// <summary>A change to a single struct field, matched by name (or rename-paired).</summary>
    public sealed class FieldEdit
    {
        public FieldEditKind Kind;
        public string Name;            // new name (for Renamed, the destination)
        public string OldName;         // only set for Renamed
        public CompiledTypeMember Old; // valid unless Added
        public CompiledTypeMember New; // valid unless Removed
        public bool HasOld;
        public bool HasNew;
        public override string ToString() => Kind == FieldEditKind.Renamed
            ? $"Renamed field '{OldName}'->'{Name}'"
            : $"{Kind} field '{Name}'";
    }

    /// <summary>All field-level changes to one struct type, matched by typeName.</summary>
    public sealed class TypeEdit
    {
        public string TypeName;
        public bool Added;
        public bool Removed;
        public CompiledType Old;
        public CompiledType New;
        public List<FieldEdit> FieldEdits = new List<FieldEdit>();
        public bool HasLayoutChange => FieldEdits.Count > 0 || Added || Removed;
        public override string ToString() =>
            Added ? $"type '{TypeName}' added" :
            Removed ? $"type '{TypeName}' removed" :
            $"type '{TypeName}' [{string.Join(", ", FieldEdits.Select(f => f.ToString()))}]";
    }

    public enum FunctionEditKind { Added, Removed, SignatureChanged, BodyChanged }

    public sealed class FunctionEdit
    {
        public FunctionEditKind Kind;
        public string Name;
        public override string ToString() => $"{Kind} function '{Name}'";
    }

    /// <summary>
    /// The complete structural delta between two <see cref="ProgramFacts"/>.
    /// Pure data; produced by <see cref="StructuralDiff"/>.
    ///
    /// <see cref="ChangedStatementInstructions"/> is the edit region S expressed
    /// as a set of *old* bytecode instruction indexes whose statements changed.
    /// The control gate intersects the active set A with this. It is coarse by
    /// default (bytecode-range comparison keyed on source location); an AST diff
    /// can sharpen it later (optional, see the design doc).
    /// </summary>
    public sealed class EditSet
    {
        public List<VariableEdit> VariableEdits = new List<VariableEdit>();
        public List<TypeEdit> TypeEdits = new List<TypeEdit>();
        public List<FunctionEdit> FunctionEdits = new List<FunctionEdit>();

        /// <summary>old instruction indexes belonging to changed statements (region S).</summary>
        public HashSet<int> ChangedStatementInstructions = new HashSet<int>();

        /// <summary>true if the whole main-body bytecode changed but S couldn't be localized.</summary>
        public bool CoarseBodyChanged;

        public bool IsEmpty =>
            VariableEdits.Count == 0 && TypeEdits.Count == 0 &&
            FunctionEdits.Count == 0 && ChangedStatementInstructions.Count == 0 &&
            !CoarseBodyChanged;

        public IEnumerable<TypeEdit> LayoutChangedTypes => TypeEdits.Where(t => t.HasLayoutChange);
    }
}
