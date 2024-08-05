using System.Collections.Generic;
using System.IO;
using ApplicationSupport.Code;
using ApplicationSupport.Launch;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Virtual;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace FadeBasic.Build
{
    
    //https://learn.microsoft.com/en-us/visualstudio/msbuild/tutorial-custom-task-code-generation?view=vs-2022
    public class FadeProjectTask : Task
    {
        [Required] public ITaskItem[] SourceFiles { get; set; }
        [Required] public ITaskItem[] Commands { get; set; }
        [Required] public ITaskItem[] References { get; set; }
        [Required] public string GeneratedClassName { get; set; }
        [Required] public string GenerateFileLocation { get; set; }

        [Output] public string GeneratedFile { get; set; }


        static Dictionary<string, string> PrepareAssemblyToDllMap(ITaskItem[] references)
        {
            var dict = new Dictionary<string, string>();
            foreach (var reference in references)
            {
                var assemblyName = reference.GetMetadata("Filename");
                var dllPath = reference.GetMetadata("FullPath");
                dict[assemblyName] = dllPath;
            }

            return dict;
        }
        
        public override bool Execute()
        {
            
            Log.LogMessage(MessageImportance.High, $"!!hello from test fade gcn=[{GeneratedClassName}] local=[{GenerateFileLocation}]");
            // use whatever version of fade is being referenced... 

            if (SourceFiles == null)
            {
                Log.LogError("If FadeBasic.Build is included, then SourceFiles must be specified.");
                return false; // nothing to do;
            }

            var libMap = new Dictionary<string, List<string>>();

            var dllTable = PrepareAssemblyToDllMap(References);

            Log.LogMessage(MessageImportance.High,$"[COMMANDS] there are {Commands.Length} commands...");
            for (var i = 0; i < Commands.Length; i++)
            {
                var command = Commands[i];
                var identity = command.GetMetadata("Identity");
                if (dllTable.TryGetValue(identity, out var dllPath))
                {
                    if (!libMap.TryGetValue(dllPath, out var lib))
                    {
                        libMap[dllPath] = lib = new List<string>();
                    }
                    var className = command.GetMetadata("FullName");
                    lib.Add(className);
                }
                
                
                // Log.LogMessage(MessageImportance.High,$" - metadata-count{command.MetadataCount}");
                // foreach (var name in command.MetadataNames)
                // {
                //     Log.LogMessage(MessageImportance.High,$" -- {name} -> {command.GetMetadata(name.ToString())}");
                // }
            }
            
            var libraries = new List<ProjectCommandSource>();
            var allClassNames = new List<string>();
            foreach (var kvp in libMap)
            {
                var source = new ProjectCommandSource
                {
                    absoluteOutputDllPath = kvp.Key,
                    commandClasses = kvp.Value // TODO: hashmap this?
                };
                allClassNames.AddRange(kvp.Value);
                libraries.Add(source);
                Log.LogMessage(MessageImportance.High,$" IDENTIFIED SOURCE: {string.Join(",", source.commandClasses)} dll=[{source.absoluteOutputDllPath}]" );
            }

            var commandCollection = ProjectBuilder.LoadCommandMetadata(libraries);
          
            // Log.LogMessage(MessageImportance.High,$"[REFERENCES] there are {References.Length} references...");
            // for (var i = 0; i < References.Length; i++)
            // {
            //     
            //     var command = References[i];
            //     // var assemblyName = command.GetMetadata("AssemblyName");
            //     // if (!assemblyName.StartsWith("Fade")) continue;
            //     
            //     
            //     // nugetPackageId - NuGetPackageId
            //     // NuGetSourceType -> Package
            //     // NuGetPackageId -> FadeBasic.Lib.Testing
            //     // Filename -> FadeBasic.Lib.Testing
            //     
            //     Log.LogMessage(MessageImportance.High,$" - metadata-count{command.MetadataCount}");
            //     foreach (var name in command.MetadataNames)
            //     {
            //         Log.LogMessage(MessageImportance.High,$" -- {name} -> {command.GetMetadata(name.ToString())}");
            //     }
            // }
            
            Log.LogMessage(MessageImportance.High, $"there are {SourceFiles.Length} source files");

            var sourcePaths = new List<string>(SourceFiles.Length);
            foreach (var item in SourceFiles)
            {
                var settingFile = item.GetMetadata("FullPath");
                sourcePaths.Add(settingFile);
                Log.LogMessage(MessageImportance.High, $"file: {settingFile} meta=[{item.MetadataCount}]");
            }

            var map = ProjectSourceMethods.CreateSourceMap(sourcePaths);
            
            // to generate the command collection, we need to load up the metadata for those dlls...
            // that means we need to be able to resolve PackageReference and ProjectReference to their actual dll paths
            
            
            var unit = map.Parse(commandCollection);

            var errors = unit.program.GetAllErrors();
            foreach (var error in errors)
            {
                var local = map.GetOriginalRange(error.location);
                Log.LogError(subcategory: null,
                    errorCode: "FADE:" + error.errorCode.code,
                    helpKeyword: null,
                    file: local.fileName,
                    lineNumber: error.location.start.lineNumber,
                    columnNumber: error.location.start.charNumber,
                    endLineNumber: error.location.end.lineNumber,
                    endColumnNumber: error.location.end.charNumber,
                    message: error.CombinedMessage);
            }

            if (errors.Count > 0) return false;

            // var compiler = new Compiler(new CommandCollection());
            // compiler.Compile(unit.program);
            
            // Log.LogMessage(MessageImportance.High, $"compiled program into {compiler.Program.Count} bytes");

            LaunchableGenerator.GenerateLaunchable(GeneratedClassName,GenerateFileLocation,unit, commandCollection, allClassNames);
            GeneratedFile = GenerateFileLocation;
            return true;
        }
    }
}