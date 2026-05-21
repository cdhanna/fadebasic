using System.Collections.Generic;
using FadeBasic;
using FadeBasic.Lib.Web;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using NUnit.Framework;

namespace Tests
{
    // Validates the cooperative-pump architecture end-to-end:
    //   1. WebCommands.Prompt fires HostBridge.PostMessage with the
    //      right channel + payload (so plugin authors can rely on the
    //      contract).
    //   2. HostBridge.SuspendVm pauses execution mid-program.
    //   3. After a deposit, the next tick resumes and the deposited
    //      value lands in the destination variable (proving the
    //      placeholder swap works end-to-end).
    //
    // These tests don't need a browser or Export.Web — they exercise
    // the platform-neutral parts of the model (HostBridge in core,
    // HostStackOps in core, WebCommands in Lib.Web).
    [TestFixture]
    public class CooperativePumpTests
    {
        [SetUp]
        public void ResetBridge()
        {
            // HostBridge slots are static. Reset between tests so a
            // failure in one doesn't bleed into the next.
            HostBridge.PostMessage = null;
            HostBridge.SuspendVm = null;
            // The print buffer is a static StringBuilder on WebCommands;
            // drain anything left over from a previous test.
            WebCommands.DrainPrintBuffer();
        }

        private static FadeRuntimeContext Compile(string src)
        {
            var commands = new CommandCollection(new WebCommands());
            var ok = Fade.TryCreateFromString(src, commands, out var ctx, out var errors);
            Assert.That(ok, Is.True,
                "compile failed: " + (errors == null ? "(null)" : errors.ToDisplay()));
            return ctx;
        }

        [Test]
        public void Prompt_FiresPostMessage_WithRightChannelAndPayload()
        {
            var channels = new List<string>();
            var payloads = new List<string>();
            HostBridge.PostMessage = (ch, p) => { channels.Add(ch); payloads.Add(p); };
            HostBridge.SuspendVm = () => { /* don't suspend — let it run to end */ };

            var ctx = Compile("y$ = prompt$(\"what is your name?\")");
            ctx.Machine.Execute3(0);

            Assert.That(channels, Is.EqualTo(new[] { "fade-web/prompt" }));
            Assert.That(payloads, Is.EqualTo(new[] { "what is your name?" }));
        }

