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
                    // VariablesReference = variable.id,
                    Name = variable.name,
                    Type = variable.type,
                    Value = variable.value,
                };
                
                // need to ask that we expand the variables...
                if (variable.fieldCount > 0 || variable.elementCount > 0)
                {
                    newVariable.VariablesReference = variable.id;
                    _session.RequestVariableInfo(variable.id, (subScopes) =>
                    {
                        subScopes[0].id = variableId;
                        db.AddScope(-1, subScopes[0]);
                        // TODO: wait for the response from these messages before continuing, 
                        //  because we need to tell our local cache what variable ids can be requested
                        //  maybe use a ref int counter with Interlocked
                    });
                }


                if (variable.fieldCount > 0) newVariable.NamedVariables = variable.fieldCount;
                if (variable.elementCount > 0) newVariable.IndexedVariables = variable.elementCount;
                
                res.Variables.Add(db.AddVariable(variable, newVariable));
                
                
                
                // if (variable.fieldCount == 0 && variable.elementCount == 0)
                // {
                //     // this is a primitive, 
                //     res.Variables.Add(db.AddVariable(variable, new Variable
                //     {
                //         Name = variable.name,
                //         Type = variable.type,
                //         Value = variable.value,
                //     }));
                // } else if (variable.elementCount > 0)
                // {
                //     res.Variables.Add(db.AddVariable(variable, new Variable
                //     {
                //         Name = variable.name,
                //         Type = variable.type,
                //         Value = variable.value,
                //         IndexedVariables = variable.elementCount
                //     }));
                // }
                // else if (variable.fieldCount > 0)
                // {
                //     // need to ask that we expand the variables...
                //     _session.RequestVariableInfo(variable.id, (subScopes) =>
                //     {
                //         subScopes[0].id = variableId;
                //         db.AddScope(-1, subScopes[0]);
                //         // TODO: wait for the response from these messages before continuing, 
                //         //  because we need to tell our local cache what variable ids can be requested
                //         //  maybe use a ref int counter with Interlocked
                //     });
                //     
                //     
                //     // this is a struct, and we need to know more!
                //     res.Variables.Add(db.AddVariable(variable, new Variable
                //     {
                //         VariablesReference = variable.id,
                //         Name = variable.name,
                //         Type = variable.type,
                //         Value = variable.value,
                //         NamedVariables = variable.fieldCount
                //     }));
                // }
                
            }
        }
        
        responder.SetResponse(res);
    }
}