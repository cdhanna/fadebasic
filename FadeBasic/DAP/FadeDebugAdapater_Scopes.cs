using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    private Dictionary<int, DebugScope> variableReferenceToscopes = new Dictionary<int, DebugScope>();
    
    List<Scope> ConvertToScopes(List<DebugScope> debugScopes)
    {
        var scopes = new List<Scope>();
        for (var i = 0; i < debugScopes.Count; i++)
        {
            var scope = new Scope();
            var dbgScope = debugScopes[i];
            scopes.Add(scope);

            scope.Name = dbgScope.scopeName;
            scope.NamedVariables = dbgScope.variables.Count;
            scope.VariablesReference = i + 1;
            variableReferenceToscopes[scope.VariablesReference] = dbgScope;
        }
        return scopes;
    }
    
    protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
    {
        // responder.Arguments.
        
        _session.RequestScopes(responder.Arguments.FrameId, scopes =>
        {
            var finalScopes = ConvertToScopes(scopes);
            // TODO: huh? 
            responder.SetResponse(new ScopesResponse
            {
                Scopes = finalScopes
            });
        });
    }

    protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
    {
        
        if (!variableReferenceToscopes.TryGetValue(responder.Arguments.VariablesReference, out var dbgScope))
        {
            _logger.Log("[ERR] invalid variables requested. ");
            responder.SetResponse(new VariablesResponse()
            {
            });
        }
        
        var res = new VariablesResponse();
        foreach (var variable in dbgScope.variables)
        {
            res.Variables.Add(new Variable
            {
                Name = variable.name,
                Type = variable.type,
                Value = variable.value,
            });
        }
        
        
        responder.SetResponse(res);

        // _session.RequestScopes(0, dbgScopes =>
        // {
        //     // var scopes = ConvertToScopes(dbgScopes);
        //     var dbgScope = dbgScopes[responder.Arguments.VariablesReference-1];
        //
        //     var res = new VariablesResponse();
        //     foreach (var variable in dbgScope.variables)
        //     {
        //         res.Variables.Add(new Variable
        //         {
        //             Name = variable.name,
        //             Type = variable.type,
        //             Value = variable.value,
        //         });
        //     }
        //     
        //     
        //     responder.SetResponse(res);
        // });
    }
}