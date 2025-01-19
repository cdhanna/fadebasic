using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public class VariableDatabase
{
    public interface IEntry
    {
        int VariableId { get; }
        List<DebugVariable> Variables { get; }
    }
    
    
    public class Entry : IEntry
    {
        public int frameId;
        public DebugScope dbgScope;

        public int VariableId => dbgScope.id;
        public List<DebugVariable> Variables => dbgScope.variables;
    }

    private Dictionary<int, IEntry> idToEntryTable = new Dictionary<int, IEntry>();
    private Dictionary<int, Variable> idToVariableTable = new Dictionary<int, Variable>();

    public Scope AddScope(int frameId, DebugScope dbgScope)
    {
        dbgScope.variables ??= new List<DebugVariable>();
        var entry = new Entry
        {
            dbgScope = dbgScope,
            frameId = frameId,
        };
        
        idToEntryTable[entry.VariableId] = entry;
        return new Scope
        {
            VariablesReference = entry.VariableId,
            NamedVariables = dbgScope.variables.Count,
            Name = dbgScope.scopeName,
        };
    }


    public bool TryGetEntry(int variableId, out IEntry entry)
    {
        return idToEntryTable.TryGetValue(variableId, out entry);
    }

    public bool TryGetVariable(DebugVariable dbgVariable, out Variable variable)
    {
        if (idToVariableTable.TryGetValue(dbgVariable.id, out variable))
        {
            return true;
        }
        
        return false;
    }
    //
    public Variable AddVariable(DebugVariable dbgVariable, Variable variable)
    {
        idToVariableTable[dbgVariable.id] = variable;
        return variable;
    }

}