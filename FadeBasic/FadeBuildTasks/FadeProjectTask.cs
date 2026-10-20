using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using ApplicationSupport.Code;
using ApplicationSupport.Launch;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Lib.Standard;
using FadeBasic.Sdk;
using FadeBasic.Virtual;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace FadeBasic.Build
{
    
    //https://learn.microsoft.com/en-us/visualstudio/msbuild/tutorial-custom-task-code-generation?view=vs-2022
    public class FadeProjectTask : Task
    {
        public string OutputPath { get; set; }
        public bool GenerateEntryPoint { get; set; } = true;
        public bool IgnoreSafetyChecks { get; set; } = false;
        public bool GenerateDebugData { get; set; }
        // When true, the generated Main dispatches to Microsoft.Testing.Platform
        // for `dotnet test` invocations and to Launcher.Main<T> otherwise.
        // Wire this from the FadeEnableTesting MSBuild property.
        public bool EnableTesting { get; set; } = false;

        // "VM" (default) bakes bytecode only. "CSharp" also compiles the program to C# with
        // the C# backend, and the generated Main runs that instead of the VM.
        public string Backend { get; set; }

        // Compile `test` blocks into the program (FadeIncludeTests; on for Debug by default).
        public bool IncludeTests { get; set; } = true;
        
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
        
        /// <summary>
        /// Reports an error at its place in its own file. The source files are joined before they
        /// are parsed, so a token's line is a line of the joined program; MSBuild wants the file's
        /// own line and column, counted from 1.
        /// </summary>
        void LogFadeError(SourceMap map, ParseError error)
        {
            SourceRange local;
            try
            {
                local = map.GetOriginalRange(error.location);
            }
            catch (NotSupportedException)
            {
                // a range that spans two files: report its start
                var start = map.GetOriginalLocation(error.location.start);
                local = new SourceRange
                {
                    fileName = start.fileName, startLine = start.startLine, startChar = start.startChar,
                    endLine = start.startLine, endChar = start.startChar
                };
            }
            Log.LogError(subcategory: "fade",
                errorCode: "FADE:" + error.errorCode.code,
                helpKeyword: null,
                file: local.fileName,
                lineNumber: local.startLine + 1,
                columnNumber: local.startChar + 1,
                endLineNumber: local.endLine + 1,
                endColumnNumber: local.endChar + 1,
                message: error.CombinedMessage);
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

                var (commandCollection, loadContext) = ProjectBuilder.LoadCommands(OutputPath, libraries, msg=>Log.LogMessage(MessageImportance.High, msg));
                
                Log.LogMessage(MessageImportance.Low, $"[FADE] there are {SourceFiles.Length} source files");

                var sourcePaths = new List<string>(SourceFiles.Length);
                foreach (var item in SourceFiles)
                {
                    var settingFile = item.GetMetadata("FullPath");
                    sourcePaths.Add(settingFile);
                    Log.LogMessage(MessageImportance.Low, $"[FADE] file: {settingFile} meta=[{item.MetadataCount}]");
                }

                ConsoleCommands.msBuildLogger = (lvl, msg) =>
                {
                    Log.LogMessage(MessageImportance.High, msg);
                };
                var map = SourceMap.CreateSourceMap(sourcePaths);

                // to generate the command collection, we need to load up the metadata for those dlls...
                // that means we need to be able to resolve PackageReference and ProjectReference to their actual dll paths
                var unit = map.Parse(commandCollection.collection, new ParseOptions
                {
                    ignoreChecks = IgnoreSafetyChecks
                });
        
                // The C# backend binds commands to their C# methods by reflection, so it has
                // to run while the command assemblies are still loaded.
                FadeBasic.CSharp.CSharpTranspileResult compiledProgram = null;
                var useCSharp = string.Equals(Backend, "CSharp", StringComparison.OrdinalIgnoreCase);
                if (useCSharp && unit.lexerResults.tokenErrors.Count == 0 && unit.program != null
                    && unit.program.GetAllErrors().Count == 0)
                {
                    compiledProgram = FadeBasic.CSharp.CSharpTranspiler.Transpile(unit.program, commandCollection.collection,
                        new FadeBasic.CSharp.CSharpTranspileOptions
                        {
                            Namespace = "FadeCompiled",
                            ClassName = GeneratedClassName + "Program",
                            SourceMap = map,
                            IncludeTests = IncludeTests
                        });
                }

                // after the parsing has been done, we can nix the load context
                loadContext.Unload();

                if (compiledProgram != null && !compiledProgram.Success)
                {
                    foreach (var error in compiledProgram.Errors)
                    {
                        Log.LogError(subcategory: "fade", errorCode: "FADE:CS0001", helpKeyword: null,
                            file: null, lineNumber: 0, columnNumber: 0, endLineNumber: 0, endColumnNumber: 0,
                            message: "the C# backend cannot compile this program: " + error +
                                     " (build with FadeBackend=VM to use the bytecode VM)");
                    }
                    return false;
                }
                
                var lexErrors = unit.lexerResults.tokenErrors;
                if (lexErrors.Count > 0)
                {
                    foreach (var error in lexErrors)
                    {
                        LogFadeError(map, error);
                    }

                    if (lexErrors.Count > 0) return false;

                }
                else
                {

                    var errors = unit.program?.GetAllErrors();
                    foreach (var error in errors)
                    {
                        LogFadeError(map, error);
                    }

                    if (errors.Count > 0) return false;

                }


                LaunchableGenerator.GenerateLaunchable(GeneratedClassName, GenerateFileLocation, unit,
                    commandCollection.collection, allClassNames, includeMain: GenerateEntryPoint,
                    generateDebug: GenerateDebugData,
                    enableTesting: EnableTesting,
                    compiledProgram: compiledProgram,
                    includeTests: IncludeTests);
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