        [Test]
        public void Prompt_NoHostInstalled_ReturnsEmptyAndContinues()
        {
            // No HostBridge handlers — Prompt should still safely return
            // and the program should continue with y$ = "" (the
            // placeholder that the source-generated executor pushes).
            var ctx = Compile(@"
y$ = prompt$(""?"")
print y$
print ""after""
");
            ctx.Machine.Execute3(0);

            var printed = WebCommands.DrainPrintBuffer().Replace("\r\n", "\n");
            // First print is the empty placeholder (one blank line),
            // second print is the literal "after".
            Assert.That(printed, Is.EqualTo("\nafter\n"));
        }

        [Test]
        public void SuspendVm_PausesExecution_BeforeFinishingProgram()
        {
            VirtualMachine vm = null;
            var suspendCount = 0;
            HostBridge.PostMessage = (_, __) => { };
            HostBridge.SuspendVm = () => { suspendCount++; vm?.Suspend(); };

            var ctx = Compile(@"
y$ = prompt$(""?"")
print y$
");
            vm = ctx.Machine;
            vm.Execute3(0);

            Assert.That(suspendCount, Is.EqualTo(1),
                "SuspendVm should fire exactly once during the prompt$ call");
            Assert.That(vm.instructionIndex, Is.LessThan(vm.program.Length),
                "VM should have stopped mid-program (didn't reach the print)");
            Assert.That(vm.isSuspendRequested, Is.True);
        }

        [Test]
        public void Pump_Suspend_DepositString_Resume_AssignsRealAnswer()
        {
            // The headline test: drive the cooperative pump exactly the
            // way FadeBridge does in production. After deposit, the
            // program should see "Chris", not the empty placeholder.
            VirtualMachine vm = null;
            HostBridge.PostMessage = (_, __) => { };
            HostBridge.SuspendVm = () => vm?.Suspend();

            var ctx = Compile(@"
y$ = prompt$(""?"")
print y$
");
            vm = ctx.Machine;

            // Tick 1: runs until prompt$ → HostBridge.SuspendVm → exit.
            vm.Execute3(0);
            Assert.That(vm.instructionIndex, Is.LessThan(vm.program.Length),
                "tick 1 should have suspended, not completed");

            // Deposit the answer onto the operand stack. This is the
            // same call FadeBridge.DepositResultString makes after the
            // page replies — exercising the shared HostStackOps helper.
            Assert.That(HostStackOps.SwapTopString(vm, "Chris"), Is.True);

            // Drain anything the executor may have streamed already
            // (shouldn't be anything — print fires AFTER the deposit).
            WebCommands.DrainPrintBuffer();

            // Tick 2: should run to completion now, printing "Chris".
            vm.Execute3(0);
            Assert.That(vm.instructionIndex, Is.GreaterThanOrEqualTo(vm.program.Length),
                "tick 2 should have completed the program");

            var printed = WebCommands.DrainPrintBuffer().Replace("\r\n", "\n");
            Assert.That(printed.TrimEnd('\n'), Is.EqualTo("Chris"),
                "expected the deposited answer to be what `print y$` saw");
        }

        [Test]
        public void Pump_MultiplePrompts_DepositEachInTurn()
        {
            // Two prompts in sequence: each should suspend, get a
            // separate deposit, and the program should see both answers.
            VirtualMachine vm = null;
            HostBridge.PostMessage = (_, __) => { };
            HostBridge.SuspendVm = () => vm?.Suspend();

            var ctx = Compile(@"
a$ = prompt$(""first?"")
b$ = prompt$(""second?"")
print a$
print b$
");
            vm = ctx.Machine;

            vm.Execute3(0);
            Assert.That(HostStackOps.SwapTopString(vm, "alpha"), Is.True);

            vm.Execute3(0);
            Assert.That(HostStackOps.SwapTopString(vm, "beta"), Is.True);

            WebCommands.DrainPrintBuffer();
            vm.Execute3(0);

            var printed = WebCommands.DrainPrintBuffer().Replace("\r\n", "\n");
            Assert.That(printed, Is.EqualTo("alpha\nbeta\n"));
        }

        [Test]
        public void SwapTopPrimitive_Int_OverwritesPlaceholder()
        {
            // Direct unit test of the primitive-swap path. Use a real
            // (trivially compiled) VM so the constructor's interned-data
            // setup succeeds — the actual program body doesn't matter,
            // we just need a valid FastStack to mutate.
            var vm = TinyVm();
            VmUtil.PushSpan(ref vm.stack, System.BitConverter.GetBytes(0), TypeCodes.INT);

            Assert.That(HostStackOps.SwapTopPrimitive(vm, TypeCodes.INT,
                System.BitConverter.GetBytes(42)), Is.True);

            VmUtil.ReadAsInt(ref vm.stack, out var read);
            Assert.That(read, Is.EqualTo(42));
            Assert.That(vm.stack.ptr, Is.EqualTo(0),
                "swap shouldn't change the stack depth");
        }

        [Test]
        public void SwapTopPrimitive_WrongSize_Refuses()
        {
            var vm = TinyVm();
            VmUtil.PushSpan(ref vm.stack, System.BitConverter.GetBytes(0), TypeCodes.INT);

            // INT is 4 bytes; passing 8 should be rejected and the
            // stack should be untouched.
            var before = vm.stack.ptr;
            var ok = HostStackOps.SwapTopPrimitive(vm, TypeCodes.INT,
                System.BitConverter.GetBytes(0L));
            Assert.That(ok, Is.False);
            Assert.That(vm.stack.ptr, Is.EqualTo(before));
        }

        // Tiniest valid VM we can build: compile an empty program with
        // the WebCommands command set. The VM constructor expects a
        // properly-formatted bytecode blob (with the interned-data
        // section the compiler always emits); building a stub by hand
        // is fragile, so use the real compiler.
        private static VirtualMachine TinyVm()
        {
            var ctx = Compile("x = 1");
            return ctx.Machine;
        }
    }
}
