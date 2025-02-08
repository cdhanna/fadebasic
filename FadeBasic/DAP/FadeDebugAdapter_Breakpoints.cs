using FadeBasic.Json;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Breakpoint = FadeBasic.Launch.Breakpoint;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override void HandleSetBreakpointsRequestAsync(IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder)
    {
        var breakpoints = new List<Breakpoint>();
        var srcPath = responder.Arguments.Source.Path;

        foreach (var requestedBreakpoint in responder.Arguments.Breakpoints)
        {
           
           if (!_sourceMap.GetMappedPosition(srcPath, requestedBreakpoint.Line - 1, requestedBreakpoint.Column ?? 0,
                   out var token, strict: false))
           {
               _logger.Log($"Couldn't find breakpoint location for bp {requestedBreakpoint.Line}:{requestedBreakpoint.Column}");
              _logger.Log($"line to tokens {_sourceMap._lineToTokens.Select(kvp => kvp.Key + " -> " + string.Join(",", kvp.Value.Select(t => t.type)))}");
           }
           else
           {
               var bp = new Breakpoint
               {
                   colNumber = token.charNumber,
                   lineNumber = token.lineNumber
               };
               _logger.Log($"Found breakpoint {bp.Jsonify()}");
               breakpoints.Add(bp);
               
           }
        }

        _session.RequestBreakpoints(breakpoints, actualBreakPoints =>
        {
            var res = new SetBreakpointsResponse();

            foreach (var actualBreakpoint in actualBreakPoints)
            {
                var local = _sourceMap.GetOriginalLocation(actualBreakpoint.lineNumber, actualBreakpoint.colNumber);
                var source = new Source
                {
                    Path = local.fileName
                };
                res.Breakpoints.Add(new Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Breakpoint()
                {
                    Source = source,
                    Line = local.startLine + 1,
                    Column = local.startChar,
                    Verified = true
                });
            }

            responder.SetResponse(res);
        });
    }

    
    protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
    {
        var res = new SetExceptionBreakpointsResponse();
        return res;
    }
    protected override SetFunctionBreakpointsResponse HandleSetFunctionBreakpointsRequest(SetFunctionBreakpointsArguments arguments)
    {
        return new SetFunctionBreakpointsResponse();
    }
}