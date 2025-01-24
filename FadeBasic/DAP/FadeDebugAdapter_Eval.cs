using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override void HandleSetExpressionRequestAsync(IRequestResponder<SetExpressionArguments, SetExpressionResponse> responder)
    {
        throw new NotImplementedException("not yet!");
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
            if (result.id > 0)
            {
                res.VariablesReference = result.scope.id;
                //
                //
                // var scope = new DebugScope
                // {
                //     scopeName = "watch",
                //     id = result.id,
                //     variables = new List<DebugVariable>
                //     {
                //         new DebugVariable
                //         {
                //             id = result.id,
                //             name = expr,
                //             value = result.value,
                //             type = result.type,
                //
                //             elementCount = result.elementCount,
                //             fieldCount = result.fieldCount,
                //         }
                //     }
                // };
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