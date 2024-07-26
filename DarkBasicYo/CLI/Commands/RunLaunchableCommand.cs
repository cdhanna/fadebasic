using System.CommandLine;
using System.Diagnostics;
using ApplicationSupport.Launch;
using CliWrap;
using DarkBasicYo.ApplicationSupport.Project;
using DarkBasicYo.Util;
using Microsoft.Build.Evaluation;
using Serilog;
using Command = System.CommandLine.Command;

namespace DarkBasicYo.Commands;

public static class RunLaunchableCommand
{
    public static void Bind(RootCommand root)
    {
        var command = new Command("run", "compile and run a project");

        var projArg = new Argument<string>("projectPath", "the path to the project yaml file");
        projArg.SetDefaultValue(".");

        var generateFlag = new Option<bool>("--no-generate", "do not compile and regenerate the project");
        generateFlag.AddAlias("-ng");
        
        command.AddArgument(projArg);
        command.AddOption(generateFlag);
        
        command.SetHandler(ctx =>
        {
            var projPath = ctx.ParseResult.GetValueForArgument(projArg);
            var noGenerate = ctx.ParseResult.GetValueForOption(generateFlag);
            if (!PathUtil.TryGetProjectPath(projPath, out projPath))
            {
                Log.Error("Cannot find a valid project");
                return;
            }
            
            Log.Debug($"found project=[{projPath}]");
            var projectContext = ProjectLoader.LoadProjectFromFile(projPath);

            if (!noGenerate)
            {
                if (!LaunchableGenerator.TryGenerateLaunchable(projectContext))
                {
                    Log.Error("Unable to write file");
                    return;
                }
            }
            
            // run the project...
            var collection = new ProjectCollection();
            var launchProject = collection.LoadProject(projectContext.absoluteLaunchCsProjPath);
            // TODO: validate that all the dependencies are correct...

            var projDirectory = Path.GetDirectoryName(projectContext.absoluteLaunchCsProjPath);

            Process p = new Process();
            p.StartInfo.UseShellExecute = true;
            p.StartInfo.FileName = "dotnet";
            p.StartInfo.Arguments = $"run --project {projDirectory}";
            p.Start();

            p.WaitForExit();
            // Cli.Wrap("dotnet")
            //     .WithArguments($"--project {projDirectory} ")
            //     .;

        });
        root.AddCommand(command);
    }
}