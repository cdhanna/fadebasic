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
    
    
//     [Test]
//     public void Exploration_Breakpoints()
//     {
//         var src = @"
// a = 3
// b# = 2.3
// ";
//         throw new NotImplementedException();
//         // Compile(src, out _, out var compiler, out var vm);
//         // var dbg = compiler.DebugData;
//         //
//         // dbg.insBreakpoints.Add(dbg.points[1].range.startToken.insIndex); // magic number where second variable is not yet defined
//         // var session = new DebugSession(vm, dbg);
//         // session.Execute();
//         // var variables = DebugUtil.LookupVariables(vm, dbg);
//         // Assert.That(variables.Count, Is.EqualTo(1));
//         //
//         // session.Continue();
//         // variables = DebugUtil.LookupVariables(vm, dbg);
//         // Assert.That(variables.Count, Is.EqualTo(2));
//     }

    
//     [Test]
//     public void Exploration_GetMap()
//     {
//         var src = @"
// a = 3
// b# = 2.3
// if a > 2
//     a = 9
// endif
// ";
//         Compile(src, out _, out var compiler, out var vm);
//         var dbg = compiler.DebugData;
//
//         var json = LaunchUtil.PackDebugData(dbg);
//         var db2 = LaunchUtil.UnpackDebugData(json);
//
//         var x = db2.points[2].range.startToken.insIndex + 9;
//         var tree = IntervalTree.From(dbg.points);
//
//         if (!tree.TryFind(x, out var map))
//         {
//             Assert.Fail("should have found map");
//         }
//     }

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

        { // verify that a pause event can be sent
            remote.SendPause(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(
                2); // read the message (1 op for the read, and 1 op to be ignored because we are paused)
            await Task.Delay(100); // fluff time for the ack to emit

            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(8),
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
            Assert.That(session.InstructionPointer, Is.EqualTo(16),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }



    }
    

}