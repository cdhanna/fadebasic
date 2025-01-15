using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    protected override PauseResponse HandlePauseRequest(PauseArguments arguments)
    {
        ResetVariableLifetime();
        _session.SendPause(() =>
        {
            Protocol.SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Pause)
            {
                AllThreadsStopped = true
            });
        });
        return new PauseResponse();
    }

    protected override ContinueResponse HandleContinueRequest(ContinueArguments arguments)
    {
        ResetVariableLifetime();
        _session.SendPlay(() =>
        {
            Protocol.SendEvent(new ContinuedEvent());
        });
        return new ContinueResponse();
    }

    protected override NextResponse HandleNextRequest(NextArguments arguments)
    {
        ResetVariableLifetime();
        _session.SendStepOver((msg) =>
        {
            Protocol.SendEvent(new StoppedEvent
            {
                Reason = StoppedEvent.ReasonValue.Step,
                AllThreadsStopped = true,
            });
        });
        return new NextResponse();
    }

    protected override StepInResponse HandleStepInRequest(StepInArguments arguments)
    {
        ResetVariableLifetime();
        _session.SendStepIn((msg) =>
        {
            Protocol.SendEvent(new StoppedEvent
            {
                Reason = StoppedEvent.ReasonValue.Step,
                AllThreadsStopped = true,
            });
        });
        return new StepInResponse();
    }

    protected override StepOutResponse HandleStepOutRequest(StepOutArguments arguments)
    {
        ResetVariableLifetime();
        _session.SendStepOut((msg) =>
        {
            Protocol.SendEvent(new StoppedEvent
            {
                Reason = StoppedEvent.ReasonValue.Step,
                AllThreadsStopped = true,
            });
        });
        return new StepOutResponse();
    }
}