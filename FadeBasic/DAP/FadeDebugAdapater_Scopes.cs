using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    // private Dictionary<int, DebugScope> variableReferenceToscopes = new Dictionary<int, DebugScope>();
    public VariableDatabase db = new VariableDatabase();

    void ResetVariableLifetime()
    {
        db = new VariableDatabase();
    }
    
    protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
    {
        // responder.Arguments.
        
        _session.RequestScopes(responder.Arguments.FrameId, scopes =>
        {
            var finaScopes = new List<Scope>();
            foreach (var dbgScope in scopes)
            {
                var scope = db.AddScope(responder.Arguments.FrameId, dbgScope);
                finaScopes.Add(scope);
            }
            
            responder.SetResponse(new ScopesResponse
            {
                Scopes = finaScopes
            });
        });
    }

    protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
    {

        if (!db.TryGetEntry(responder.Arguments.VariablesReference, out var entry))
        {
            _logger.Log("[ERR] invalid variables requested. ");
            responder.SetResponse(new VariablesResponse()
            {
            });
        }
        var res = new VariablesResponse();
        
        foreach (var variable in entry.Variables)
        {
            var variableId = variable.id;
            if (db.TryGetVariable(variable, out var existing))
            {
                res.Variables.Add(existing);
            }
            else
            {
                // need to construct the variable...
                
                var newVariable = new Variable
                {
                    Name = variable.name,
                    Type = variable.type,
                    Value = variable.value,
                    EvaluateName = variable.id.ToString(),
                    PresentationHint = new VariablePresentationHint()
                };
                

                if (string.Equals("string", newVariable.Type, StringComparison.InvariantCultureIgnoreCase))
                {
                    newVariable.PresentationHint.Kind = VariablePresentationHint.KindValue.Data;
                    newVariable.PresentationHint.Attributes = VariablePresentationHint.AttributesValue.RawString;
                    newVariable.PresentationHint.Visibility = VariablePresentationHint.VisibilityValue.Public;
                }
                
                // need to ask that we expand the variables...
                if (variable.fieldCount > 0 || variable.elementCount > 0)
                {
                    newVariable.VariablesReference = variable.id;
                    _session.RequestVariableInfo(variable.id, (subScopes) =>
                    {
                        subScopes[0].id = variableId;
                        db.AddScope(-1, subScopes[0]);
                    });
                }
                
                if (variable.fieldCount > 0) newVariable.NamedVariables = variable.fieldCount;
                if (variable.elementCount > 0) newVariable.IndexedVariables = variable.elementCount;
                
                res.Variables.Add(db.AddVariable(variable, newVariable));
                
            }
        }
        
        responder.SetResponse(res);
    }
}