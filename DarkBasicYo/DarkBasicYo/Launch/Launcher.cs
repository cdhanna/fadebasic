using DarkBasicYo.Virtual;

namespace DarkBasicYo.Launch
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
            //bytecode: 1, 0, 1, 0, 0, 0, 9, 0, 7, 0, 1, 10, 0, 1, 0, 1, 0, 0, 0, 14

            vm.Execute2(0); // 0 means run until suspend. 
        }
    }
}