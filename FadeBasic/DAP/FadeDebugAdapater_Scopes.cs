using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
    {
        // responder.Arguments.
        responder.SetResponse(new ScopesResponse
        {
            Scopes = new List<Scope>()
        });
    }
}