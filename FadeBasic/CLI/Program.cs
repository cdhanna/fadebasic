// See https://aka.ms/new-console-template for more information

using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json.Serialization;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using Newtonsoft.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using JsonConverter = Newtonsoft.Json.JsonConverter;


var root = new RootCommand();
var builder = new CommandLineBuilder(root);

var logOption = new Option<string>("--log", getDefaultValue: () => "info");
logOption.AddAlias("--logs");
logOption.AddAlias("-l");
root.AddGlobalOption(logOption);


builder.AddMiddleware(context =>
{
    { // set up logging
                
        var levelSwitch = new LoggingLevelSwitch
        {
            MinimumLevel = ParseLogLevel(context.ParseResult.GetValueForOption(logOption))
        };

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.ControlledBy(levelSwitch)
            .CreateLogger();
    }

    { // set up services
        ProjectLoader.Initialize();
    }

    { // start services
        
    }

}, MiddlewareOrder.Configuration);



// var srcArg = new Argument<string>("srcPath", "path to dbp source code");
// srcArg.AddValidator(x =>
// {
//     var value = x.GetValueOrDefault<string>();
//     if (!File.Exists(value))
//     {
//         x.ErrorMessage = "file does not exist";
//     }
// });
//
// var outOption = new Option<string>("--output", "path to write output");
// outOption.AddAlias("-o");
//
// var compressedJsonOption = new Option<bool>("--compact", "when true, written json will be compact");
// compressedJsonOption.AddAlias("-c");
// compressedJsonOption.SetDefaultValue(false);
//
// root.AddGlobalOption(compressedJsonOption);



{
    // register commands
    // CompileToLaunchableCommand.Bind(root);
    // RunLaunchableCommand.Bind(root);
}

var app = builder
    .UseHelp()
    .UseDefaults()
    .Build();

await app.InvokeAsync(args);



static LogEventLevel ParseLogLevel(string? optionValue)
{
    var value = optionValue?.ToLowerInvariant().Trim()[0] ?? 'i';
    switch (value)
    {
        case 'v': return LogEventLevel.Verbose;
        case 'd': return LogEventLevel.Debug;
        case 'w': return LogEventLevel.Warning;
        case 'e': return LogEventLevel.Error;
        case 'f': return LogEventLevel.Fatal;
            
        default:
        case 'i': return LogEventLevel.Information;

    }
}
