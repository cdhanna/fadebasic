using System.Diagnostics;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
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
    private string _debuggerLogPath;

    private RemoteDebugSession _session;
    private ProjectContext? _project;
    private SourceMap? _sourceMap;
    private IDAPLogger _logger;

    public FadeDebugAdapter(IDAPLogger logger, Stream stdIn, Stream stdOut)
    {
        _logger = logger;

        InitializeProtocolClient(stdIn, stdOut);
        Protocol.DispatcherError += (sender, args) =>
        {
            
            logger.Log($"Unhandled error! type=[{args.Exception.GetType().Namespace}]\n message=[{args.Exception.Message}]\n stack=[{args.Exception.StackTrace}]");
        };
        Protocol.LogMessage += (sender, args) =>
        {
            logger.Log($"{args.Category} [{args.Message}]");
        };
        Protocol.RequestReceived += (sender, args) =>
        {
            logger.Log($"request command=[{args.Command}]");
        };
    }

    public void Run() => Protocol.Run();

    protected override InitializeResponse HandleInitializeRequest(InitializeArguments arguments)
    {
        
        var res = new InitializeResponse
        {
            SupportsConfigurationDoneRequest = true,
            SupportsSetExpression = true,
            SupportsSetVariable = false,
            // SupportsReadMemoryRequest = true,
        };
        _logger.Log($"VARIABLE_SUPPORT=[{arguments.SupportsVariableType}]");
        return res;
    }


    protected override LaunchResponse HandleLaunchRequest(LaunchArguments arguments)
    {
        _fileName = arguments.ConfigurationProperties.GetValueAsString("program");
        _debuggerLogPath = arguments.ConfigurationProperties.GetValueAsString("debuggerLogPath");
        
        if (String.IsNullOrEmpty(_fileName))
        {
            throw new ProtocolException("Launch failed because launch configuration did not specify 'program'.");
        }

        _project = ProjectLoader.LoadCsProject(_fileName);
        var projectInfo = ProjectBuilder.LoadCommandMetadata(_project.projectLibraries);
        var lexer = new Lexer();
        _sourceMap = _project.CreateSourceMap();
        var source = _sourceMap.fullSource;
        var lexerResults = lexer.TokenizeWithErrors(source, projectInfo.collection);
        _sourceMap.ProvideTokens(lexerResults);
        
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
            [LaunchOptions.ENV_DEBUG_LOG_PATH] = _debuggerLogPath
        };
        
        _session = new RemoteDebugSession(port);

        _session.HitBreakpointCallback = () =>
        {
            Protocol.SendEvent(new StoppedEvent
            {
                Reason = StoppedEvent.ReasonValue.Breakpoint,
                Description = "Hit a breakpoint",
                AllThreadsStopped = true,
                HitBreakpointIds = new List<int>(){0}
            });
        };

        _session.Exited = () =>
        {
            Protocol.SendEvent(new ExitedEvent());
            Protocol.SendEvent(new TerminatedEvent());
        };

        _session.RuntimeException = (error) =>
        {
            _logger.Log($"Received runtime exception message=[{error}]");
            Protocol.SendEvent(new StoppedEvent(StoppedEvent.ReasonValue.Exception)
            {
                Text = "Fatal Exception",
                Description = error,
                AllThreadsStopped = true,
            });
        };

        // as soon as this event is sent- debugger info will appear. 
        Protocol.SendEvent(new InitializedEvent());
        
        
        this.Protocol.SendClientRequest(startReq, x =>
        {
            _logger.Log("Connecting to debug application");
            _session.Connect();
        
        }, (args, err) =>
        {
            
        });
        var res = new LaunchResponse();
        return res;
    }


    protected override ConfigurationDoneResponse HandleConfigurationDoneRequest(ConfigurationDoneArguments arguments)
    {
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

    protected override void HandleTerminateRequestAsync(IRequestResponder<TerminateArguments> responder)
    {
        _session.SendTerminate(() =>
        {
            responder.SetResponse(new TerminateResponse());
        });
    }


    protected override DisconnectResponse HandleDisconnectRequest(DisconnectArguments arguments)
    {
        _logger.Log("KILLING: " + _session.RemoteProcessId);
        _session.SendTerminate(() => { });

        Protocol.SendEvent(new ExitedEvent(0));
        return new DisconnectResponse();
    }
    
    // protected override void HandleDisconnectRequestAsync(IRequestResponder<DisconnectArguments> responder)
    // {
    //     _session.SendTerminate(() =>
    //     {
    //         Protocol.SendEvent(new ExitedEvent());
    //         responder.SetResponse(new DisconnectResponse());
    //     });
    // }

    protected override void HandleProtocolError(Exception ex)
    {
        base.HandleProtocolError(ex);
    }
}