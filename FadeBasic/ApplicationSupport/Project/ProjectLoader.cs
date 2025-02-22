using FadeBasic.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Locator;

namespace FadeBasic.ApplicationSupport.Project;

public static class ProjectLoader
{
    public static void Initialize()
    {
        var instance = MSBuildLocator.RegisterDefaults();
    }

    
    public static ProjectContext LoadCsProject(string csProjPath)
    {
        var projectCollection = new ProjectCollection();
        var context = new ProjectContext();
        
        var csProj = projectCollection.LoadProject(csProjPath);
        var csProjDir = Path.GetDirectoryName(csProjPath);

        // csProj.Build(new List<ILogger>() { new MsbuildLog() });
        var objDir = csProj.GetPropertyValue("BaseIntermediateOutputPath");
        var targetFramework = csProj.GetPropertyValue("TargetFramework");
        var fullObjDir = Path.Combine(csProjDir, objDir);

        var assetsFile = Path.Combine(fullObjDir, "project.assets.json").Replace("\\", "/");
        if (!File.Exists(assetsFile))
        {
            throw new Exception($"Run a `dotnet restore`. No assets file found. csprojDir=[{csProjDir}] outDir=[{objDir}] supposed-asset-file=[{assetsFile}]");
        }
        
        var assetsJson = File.ReadAllText(assetsFile);
        var assetsData = Jsonable2.Parse(assetsJson);

        if (!assetsData.objects.TryGetValue("libraries", out var assetLibraryData))
        {
            throw new Exception("Run a `dotnet restore`. no libraries field found.");
        }
        if (!assetsData.objects.TryGetValue("targets", out var assetTargetsData))
        {
            throw new Exception("Run a `dotnet restore`. no targets field found.");
        }
        if (!assetTargetsData.objects.TryGetValue(targetFramework, out var targetData))
        {
            throw new Exception("Run a `dotnet restore`. no target for framework found.");
        }

        
        var outDir = csProj.GetPropertyValue("OutDir");
        var sourceItems = csProj.GetItems("FadeSource");
        foreach (var sourceItem in sourceItems)
        {
            var sourcePath = sourceItem.GetMetadataValue("FullPath");
            context.absoluteSourceFiles.Add(sourcePath);
        }

        // var references = csProj.GetItems("ReferencePath");
        // var refMap = new Dictionary<string, string>();
        // foreach (var reference in references)
        // {
        //     var assembly = reference.GetMetadataValue("FileName");
        //     var dllPath = reference.GetMetadataValue("FullPath");
        //     refMap[assembly] = dllPath;
        // }


        
        // var instance = csProj.CreateProjectInstance();
        // instance.Build(new ILogger[]{new MsbuildLog()});
        // var items = instance.GetItems("ReferencePath");
        //
        //
        
        var commands = csProj.GetItems("FadeCommand");
        var refMap = new Dictionary<string, string>(); // Identity --> dllPath
        var libMap = new Dictionary<string, List<string>>(); // dllPath to list of class names
        foreach (var command in commands)
        {
            var fullClassName = command.GetMetadataValue("FullName");
            var referenceName = command.GetMetadataValue("Identity");

            if (!refMap.TryGetValue(referenceName, out var expectedDllPath))
            {
                var searchingForLibrary = true;
                foreach (var kvp in targetData.objects)
                {
                    if (kvp.Key.StartsWith(referenceName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // TODO: Assume that it is a nuget package, but in the future, maybe there is an option that specifies the PRojectReference
                        // TODO: how do we know the version is correct? 
                        searchingForLibrary = false;

                        var _ = kvp.Value.strings["type"];
                        var runtimeDlls = kvp.Value.objects["runtime"];
                        var dllPackagePath = runtimeDlls.objects.FirstOrDefault().Key;

                        var nugetPath = assetLibraryData.objects[kvp.Key].strings["path"];

                        var packagesPath = assetsData.objects["project"].objects["restore"].strings["packagesPath"];

                        refMap[referenceName] = expectedDllPath = Path.Combine(packagesPath, nugetPath, dllPackagePath);
                        break;
                        // refMap[referenceName] = existingDllPath = 
                    }
                }

                if (searchingForLibrary)
                {
                    throw new Exception($"Run a `dotnet restore`. unable to resolve dll for {referenceName}, {fullClassName}.");
                }
            }
            
        
            if (!libMap.TryGetValue(expectedDllPath, out var commandClasses))
            {
                commandClasses = libMap[expectedDllPath] = new List<string>();
            }
            commandClasses.Add(fullClassName);
        }

        foreach (var kvp in libMap)
        {
            var source = new ProjectCommandSource
            {
                absoluteOutputDllPath = kvp.Key,
                commandClasses = kvp.Value
            };
            context.projectLibraries.Add(source);
        }


        return context;
    }
    
    public class MsbuildLog : ILogger
    {
        public void Initialize(IEventSource eventSource)
        {
            eventSource.MessageRaised += (sender, args) =>
            {
                Console.WriteLine("MESSAGE: " + args.Message);
            };
            eventSource.ErrorRaised += (sender, args) =>
            {
                Console.WriteLine("ERR: " + args.Message);
            };
            eventSource.AnyEventRaised += (sender, args) =>
            {
                Console.WriteLine("ANY: " + args.Message);
            };
        }

        public void Shutdown()
        {
        }

        public LoggerVerbosity Verbosity { get; set; }
        public string? Parameters { get; set; }
    }
    
}