﻿// See https://aka.ms/new-console-template for more information

using System.Diagnostics;
using DAP;
using FadeBasic.ApplicationSupport.Project;

Console.WriteLine("DAP!!");

var shouldWait = Environment.GetEnvironmentVariable("FADE_WAIT_FOR_DEBUG") == "true";
if (shouldWait)
{
    while (!Debugger.IsAttached)
    {
        Thread.Sleep(100);
    }
}

ProjectLoader.Initialize();
var adapter = new FadeDebugAdapter(Console.OpenStandardInput(), Console.OpenStandardOutput());

try
{
    adapter.Run();
}
catch (Exception ex)
{
    throw;
}

var x = 1;