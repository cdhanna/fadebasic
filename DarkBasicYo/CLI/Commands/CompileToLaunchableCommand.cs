using System.CommandLine;
using ApplicationSupport.Code;
using ApplicationSupport.Launch;
using DarkBasicYo.ApplicationSupport.Project;
using DarkBasicYo.Util;
using Serilog;

namespace DarkBasicYo.Commands;

public static class CompileToLaunchableCommand
{
    public static void Bind(RootCommand root)
    {
        var command = new Command("generate", "compile and run a project");

        var projArg = new Argument<string>("projectPath", "the path to the project yaml file");
        projArg.SetDefaultValue(".");
        
        command.AddArgument(projArg);
        command.SetHandler(ctx =>
        {
            var projPath = ctx.ParseResult.GetValueForArgument(projArg);
            if (!PathUtil.TryGetProjectPath(projPath, out projPath))
            {
                Log.Error("Cannot find a valid project");
                return;
            }
            
            Log.Debug($"found project=[{projPath}]");
            var projectContext = ProjectLoader.LoadProjectFromFile(projPath);

            if (!LaunchableGenerator.TryGenerateLaunchable(projectContext))
            {
                Log.Error("Unable to write file");
                return;
            }
        });
        root.AddCommand(command);
    }

}