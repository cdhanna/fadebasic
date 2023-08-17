using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using DarkBasicYo;
using DarkBasicYo.Virtual;
using NUnit.Framework;

namespace Tests
{
    public class CallPlayground
    {

        [Test]
        public void Call()
        {
            var vm = new VirtualMachine(new byte[] { 99 });

            // mock out some byte code...
            var x = BitConverter.GetBytes(33);
            VmUtil.PushSpan(ref vm.stack, new ReadOnlySpan<byte>(x), TypeCodes.INT);


            // var commands = new DarkBasicCommands();
            // commands.Command

            // var caller = new DarkBasicCommandUtil.Autogenerated.GeneratedDarkBasicCommands();
            // var didRun = caller.TryRun(vm, 1);
            // var y = new DarkBasicCommandUtil.Autogenerated.GeneratedDarkBasicCommands();
            // y.x = 3;
            // DarkBasicCommandUtil.Autogenerated.GeneratedDarkBasicCommands.Call2_Ana(vm);

        }


        public List<Action<VirtualMachine>> GetMethods()
        {
            return new List<Action<VirtualMachine>>
            {
                Call_Hats_Add
            };
        }

        public void Call_Hats_Add(VirtualMachine vm)
        {

            // TODO: use a source generator????
            VmUtil.ReadSpan(ref vm.stack, out var tc, out var value);
            var a = BitConverter.ToInt32(value);

            VmUtil.ReadSpan(ref vm.stack, out var tc2, out var value2);
            var b = BitConverter.ToInt32(value2);

            var result = Hats.Add(a, b);
            var bits = BitConverter.GetBytes(result);
            vm.stack.PushSpanAndType(bits, 0, TypeCodes.GetByteSize(0));
        }

    }

    public class Hats
    {
        // load up 2 bytes? and put one on afterwards
        public static int Add(int a, int b) => a + b;
    }
}