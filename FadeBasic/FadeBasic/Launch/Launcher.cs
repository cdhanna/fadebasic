using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public static class Launcher
    {
        public static void Run<T>() 
            where T : ILaunchable, new()
        {
            Run<T>(new T());
        }
        
        public static void Run<T>(T instance)
            where T : ILaunchable
        {
            var vm = new VirtualMachine(instance.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection)
            };
            vm.Execute2(0); // 0 means run until suspend. 
        }
    }
}