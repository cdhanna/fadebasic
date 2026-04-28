using System.Linq;
using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> responder)
    {
        var args = responder.Arguments;

        // Find the child variable by name within the parent scope
        if (!db.TryGetEntry(args.VariablesReference, out var entry))
        {
            responder.SetError(new ProtocolException($"Unknown variablesReference {args.VariablesReference}"));
            return;
        }

        var childVar = entry.Variables?.FirstOrDefault(v =>
            string.Equals(v.name, args.Name, StringComparison.OrdinalIgnoreCase));
        if (childVar == null)
        {
            responder.SetError(new ProtocolException($"Variable '{args.Name}' not found in scope"));
            return;
        }

        _session.RequestSetVariable(childVar.id, 0, args.Value, msg =>
        {
            if (msg.id < 0)
            {
                responder.SetError(new ProtocolException(msg.value));
                return;
            }

            // Update both the underlying DebugVariable (read on cache miss) and the cached
            // DAP Variable (returned on cache hit) so a subsequent `variables` request reflects
            // the new value. Without this, the variable cache returns the original stale value.
            childVar.value = msg.value;
            childVar.type = msg.type;
            if (db.TryGetVariable(childVar, out var cachedVar))
            {
                cachedVar.Value = msg.value;
                cachedVar.Type = msg.type;
            }

            var res = new SetVariableResponse
            {
                Value = msg.value,
                Type = msg.type,
                IndexedVariables = msg.elementCount,
                NamedVariables = msg.fieldCount,
            };
            if (msg.fieldCount > 0 || msg.elementCount > 0)
            {
                res.VariablesReference = msg.id;
                if (msg.scope != null)
                    db.AddScope(-1, msg.scope);
            }
            responder.SetResponse(res);
        });
    }

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
        var frame = args.FrameId.GetValueOrDefault();

        if (args.Context == EvaluateArguments.ContextValue.Repl)
        {
            _session.RequestRepl(frame, args.Expression, result =>
            {
                if (result.id < 0)
                {
                    res.Result = result.value;
                    res.PresentationHint = new VariablePresentationHint
                    {
                        Attributes = VariablePresentationHint.AttributesValue.FailedEvaluation
                    };
                }
                else
                {
                    res.Result = result.value ?? "";
                    res.Type = result.type;
                    if (result.fieldCount > 0 || result.elementCount > 0)
                    {
                        res.VariablesReference = result.scope?.id ?? 0;
                        if (result.scope != null)
                            db.AddScope(-1, result.scope);
                    }
                }
                responder.SetResponse(res);
            });
            return;
        }

        _session.RequestEval(frame, args.Expression, result =>
        {
            res.Result = result.value;
            res.Type = result.type;
            if (result.fieldCount > 0 || result.elementCount > 0)
            {
                res.VariablesReference = result.scope.id;
                db.AddScope(-1, result.scope);
            }

            res.PresentationHint = new VariablePresentationHint
            {
                Kind = VariablePresentationHint.KindValue.Data
            };
            if (result.id < 0)
            {
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