using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    // protected override StackTraceResponse HandleStackTraceRequest(StackTraceArguments arguments)
    // {
    //     throw new Exception("uh oh?");
    // }

    protected override void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
    {
        _session.RequestStackFrames(debugFrames =>
        {
            var frames = new List<StackFrame>();
            for (var i = 0 ; i < debugFrames.Count; i ++)
            {
                frames.Add(new StackFrame(i, "root", debugFrames[i].lineNumber, debugFrames[i].colNumber));
            }
            responder.SetResponse(new StackTraceResponse(frames));
        });
    }
}