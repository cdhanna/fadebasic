using System;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace Tests;

[TestFixture]
public class RuntoOpCodeTests
{
    /// <summary>
    /// Produces a minimal valid VM program with a stub interned-data section,
    /// then lets the caller specify the code bytes that live between the
    /// 4-byte header and the interned-data section.
    /// </summary>
    private byte[] BuildProgram(byte[] code)
    {
        var src = "x = 0\n";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);
        var compiled = compiler.Program.ToArray();

        var origInterned = BitConverter.ToInt32(compiled, 0);
        var internedTail = compiled.AsSpan(origInterned, compiled.Length - origInterned).ToArray();

        var newInternedStart = 4 + code.Length;
        var program = new byte[4 + code.Length + internedTail.Length];
        var headerBytes = BitConverter.GetBytes(newInternedStart);
        Array.Copy(headerBytes, 0, program, 0, 4);
        Array.Copy(code, 0, program, 4, code.Length);
        Array.Copy(internedTail, 0, program, newInternedStart, internedTail.Length);
        return program;
    }

    private static void EmitPushInt(List<byte> code, int value)
    {
        code.Add(OpCodes.PUSH);
        code.Add(TypeCodes.INT);
        code.AddRange(BitConverter.GetBytes(value));
    }

    /// <summary>
    /// Halts execution by jumping past program.Length. Mirrors how the compiler
    /// handles an `end` statement (CompileEnd in Compiler.cs).
    /// </summary>
    private static void EmitHalt(List<byte> code)
    {
        EmitPushInt(code, int.MaxValue);
        code.Add(OpCodes.JUMP);
    }

    [Test]
    public void Runto_HitsTarget_YieldsBack()
    {
        // Layout (byte addresses, code starts at 4):
        //   4: NOOP                  program "label"
        //   5: RUNTO_YIELD           target_addr if matched = 6
        //   6: NOOP                  post-yield program resume
        //   7: EXPLODE               sentinel: must not reach
        //   8: PUSH 6                test entry: target = 6
        //  14: RUNTO                 test_resume_ip = 15
        //  15: NOOP                  test resumes here after yield
        //  16: PUSH int.MaxValue
        //  22: JUMP                  halt
        var code = new List<byte>();
        code.Add(OpCodes.NOOP);                  // 4
        code.Add(OpCodes.RUNTO_YIELD);           // 5
        code.Add(OpCodes.NOOP);                  // 6
        code.Add(OpCodes.EXPLODE);               // 7
        EmitPushInt(code, 6);                    // 8-13
        code.Add(OpCodes.RUNTO);                 // 14
        code.Add(OpCodes.NOOP);                  // 15
        EmitHalt(code);                          // 16-22

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program, entryPointAddress: 8);

        vm.Execute().MoveNext();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
        Assert.That(vm.programResumeIP, Is.EqualTo(6));
    }

    [Test]
    public void Runto_PassThroughNonMatchingLabel_NoYield()
    {
        // First yield at addr 5 has post-ip = 6 (doesn't match target 8).
        // Second yield at addr 7 has post-ip = 8 (matches target 8). Yields.
        var code = new List<byte>();
        code.Add(OpCodes.NOOP);                  // 4
        code.Add(OpCodes.RUNTO_YIELD);           // 5
        code.Add(OpCodes.NOOP);                  // 6
        code.Add(OpCodes.RUNTO_YIELD);           // 7
        code.Add(OpCodes.NOOP);                  // 8
        code.Add(OpCodes.EXPLODE);               // 9
        EmitPushInt(code, 8);                    // 10-15
        code.Add(OpCodes.RUNTO);                 // 16
        code.Add(OpCodes.NOOP);                  // 17
        EmitHalt(code);                          // 18-24

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program, entryPointAddress: 10);

        vm.Execute().MoveNext();

        Assert.That(vm.programResumeIP, Is.EqualTo(8));
        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }

    [Test]
    public void Runto_EmptyRuntoStack_NoYield()
    {
        // RUNTO_YIELD with empty runtoStack falls through. No exception.
        var code = new List<byte>();
        code.Add(OpCodes.RUNTO_YIELD);           // 4
        code.Add(OpCodes.NOOP);                  // 5
        EmitHalt(code);                          // 6-12

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program);

        vm.Execute().MoveNext();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }

    [Test]
    public void Runto_DefaultEntryPoint_PreservesRunBehavior()
    {
        // A normally-compiled program runs unchanged with the new constructor.
        var src = "x = 42\n";
        var lex = new Lexer().TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lex.stream, TestCommands.CommandsForTesting);
        var prog = parser.ParseProgram();
        var compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions());
        compiler.Compile(prog);

        var vm = new VirtualMachine(compiler.Program);
        vm.Execute().MoveNext();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
        Assert.That(vm.programResumeIP, Is.EqualTo(4));
    }

    [Test]
    public void Runto_CustomEntryPoint_StartsThere()
    {
        // EXPLODE at addr 4 (default entry); custom entry at 5 just halts.
        var code = new List<byte>();
        code.Add(OpCodes.EXPLODE);               // 4
        EmitHalt(code);                          // 5-11

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program, entryPointAddress: 5);

        Assert.DoesNotThrow(() => vm.Execute().MoveNext());
    }

    [Test]
    public void Runto_PushesFrameOntoRuntoStack()
    {
        // Target an address that has no RUNTO_YIELD. The frame should remain on
        // the stack after execution stops via halt at the end of the program area.
        // We need the program area to halt cleanly (no RUNTO_YIELD), so we put a
        // halt at addr 4 — but that'd terminate before the test runs. Instead,
        // the program area has a halt that fires when reached after RUNTO.
        //
        // Layout:
        //   4: PUSH int.MaxValue          program halt
        //  10: JUMP
        //  11: PUSH 100                   test entry: target = 100 (never matched)
        //  17: RUNTO                      test_resume_ip = 18
        //  18: <never reached because program halts before any yield>
        var code = new List<byte>();
        EmitHalt(code);                          // 4-10 (program halt)
        EmitPushInt(code, 100);                  // 11-16
        code.Add(OpCodes.RUNTO);                 // 17
        code.Add(OpCodes.NOOP);                  // 18 (unreachable but here for clarity)
        EmitHalt(code);                          // 19-25

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program, entryPointAddress: 11);

        vm.Execute().MoveNext();

        Assert.That(vm.runtoStack.Count, Is.EqualTo(1), "frame should remain on stack (target never hit)");
        var frame = vm.runtoStack.buffer[vm.runtoStack.ptr - 1];
        Assert.That(frame.targetAddr, Is.EqualTo(100));
        Assert.That(frame.testResumeIp, Is.EqualTo(18));
    }

    [Test]
    public void Runto_MultipleYields_ProgramResumesFromSavedIP()
    {
        // Two runtos against two yields. Second runto must resume from the IP
        // saved during the first yield, not from __main entry.
        //
        //   4: NOOP                       label A
        //   5: RUNTO_YIELD                target if matched = 6
        //   6: NOOP                       label B
        //   7: RUNTO_YIELD                target if matched = 8
        //   8: NOOP                       program-end resume
        //   9: EXPLODE                    sentinel
        //  10: PUSH 6                     test: first runto target
        //  16: RUNTO                      test_resume_ip = 17
        //  17: PUSH 8                     test: second runto target
        //  23: RUNTO                      test_resume_ip = 24
        //  24: NOOP
        //  25: PUSH int.MaxValue
        //  31: JUMP
        var code = new List<byte>();
        code.Add(OpCodes.NOOP);                  // 4
        code.Add(OpCodes.RUNTO_YIELD);           // 5
        code.Add(OpCodes.NOOP);                  // 6
        code.Add(OpCodes.RUNTO_YIELD);           // 7
        code.Add(OpCodes.NOOP);                  // 8
        code.Add(OpCodes.EXPLODE);               // 9
        EmitPushInt(code, 6);                    // 10-15
        code.Add(OpCodes.RUNTO);                 // 16
        EmitPushInt(code, 8);                    // 17-22
        code.Add(OpCodes.RUNTO);                 // 23
        code.Add(OpCodes.NOOP);                  // 24
        EmitHalt(code);                          // 25-31

        var program = BuildProgram(code.ToArray());
        var vm = new VirtualMachine(program, entryPointAddress: 10);

        vm.Execute().MoveNext();

        Assert.That(vm.programResumeIP, Is.EqualTo(8));
        Assert.That(vm.runtoStack.Count, Is.EqualTo(0));
    }
}
