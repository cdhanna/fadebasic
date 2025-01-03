using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
    {
        var res = new EvaluateResponse();
        var args = responder.Arguments;
        
        // need to take all the scopes for the given frameId
        //  and all the functions
        //  and all the labels... 
        
        //  and then somehow compile an expression with all that context; and see what it evalualtes to? 
    }
}