using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    // protected override void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> responder)
    // {
    //     var res = new SetExpressionResponse();
    //     var args = responder.Arguments;
    //     _session.RequestSetVariable(args.VariablesReference, args.Name, args.Value, msg =>
    //     {
    //         responder.SetError(new ProtocolException("Unable to acquiesce your request!"));
    //     });
    //
    // }

    private int errorCount;
    protected override void HandleSetExpressionRequestAsync(IRequestResponder<SetExpressionArguments, SetExpressionResponse> responder)
    {
        var res = new SetExpressionResponse();
        var args = responder.Arguments;

        if (!int.TryParse(args.Expression, out var variableId))
        {
            _logger.Log($"unable to set expression because expr=[{args.Expression}] must be a variable id");
            responder.SetError(new ProtocolException("Invalid expression, must be a variable id"));
        }
        
        _session.RequestSetVariable(variableId, args.FrameId.GetValueOrDefault(), args.Value, msg =>
        {
            if (msg.id < 0)
            {
                responder.SetError(new ProtocolException(msg.value, ++errorCount, msg.value, showUser:true ));
                return;
            }
            _logger.Log("set val but who knows ");
            res.Type = msg.type;
            res.Value = msg.value;
            res.IndexedVariables = msg.elementCount;
            res.NamedVariables = msg.fieldCount;
            
            if (msg.fieldCount > 0 || msg.elementCount > 0)
            {
                res.VariablesReference = msg.id;
                db.AddScope(-1, msg.scope);
            }
            responder.SetResponse(res);
            // responder.SetError(new ProtocolException("Unable to acquiesce your request!"));
        });
        
        // responder.SetResponse(res);
        // throw new NotImplementedException("not yet!");
    }

    protected override void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
    {
        var res = new EvaluateResponse();
        var args = responder.Arguments;

        var expr = args.Expression;
        var frame = args.FrameId;
        _session.RequestEval(frame.GetValueOrDefault(), expr, result =>
        {
            res.Result = result.value;
            res.Type = result.type;
            if (result.fieldCount > 0 || result.elementCount > 0)
            {
                res.VariablesReference = result.scope.id;
                db.AddScope(-1, result.scope);
            }
            
            //
            res.PresentationHint = new VariablePresentationHint
            {
                Kind = VariablePresentationHint.KindValue.Data
            };
            if (result.id < 0)
            {
                // res.PresentationHint.Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation;
                res.PresentationHint = new VariablePresentationHint
                {
                    Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation
                };
            }

            if (string.Equals("string", res.Type, StringComparison.InvariantCultureIgnoreCase))
            {
                res.PresentationHint.Attributes = VariablePresentationHint.AttributesValue.RawString;
            }
            
            responder.SetResponse(res);
        });
    }
}