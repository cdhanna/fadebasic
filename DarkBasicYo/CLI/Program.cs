// See https://aka.ms/new-console-template for more information

using System.CommandLine;
using System.Text.Json.Serialization;
using DarkBasicYo;
using Newtonsoft.Json;
using JsonConverter = Newtonsoft.Json.JsonConverter;


/*
dbp lex <src> <output>
dbp parse <src> <output>
dbp compile <src> <output>
dbp run <src>
*/

var root = new RootCommand();

var srcArg = new Argument<string>("srcPath", "path to dbp source code");
srcArg.AddValidator(x =>
{
    var value = x.GetValueOrDefault<string>();
    if (!File.Exists(value))
    {
        x.ErrorMessage = "file does not exist";
    }
});

var outOption = new Option<string>("--output", "path to write output");
outOption.AddAlias("-o");

var compressedJsonOption = new Option<bool>("--compact", "when true, written json will be compact");
compressedJsonOption.AddAlias("-c");
compressedJsonOption.SetDefaultValue(false);

root.AddGlobalOption(compressedJsonOption);

var lexCommand = new Command("lex", "tokenize dbp source code");
lexCommand.AddAlias("l");
lexCommand.AddArgument(srcArg);
lexCommand.AddOption(outOption);
lexCommand.SetHandler((srcPath, outPath, compact) =>
{
    var src = File.ReadAllText(srcPath);
    var dbp = new DarkBasic();
    var res = dbp.Tokenize(src);
    var json = JsonConvert.SerializeObject(res, compact ? Formatting.None : Formatting.Indented);

    if (string.IsNullOrEmpty(outPath))
    {
        Console.WriteLine(json);
    }
    else
    {
        File.WriteAllText(outPath + ".json", json);
    }
    
}, srcArg, outOption, compressedJsonOption);
root.AddCommand(lexCommand);


var parseCommand = new Command("parse", "parse dbp source code");
parseCommand.AddAlias("p");
parseCommand.AddArgument(srcArg);
parseCommand.AddOption(outOption);
parseCommand.SetHandler((srcPath, outPath, compact) =>
{
    var src = File.ReadAllText(srcPath);
    var dbp = new DarkBasic();
    var res = dbp.Parse(src);
    // var json = JsonConvert.SerializeObject(res, compact ? Formatting.None : Formatting.Indented);

    var json = res.ToString();
    if (string.IsNullOrEmpty(outPath))
    {
        Console.WriteLine(json);
    }
    else
    {
        File.WriteAllText(outPath + ".json", json);
    }
}, srcArg, outOption, compressedJsonOption);
root.AddCommand(parseCommand);

await root.InvokeAsync(args);


