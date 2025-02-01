using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using FadeBasic;
using FadeBasic.Virtual;
using NUnit.Framework;

namespace Tests
{
    public class CallPlayground
    {
        
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