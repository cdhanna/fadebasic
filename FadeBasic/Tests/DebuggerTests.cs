using System.Collections.Concurrent;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Json;
using FadeBasic.Virtual;
using FadeBasic.Launch;

namespace Tests;

public class DebuggerTests
{

    public void Compile(string src, out ProgramNode program, out Compiler compiler, out VirtualMachine vm)
    {
        var lexer = new Lexer();
        var lexerResults = lexer.TokenizeWithErrors(src);
        var parser = new Parser(lexerResults.stream, TestCommands.CommandsForTesting);
        program = parser.ParseProgram();
        program.AssertNoParseErrors();

        compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions
        {
            GenerateDebugData = true
        });
        compiler.Compile(program);

        vm = new VirtualMachine(compiler.Program);
        vm.hostMethods = compiler.methodTable;
    }
    
    [Test]
    public void Exploration_Variables()
    {
        var src = @"
a = 3
b# = 2.3
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        vm.Execute2();

        var variables = DebugUtil.LookupVariables(vm, dbg);

        Assert.That(variables.Count, Is.EqualTo(2));
    }
    
    
    [Test]
    public void Exploration_Breakpoints()
    {
        var src = @"
a = 3
b# = 2.3
";
        throw new NotImplementedException();
        // Compile(src, out _, out var compiler, out var vm);
        // var dbg = compiler.DebugData;
        //
        // dbg.insBreakpoints.Add(dbg.points[1].range.startToken.insIndex); // magic number where second variable is not yet defined
        // var session = new DebugSession(vm, dbg);
        // session.Execute();
        // var variables = DebugUtil.LookupVariables(vm, dbg);
        // Assert.That(variables.Count, Is.EqualTo(1));
        //
        // session.Continue();
        // variables = DebugUtil.LookupVariables(vm, dbg);
        // Assert.That(variables.Count, Is.EqualTo(2));
    }

    [TestCase(3,10)]
    [TestCase(492,19155)]
    [TestCase(31229,50)]
    [TestCase(1299999,5915815)]
    public void LocationPackTests(int l, int c)
    {
        DebugUtil.PackPosition(l,c, out var d);
        DebugUtil.UnpackPosition(d, out var lOut, out var cOut);
        
        Assert.That(l, Is.EqualTo(lOut));
        Assert.That(c, Is.EqualTo(cOut));
    }
    
    [Test]
    public void Exploration_GetMap()
    {
        var src = @"
a = 3
b# = 2.3
if a > 2
    a = 9
endif
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var json = LaunchUtil.PackDebugData(dbg);
        var db2 = LaunchUtil.UnpackDebugData(json);

        var x = db2.points[2].range.startToken.insIndex + 9;
        var tree = IntervalTree.From(dbg.points);

        if (!tree.TryFind(x, out var map))
        {
            Assert.Fail("should have found map");
        }
    }

    class DummyServer
    {
        private readonly int _port;
        private readonly DebugConnectionFunction _binder;
        private readonly CancellationTokenSource _cts;
        private Task _task;
        private ConcurrentQueue<DebugControlMessage> _outboundMessages = new ConcurrentQueue<DebugControlMessage>();
        private ConcurrentQueue<DebugControlMessage> _receivedMessages = new ConcurrentQueue<DebugControlMessage>();
        private Task _logicTask;
        private Func<DebugControlMessage, DebugControlMessage?> _messageHandler;

        public DummyServer(
            int port, 
            DebugConnectionFunction binder,
            Func<DebugControlMessage, DebugControlMessage?> messageHandler)
        {
            _messageHandler = messageHandler;
            _port = port;
            _binder = binder;
            _cts = new CancellationTokenSource();
        }

        public void Send(DebugControlMessage message) => _outboundMessages.Enqueue(message);

        public void Start()
        {
            _task = Task.Run(() =>
            {
                _binder(_port, _outboundMessages, _receivedMessages, _cts.Token);
                // DebugServerStreamUtil.OpenServer(_port, _outboundMessages, _receivedMessages, _cts.Token);
            });

            _logicTask = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    while (_receivedMessages.TryDequeue(out var message))
                    {
                        // stupid, just double all messages
                        var res = _messageHandler(message);
                        if (res.HasValue)
                        {
                            _outboundMessages.Enqueue(res.Value);
                        }
                    }
                    await Task.Delay(1);
                }
            });
        }

        public void Stop()
        {
            _cts.Cancel();
        }

        public async Task Wait()
        {
            await _task;
            await _logicTask;
        }
    }

    [Test]
    public async Task ServerHuh()
    {
        
        var port = 9901;
        var cts = new CancellationTokenSource();
        var hostTask = MessageServer<ServerHuhMsg>.Host(port);
        var clientTask = MessageServer<ServerHuhMsg>.Join(port);

        var host = await hostTask;
        var client = await clientTask;

        var serverIn = new ConcurrentQueue<ServerHuhMsg>();
        var clientOut = new ConcurrentQueue<ServerHuhMsg>();
        
        var serverOut = new ConcurrentQueue<ServerHuhMsg>();
        var clientIn = new ConcurrentQueue<ServerHuhMsg>();

        var clientSend = MessageServer<ServerHuhMsg>.Emit(client, cts, clientOut);
        var serverListen = MessageServer<ServerHuhMsg>.Listen(host, cts, serverIn);
        var clientListen = MessageServer<ServerHuhMsg>.Listen(client, cts, clientIn);
        var serverSend = MessageServer<ServerHuhMsg>.Emit(host, cts, serverOut);

        await Task.Delay(100);
        
        clientOut.Enqueue(new ServerHuhMsg
        {
            id = 3,
            msg = "hello world"
        });
        
        await Task.Delay(100);
        Assert.That(clientOut.Count, Is.EqualTo(0));
        Assert.That(serverIn.Count, Is.EqualTo(1));
        
        serverOut.Enqueue(new ServerHuhMsg
        {
            id = 9,
            msg = "blah"
        });

        await Task.Delay(100);
        
        Assert.That(serverOut.Count, Is.EqualTo(0));
        Assert.That(clientIn.Count, Is.EqualTo(1));

        
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await serverListen;
        await clientSend;
    }

    class ServerHuhMsg : IJsonable
    {
        public int id;
        public string msg;
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField("id", ref id);
            op.IncludeField("msg", ref msg);
        }
    }

    [Test]
    public async Task DebugServerTest()
    {
        
        var port = 8909;
        var src = @"
b = 1
b2 = 2
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, new LaunchOptions
        {
            debug = true,
            debugPort = port,
            debugWaitForConnection = true
        });
        session.StartServer();
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(0),
            "because the client has not connected yet, the program should not have run at all, even through there was budget");
        
        var remote = new RemoteDebugSession(port);
        remote.Connect();

        await Task.Delay(100); // fluff time for the connection to happen...
        
        session.StartDebugging(1);
        Assert.That(session.InstructionPointer, Is.EqualTo(0),
            "Exactly 1 op will let the debugger attach, but the program counter has no budget left");
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(8),
            "I happen to know that 2 is a magical number of budget to yield 8 as an instruction index...");

        
        var receivedConf = false;
        remote.SendPause(() =>
        {
            receivedConf = true;
        });
        
        session.StartDebugging(1);


        await Task.Delay(100);
        
        
        // remote.Send(new DebugMessageLegacy
        // {
        //     id = 1,
        //     type = 1
        // }, msg =>
        // {
        //     
        // });
        await Task.Delay(100);

        // Assert.That(receivedConf, Is.True);
        // session._cts.CancelAfter(TimeSpan.FromSeconds(1));


    }
    
    [Test]
    public async Task Exploration_StreamTest()
    {
        var port = 8903;
        var server = new DummyServer(port, DebugServerStreamUtil.OpenServer, message =>
        {
            return new DebugControlMessage
            {
                id = message.id,
                arg = message.arg * 2,
                type = DebugControlMessageTypes.PROTOCOL_ACK
            };
        });
        server.Start();

        // await Task.Delay(50);

        ulong sent = 3;
        ulong got = 0;
        var client = new DummyServer(port, DebugServerStreamUtil.ConnectToServer, message =>
        {
            // eh?
            got = message.arg;
            return default;
        });
        client.Start();

        await Task.Delay(100);
        
        client.Send(new DebugControlMessage
        {
            arg = sent, id = 1, type = 1
        });
        
        await Task.Delay(50);

        server.Stop();
        client.Stop();
        
        Assert.That(got, Is.EqualTo(sent * 2));
        
        // await server.Wait();
    }

}