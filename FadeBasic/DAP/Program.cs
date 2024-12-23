// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using DAP;
using FadeBasic.ApplicationSupport.Project;


DAPLogger.TryCreateLogger(out var logger);
logger.Log("initializing...");

if (DAPEnv.WaitForDebugger)
{
    logger.Log("waiting for C# debugger...");
    while (!Debugger.IsAttached)
    {
        Thread.Sleep(100);
    }
}

ProjectLoader.Initialize();
var adapter = new FadeDebugAdapter(logger, Console.OpenStandardInput(), Console.OpenStandardOutput());

try
{
    adapter.Run();
}
catch (Exception ex)
{
    throw;
}