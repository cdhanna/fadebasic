using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace DAP;

public partial class FadeDebugAdapter
{
    

    protected override PauseResponse HandlePauseRequest(PauseArguments arguments)
    {
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
        _session.SendPlay(() =>
        {
            Protocol.SendEvent(new ContinuedEvent());
        });
        return new ContinueResponse();
    }
}