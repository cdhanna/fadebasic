﻿// See https://aka.ms/new-console-template for more information

using System;
using System.IO.Pipes;
using System.Threading.Tasks;
using LSP.Handlers;
using LSP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var pipeAddr = "";
        foreach (var t in args)
        {
            if (!t.StartsWith("--pipe=")) continue;
            
            pipeAddr = t.Substring("--pipe=".Length);
            break;
        }
        
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
            .MinimumLevel.Information()
            .CreateLogger();
        
        var pipeClient = new NamedPipeClientStream(pipeAddr);
        await pipeClient.ConnectAsync();
        
        Log.Logger.Information("connected.");

        try
        {
            var server = await LanguageServer.From(options =>
                {
                    options.OnUnhandledException = ex =>
                    {
                        Log.Logger.Information("ERROR YO!!!!" + ex.GetType().Name + " : " + ex.Message + " :: " + ex.StackTrace);
                    };
                    options
                        .WithInput(pipeClient.UsePipeReader())
                        .WithOutput(pipeClient.UsePipeWriter())
                        .ConfigureLogging(
                            x => x
                                .AddSerilog(Log.Logger)
                                .AddLanguageProtocolLogging()
                                .SetMinimumLevel(LogLevel.Information)
                        )
                        .AddDefaultLoggingProvider()
                        .WithServices(ConfigureServices)
                        .WithServices(x => x.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace)))
                        .OnInitialize((languageServer, request, token) =>
                        {
                            Log.Logger.Information("fuck this");

                            // var foo = languageServer.Services.GetService<Foo>();
                            // foo.SayFoo();
                            return Task.CompletedTask;
                        })

                        .WithHandler<TextDocumentSyncHandler>()
                        // .WithHandler<FoldingRangeHandler>()
                        // .WithHandler<DocumentSymbolHandler>()
                        .WithHandler<SemanticTokenHandler>()
                        // .WithHandler<DiagnosticsHandler>()
                        .OnStarted((languageServer, token) =>
                        {
                            Log.Logger.Information("fuck this too");

                            var foo = languageServer.Services.GetService<Foo>();
                            foo.SayFoo();

                            languageServer.Workspace.SendNotification("Derp");

                            return Task.CompletedTask;
                        });
                }
            ).ConfigureAwait(false);

            await server.WaitForExit.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal("Game over: " + ex.GetType().Name + " " + ex.Message + "\n" + ex.StackTrace);
        }
    }

    static void ConfigureServices(IServiceCollection services)
    {
        // Console.WriteLine("configuring services");
        services.AddSingleton<Foo>();
    }
}