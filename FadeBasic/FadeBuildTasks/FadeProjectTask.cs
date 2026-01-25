using System;
using System.Collections.Generic;
using System.IO;
using ApplicationSupport.Code;
using ApplicationSupport.Launch;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace FadeBasic.Build
{
    
    //https://learn.microsoft.com/en-us/visualstudio/msbuild/tutorial-custom-task-code-generation?view=vs-2022
    public class FadeProjectTask : Task
    {
        public bool GenerateEntryPoint { get; set; } = true;
        public bool IgnoreSafetyChecks { get; set; } = false;
        public bool GenerateDebugData { get; set; }
        
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
            try
            {
                if (SourceFiles == null)
                {
                    Log.LogError("If FadeBasic.Build is included, then SourceFiles must be specified.");
                    return false; // nothing to do;
                }

                var libMap = new Dictionary<string, List<string>>();

                var dllTable = PrepareAssemblyToDllMap(References);

                Log.LogMessage(MessageImportance.Low, $"[FADE] there are {Commands.Length} commands...");
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
                    Log.LogMessage(MessageImportance.Low,
                        $"[FADE] IDENTIFIED SOURCE: {string.Join(",", source.commandClasses)} dll=[{source.absoluteOutputDllPath}]");
                }

                var commandCollection = ProjectBuilder.LoadCommandMetadata(libraries);


                Log.LogMessage(MessageImportance.Low, $"[FADE] there are {SourceFiles.Length} source files");

                var sourcePaths = new List<string>(SourceFiles.Length);
                foreach (var item in SourceFiles)
                {
                    var settingFile = item.GetMetadata("FullPath");
                    sourcePaths.Add(settingFile);
                    Log.LogMessage(MessageImportance.Low, $"[FADE] file: {settingFile} meta=[{item.MetadataCount}]");
                }

                var map = SourceMap.CreateSourceMap(sourcePaths);

                // to generate the command collection, we need to load up the metadata for those dlls...
                // that means we need to be able to resolve PackageReference and ProjectReference to their actual dll paths
                var unit = map.Parse(commandCollection.collection, new ParseOptions
                {
                    ignoreChecks = IgnoreSafetyChecks
                });
                var lexErrors = unit.lexerResults.tokenErrors;
                if (lexErrors.Count > 0)
                {
                    foreach (var error in lexErrors)
                    {
                        var local = map.GetOriginalRange(error.location);
                        Log.LogError(subcategory: "fade",
                            errorCode: "FADE:" + error.errorCode.code,
                            helpKeyword: null,
                            file: local.fileName,
                            lineNumber: local.startLine,
                            columnNumber: local.startChar,
                            endLineNumber: local.endLine,
                            endColumnNumber: local.endChar,
                            message: error.Display);
                    }

                    if (lexErrors.Count > 0) return false;

                }
                else
                {

                    var errors = unit.program?.GetAllErrors();
                    foreach (var error in errors)
                    {
                        var local = map.GetOriginalRange(error.location);
                        Log.LogError(subcategory: "fade",
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

                }


                LaunchableGenerator.GenerateLaunchable(GeneratedClassName, GenerateFileLocation, unit,
                    commandCollection.collection, allClassNames, includeMain: GenerateEntryPoint,
                    generateDebug: GenerateDebugData);
                GeneratedFile = GenerateFileLocation;
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"FadeBuild encountered fatal error. type=[{ex.GetType().Name}] message=[{ex.Message}] stack=[{ex.StackTrace}]");
                throw;
            }
        }
    }
}