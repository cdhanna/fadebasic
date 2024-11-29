using FadeBasic.Launch;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;
using Thread = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages.Thread;

namespace DAP;

// https://github.com/microsoft/VSDebugAdapterHost/blob/main/src/sample/SampleDebugAdapter/SampleDebugAdapter.cs
public partial class FadeDebugAdapter : DebugAdapterBase
{
    private string _fileName;

    private RemoteDebugSession _session;
    
    public FadeDebugAdapter(Stream stdIn, Stream stdOut)
    {
        
        InitializeProtocolClient(stdIn, stdOut);
        Protocol.DispatcherError += (sender, args) =>
        {

        };
        Protocol.LogMessage += (sender, args) =>
        {

        };
        Protocol.RequestReceived += (sender, args) =>
        {

        };
    }

    public void Run() => Protocol.Run();

    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments)
    {
        var res = new InitializeResponse
        {
            SupportsConfigurationDoneRequest = true,
            
        };
        var canRunInTerminal = arguments.SupportsRunInTerminalRequest;
        
        Protocol.SendEvent(new InitializedEvent());
        return res;
    }


    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        _fileName = arguments.ConfigurationProperties.GetValueAsString("program");
        if (String.IsNullOrEmpty(_fileName))
        {
            throw new ProtocolException("Launch failed because launch configuration did not specify 'program'.");
        }

        var res = new LaunchResponse();
        return res;
    }

    protected override SetExceptionBreakpointsResponse HandleSetExceptionBreakpointsRequest(SetExceptionBreakpointsArguments arguments)
    {
        var res = new SetExceptionBreakpointsResponse();
        return res;
    }

    protected override SetBreakpointsResponse HandleSetBreakpointsRequest(SetBreakpointsArguments arguments)
    {
        var res = new SetBreakpointsResponse();
        foreach (var breakpoint in arguments.Breakpoints)
        {
            res.Breakpoints.Add(new Breakpoint(false));
        }

        return res;
    }

    protected override SetFunctionBreakpointsResponse HandleSetFunctionBreakpointsRequest(SetFunctionBreakpointsArguments arguments)
    {
        return new SetFunctionBreakpointsResponse();
    }

    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
    {
        // at this point, we can actually kick off the process. 
        var path = Path.GetDirectoryName(_fileName);
        var port = LaunchUtil.FreeTcpPort();
        var startReq = new RunInTerminalRequest(path, new List<string>
        {
            "dotnet", "run", _fileName, "-p:FadeBasicDebug=true"
        });
        startReq.Kind = RunInTerminalArguments.KindValue.Integrated;
        startReq.Title = "Fade";
        startReq.ArgsCanBeInterpretedByShell = true;
        startReq.Env = new Dictionary<string, object>
        {
            [LaunchOptions.ENV_ENABLE_DEBUG] = "true",
            [LaunchOptions.ENV_DEBUG_PORT] = port,
        };
        this.Protocol.SendClientRequest(startReq, x =>
        {
            
            _session = new RemoteDebugSession(port);
            _session.Connect();
        }, (args, err) =>
        {
            
        });
        return new ConfigurationDoneResponse();
    }

    protected override ThreadsResponse HandleThreadsRequest(ThreadsArguments arguments)
    {
        return new ThreadsResponse
        {
            Threads = new List<Thread>
            {
                new Thread
                {
                    Id = 1
                }
            }
        };
    }
    
    

    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
    {
        // TODO: need to kill the debugee process...
        return new DisconnectResponse();
    }

    protected override void HandleProtocolError(Exception ex)
    {
        base.HandleProtocolError(ex);
    }
}