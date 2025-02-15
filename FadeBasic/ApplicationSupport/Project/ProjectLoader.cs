using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Locator;

namespace FadeBasic.ApplicationSupport.Project;

public static class ProjectLoader
{
    public static void Initialize()
    {
        // var x = typeof(NuGetFramework);
        var instance = MSBuildLocator.RegisterDefaults();
        // var vsi = MSBuildLocator.QueryVisualStudioInstances().First();
        // var alc = new AssemblyLoadContext("MSBuild");
        // AssemblyLoadContext.Default.Resolving += (assemblyLoadContext, assemblyName) =>
        // {
        //     try
        //     {
        //         string path = Path.Combine(vsi.MSBuildPath, assemblyName.Name + ".dll");
        //         if (path.Contains("Nuget"))
        //         {
        //             
        //         }
        //         if (File.Exists(path))
        //         {
        //             return alc.LoadFromAssemblyPath(path);
        //         }
        //
        //         return null;
        //     }
        //     catch (Exception ex)
        //     {
        //         Console.WriteLine(ex.Message);
        //         throw;
        //     }
        // };
        // MSBuildLocator.RegisterInstance(vsi);
    }
    
    public static ProjectContext LoadCsProject(string csProjPath)
    {
        var projectCollection = new ProjectCollection();
        var context = new ProjectContext();
        
        var csProj = projectCollection.LoadProject(csProjPath);
       
        // csProj.Build(new List<ILogger>() { new MsbuildLog() });
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
        var libMap = new Dictionary<string, List<string>>(); // dllPath to list of class names
        foreach (var command in commands)
        {
            var fullClassName = command.GetMetadataValue("FullName");
            var referenceName = command.GetMetadataValue("Identity");

            var csProjDir = Path.GetDirectoryName(csProjPath);
            var expectedDllPath = Path.Join(csProjDir, outDir, referenceName + ".dll");
            expectedDllPath = expectedDllPath.Replace("\\", "/");
            if (!File.Exists(expectedDllPath))
            {
                continue;
            }///Users/chrishanna/Documents/SillyConsumerTest/Demo/bin/Debug/net8.0
             // 
            
            // if (!refMap.TryGetValue(referenceName, out var dllPath))
            // {
            //     throw new InvalidOperationException($"No dll found for {referenceName}");
            // }
        
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