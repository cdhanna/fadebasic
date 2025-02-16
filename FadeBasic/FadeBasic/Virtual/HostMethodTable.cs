namespace FadeBasic.Virtual
{
    public struct HostMethodTable
    {
        public CommandInfo[] methods;

        public void FindMethod(int methodAddr, out CommandInfo method)
        {
            method = methods[methodAddr];
        }

        public static HostMethodTable FromCommandCollection(CommandCollection collection)
        {
            var methods = new CommandInfo[collection.Commands.Count];
            for (var i = 0; i < methods.Length; i++)
            {
                methods[i] = collection.Commands[i];
            }

            return new HostMethodTable
            {
                methods = methods
            };
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