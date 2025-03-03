using System.Buffers.Binary;
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
        var lexerResults = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting);
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
    public void Exploration_Variables_Arrays()
    {
        var src = @"
type vec
    x
    y
endtype
dim x(3,5) as vec
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, null, new LaunchOptions
        {
            debugWaitForConnection = false,
            debug = true,
            debugPort = 9999
        });
        session.StartDebugging();

        session.variableDb.GetGlobalVariablesForFrame(0);
        var scope = session.variableDb.Expand(2);
        // session.variableDb.Expand()

        var variables = DebugUtil.LookupVariables(vm, dbg, global: true);
        
        
        Assert.That(variables.Count, Is.EqualTo(1));
    }
    
    [TestCase("x# = 4.2", "x#+1", "5.2")]
    [TestCase("inc x", "x", "1")]
    [TestCase("tuna x$", "x$", "tuna")]
    [TestCase("x = 4", "x+1", "5")]
    [TestCase(@"
x = 1
function decoyFunction()
endfunction
function sampleFunc(x)
endfunction x + 1
", "sampleFunc(2) + x", "4")]
    [TestCase(@"
dim x(4,9)
x(3,8) = 4
", "x(3,8)+1", "5")]
    [TestCase(@"
type vec
    x
    y
endtype
dim vees(3) as vec
v as vec
v.x = 44
vees(1) = v
", "vees(1).x", "44")]
    [TestCase(@"
type vec
    x
    y
endtype
a = 5
v as vec
v.x = 3
v.y = 2
", "v", "[vec]")]
    [TestCase(@"
type vec
    x
    y
endtype
type egg
    a 
    v as vec
endtype
n = 6
v as vec
v.x = 3
v.y = 2
`e as egg
`e.a = 3
`e.v = v
", "n:v", "6:[vec]")]
    [TestCase(@"`simple array case
dim x(3)
x(1) = 4
", "x", "(3)")]
    [TestCase(@"`struct array case
type vec
    x
    y
endtype
dim x(3) as vec
x(1).x = 4
", "x", "(3)")]
    public async Task Exploration_Eval(string src, string evalGroup, string expectedGroup)
    {
        var evals = evalGroup.Split(":", StringSplitOptions.RemoveEmptyEntries);
        var expects = expectedGroup.Split(":", StringSplitOptions.RemoveEmptyEntries);
        if (evals.Length != expects.Length) throw new InvalidOperationException("bad test input");

        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50); // give some time for the program to finish executing... 

        for (var i = 0; i < evals.Length; i++)
        {
            var eval = evals[i];
            var expected = expects[i];
            var res = session.Eval(0, eval);
            Assert.That(res.value, Is.EqualTo(expected));

        }

    }
    
    [TestCase(@"`basic case
x = 5
", "x", "8", "8", 3, new int[]{})]
    [TestCase(@"`basic float
x# = 5.2
", "x#", "8.3", "8.3", 3, new int[]{})]
    
    [TestCase(@"`basic float (but looks like int)
x# = 5.2
", "x#", "8", "8", 3, new int[]{})]
    
    [TestCase(@"`basic byte
x as byte = 5
", "x", "8", "8", 3, new int[]{})]

    [TestCase(@"`basic double integer
x as double integer = 5
", "x", "8", "8", 3, new int[]{})]

    [TestCase(@"`accessor
type vec
    x
    y
endtype
v as vec
v.x = 5
", "v.x", "8", "8", 5, new int[]{3})]
    [TestCase(@"`replace struct
type vec
    x
    y
endtype
v as vec
v2 as vec
v.x = 5
v2.x = 10
", "v", "v2", "[vec]", 3, new int[]{})]
    public async Task Exploration_Expr(string src, string lhs, string rhs, string expected, int variableId, int[] idsToExpand)
    {
        
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50); // give some time for the program to finish executing... 
        
        session.GetScopes(new DebugScopeRequest
        {
            frameIndex = 0
        });
        foreach (var id in idsToExpand)
        {
            session.variableDb.Expand(id);
        }
        
        var res = session.Eval(0, rhs, variableId);
        

        
        Assert.That(res.value, Is.EqualTo(expected));
    }

    
    [Test]
    public void Exploration_Variables_Structs()
    {
        var src = @"
TYPE egg
    x
ENDTYPE

greg AS egg
greg.x = 3
dan AS egg
dan.x = 5

";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        vm.Execute2();

        var variables = DebugUtil.LookupVariables(vm, dbg, global: false);

        Assert.That(variables.Count, Is.EqualTo(2));
    }

    
    [Test]
    public void FunctionMap()
    {
        var src = @"n = 1
igloo(n)

function igloo(y)
x = y * 2
toast()
endfunction

function toast()
endfunction
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var map = new IndexCollection(dbg.statementTokens);
     
    }

    [Test]
    public void IndexMap()
    {
        var src = @"n = 0
igloo()

function igloo()
    print ""toast""
    while 1 > 0
        inc n
        print ""hello"", n
        wait ms 500
        getVm
    endwhile
endfunction";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var map = new IndexCollection(dbg.statementTokens);
        if (!map.TryFindClosestTokenBeforeIndex(176, out var lastToken))
        {
            Assert.Fail("There should be something for the last token");
        }
        // var tree = IntervalTree.From(dbg.points);
// ITS ONLY GETTING LINE 1 for some reason?


// TODO: 182 is not even in the tree, but that is the number that is being hit in real life after step-over. 
        // var hasIndex = tree.TryFind(182, out var index);
    }

    
    [Test]
    public async Task DebugServerTest()
    {
        
        var port = LaunchUtil.FreeTcpPort();
        var src = @"
b = 1
b2 = 2
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, null, new LaunchOptions
        {
            debug = true,
            debugPort = port,
            debugWaitForConnection = true
        });
        session.StartServer();
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "because the client has not connected yet, the program should not have run at all, even through there was budget");
        
        var remote = new RemoteDebugSession(port);
        remote.Connect();
        remote.SayHello();

        await Task.Delay(100); // fluff time for the connection to happen...
        
        session.StartDebugging(1);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "Exactly 1 op will let the debugger attach, but the program counter has no budget left");
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(12),
            "I happen to know that 2 is a magical number of budget to yield 8 as an instruction index...");

        
        var receivedConf = false;

        { // verify that a pause event can be sent
            remote.SendPause(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(
                2); // read the message (1 op for the read, and 1 op to be ignored because we are paused)
            await Task.Delay(100); // fluff time for the ack to emit

            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(12),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }
        
        { // check stack frames
            remote.RequestStackFrames(frames =>
            {
                
            });
            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(1); // read the message, but do not process
            await Task.Delay(100); // fluff time for the ack to emit
        }

        { // verify that a play event can be sent
            receivedConf = false;
            remote.SendPlay(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(2); // read the message (1 op for the read, and 1 op to move the debugger forward)
            await Task.Delay(100); // fluff time for the ack to emit
            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(27),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }



    }
    

}