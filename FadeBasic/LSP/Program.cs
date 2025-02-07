// See https://aka.ms/new-console-template for more information

using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic.ApplicationSupport.Project;
using LSP.Handlers;
using LSP.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Server;
using Serilog;

public static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            var connectionCts = new CancellationTokenSource();
            var pipeAddr = "";
            var isStdio = false; // doesn't DO anything yet.
            foreach (var t in args)
            {
                if (t.StartsWith("--stdio")) isStdio = true;
                if (!t.StartsWith("--pipe=")) continue;

                pipeAddr = t.Substring("--pipe=".Length);
                break;
            }

            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                // .WriteTo.Console()
                .MinimumLevel.Information()
                .CreateLogger();

            Log.Logger.Information($"connecting... pipeAddr=[{pipeAddr}]");
            for (var i = 0 ; i < args.Length; i ++)
            {
                Log.Logger.Information($" arg {i}=[{args[i]}]");
            }

            NamedPipeClientStream pipeClient = null;

            if (!string.IsNullOrEmpty(pipeAddr))
            {
                pipeClient = new NamedPipeClientStream(pipeAddr);
                Log.Logger.Information(" created pipe client");
                try
                {
                    await pipeClient.ConnectAsync(TimeSpan.FromSeconds(10), connectionCts.Token);
                }
                catch (Exception ex)
                {
                    Log.Logger.Error($"failed to connect to pipe in time. message=[{ex.Message}]");
                    throw;
                }
            }

            Log.Logger.Information(" connected.");

            try
            {
                ProjectLoader.Initialize(); // initialize msbuild.
                Log.Logger.Information("Creating server");

                var server = await LanguageServer.From(options =>
                    {
                        options.OnUnhandledException = ex =>
                        {
                            Log.Logger.Information("ERROR YO!!!!" + ex.GetType().Name + " : " + ex.Message +
                                                   " :: " + ex.StackTrace);
                        };
                        if (pipeClient != null)
                        {
                            options = options
                                .WithInput(pipeClient.UsePipeReader())
                                .WithOutput(pipeClient.UsePipeWriter());
                        }
                        else
                        {
                            options = options
                                .WithInput(Console.OpenStandardInput())
                                .WithOutput(Console.OpenStandardOutput());
                        }

                        options
                                
                            
                            .WithConfigurationSection("conf.language.fade")
                            .ConfigureLogging(
                                x => x
                                    .AddSerilog(Log.Logger)
                                    .AddLanguageProtocolLogging()
                                    .SetMinimumLevel(LogLevel.Information)
                            )
                            .AddDefaultLoggingProvider()
                            .WithServices(ConfigureServices)
                            .WithServices(x => x.AddLogging(b =>
                            {
                                b.SetMinimumLevel(LogLevel.Trace);
                                b.AddSerilog(Log.Logger);
                                b.AddLanguageProtocolLogging();
                            }))
                            .OnInitialize((languageServer, request, token) =>
                            {
                                var docService = languageServer.Services.GetService<DocumentService>();
                                var projectService = languageServer.Services.GetService<ProjectService>();
                                docService.Populate(request.RootUri);

                                foreach (var (uri, text) in docService.AllProjects())
                                {
                                    projectService.LoadProject(uri);
                                }

                                // TODO: populate
                                // var foo = languageServer.Services.GetService<Foo>();
                                // foo.SayFoo();
                                return Task.CompletedTask;
                            })

                            .WithHandler<TextDocumentSyncHandler>()
                            .WithHandler<ProjectTextDocumentSyncHandler>()
                            .WithHandler<FormattingHandler>()
                            .WithHandler<FormattingWhenTypingHandler>()
                            .WithHandler<FormattingRangeHandler>()
                            .WithHandler<GotoDefinitionHandler>()
                            .WithHandler<FindReferencesHandler>()
                            .WithHandler<HoverHandler>()
                            // .WithHandler<FoldingRangeHandler>()
                            // .WithHandler<DocumentSymbolHandler>()
                            .WithHandler<SemanticTokenHandler>()
                            // .WithHandler<DiagnosticsHandler>()
                            .OnStarted((languageServer, token) =>
                            {
                                languageServer.Workspace.SendNotification("Derp");
                                return Task.CompletedTask;
                            });
                    }
                ).ConfigureAwait(false);

                Log.Logger.Information(" starting server");
                await server.WaitForExit.ConfigureAwait(false);
                Log.Logger.Information(" started server");
            }
            catch (Exception ex)
            {
                Log.Logger.Fatal("Game over: " + ex.GetType().Name + " " + ex.Message + "\n" + ex.StackTrace);
            }
        }
        catch (Exception ex)
        {
            Log.Logger.Fatal($"LSP failed. type=[{ex.GetType().Name}] message=[{ex.Message}] stack=[{ex.StackTrace}]");
        }
    }

    static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<DocumentService>();
        services.AddSingleton<CompilerService>();
        services.AddSingleton<ProjectService>();
    }
}