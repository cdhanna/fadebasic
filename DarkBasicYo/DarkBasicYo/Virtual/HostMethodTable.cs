using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DarkBasicYo.Virtual
{
    public struct HostMethodTable
    {
        public CommandInfo[] methods;

        public void FindMethod(int methodAddr, out CommandInfo method)
        {
            method = methods[methodAddr];
        }
    }

    public static class HostMethodUtil
    {
        public static void Execute(CommandInfo method, VirtualMachine machine)
        {
            method.executor(machine);
        }
        
    }
}