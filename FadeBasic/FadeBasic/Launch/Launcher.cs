using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FadeBasic.Virtual;

namespace FadeBasic.Launch
{
    public class LaunchOptions
    {
        public const string ENV_ENABLE_DEBUG = "FADE_BASIC_DEBUG";
        public const string ENV_DEBUG_PORT = "FADE_BASIC_DEBUG_PORT";
        
        
        public bool debug;
        public int debugPort = 0;
        public bool debugWaitForConnection = true;

        public static readonly LaunchOptions DefaultOptions;
        static LaunchOptions()
        {
            var debugEnv = Environment.GetEnvironmentVariable(ENV_ENABLE_DEBUG)?.ToLowerInvariant();
            DefaultOptions = new LaunchOptions
            {
                debug = debugEnv == "true" || debugEnv == "1",
                debugPort = 0,
                debugWaitForConnection = true,
            };

            if (!int.TryParse(Environment.GetEnvironmentVariable(ENV_DEBUG_PORT), out DefaultOptions.debugPort))
            {
                DefaultOptions.debugPort = LaunchUtil.FreeTcpPort();
            }

        }

    }
    
    public static class Launcher
    {
        // public static bool IsDebugMode => Environment.GetEnvironmentVariable("FADE_BASIC_DEBUG")
        //
        
        public static void Run<T>(LaunchOptions options=null) 
            where T : ILaunchable, new()
        {
            Run<T>(new T(), options);
        }
        
        public static void Run<T>(T instance, LaunchOptions options=null)
            where T : ILaunchable
        {
            options ??= LaunchOptions.DefaultOptions;
            
            var vm = new VirtualMachine(instance.Bytecode)
            {
                hostMethods = HostMethodTable.FromCommandCollection(instance.CommandCollection)
            };
            
            if (!options.debug)
            {
                vm.Execute2(0); // 0 means run until suspend. 
            }
            else
            {
                // vm.Execute2(0); // 0 means run until suspend. 

               
                var session = new DebugSession(vm, instance.DebugData, options);
                session.StartServer();
                session.StartDebugging(); // needs infinite budget. 
                
                
                // session.StartServer();
            }
            
        }
    }
}