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
                var frame = debugFrames[i];
                var location = _sourceMap.GetOriginalLocation(frame.lineNumber, frame.colNumber);
                
                var source = new Source
                {
                    Path = location.fileName
                };

                var correctedLineNumber = location.startLine + 1; // zero indexed vs one indexed. The DAP seems to expect 1 based indexing
                var stack = new StackFrame(i, frame.name ?? "<root>", correctedLineNumber, location.startChar);
                stack.Source = source;
                frames.Add(stack);
            }
            responder.SetResponse(new StackTraceResponse(frames));
        });
    }
}