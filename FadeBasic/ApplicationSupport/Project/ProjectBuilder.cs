using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using FadeBasic.Virtual;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;

// using System.Runtime.Loader;

namespace FadeBasic.ApplicationSupport.Project
{

    public class VirtualCommandProvider : IMethodSource
    {
        private CommandMetadata _metadata;
        public int Count { get; }
        public CommandInfo[] Commands { get; }
        public string CommandGroupName => _metadata.className;

        public VirtualCommandProvider(CommandMetadata metadata)
        {
            _metadata = metadata;

            Count = _metadata.commands.Count;
            Commands = new CommandInfo[Count];
            for (var i = 0; i < Count; i++)
            {
                var command = metadata.commands[i];
                var args = new CommandArgInfo[command.parameters.Count];
                for (var j = 0; j < args.Length; j++)
                {
                    args[j] = new CommandArgInfo
                    {
                        typeCode = (byte)command.parameters[j].typeCode,
                        isOptional = command.parameters[j].isOptional,
                        isParams = command.parameters[j].isParams,
                        isRef = command.parameters[j].isRef,
                        isRawArg = command.parameters[j].isRaw,
                        isVmArg = command.parameters[j].isVm
                    };
                }

                Commands[i] = new CommandInfo
                {
                    methodIndex = command.methodIndex,
                    name = command.callName,
                    sig = command.sig,
                    returnType = (byte)command.returnTypeCode, // todo; range check?
                    executor = (_) =>
                    {
                        // no-op.
                    },
                    args = args
                };
            }
        }
    }

    public class ProjectCommandInfo
    {
        public CommandCollection collection;
        public ProjectDocs docs;
    }
    
    public class ProjectBuilder
    {
        public static ProjectCommandInfo LoadCommandMetadata(ProjectContext context)
        {
            return LoadCommandMetadata(context.projectLibraries);
        }
        
        public static ProjectCommandInfo LoadCommandMetadata(List<ProjectCommandSource> libraries)
        {
            var metaDatas = new List<CommandMetadata>();
            
            foreach (var projectLib in libraries)
            {
                var loadContext = new AssemblyLoadContext("metadata", isCollectible: true);

                
                var firstDll = true;
                loadContext.Resolving += (assemblyContext, assemblyName) =>
                {
                    if (!firstDll)
                    {
                        throw new Exception($"Unable to load any more dlls other than the metadata type. invalid=[{assemblyName.FullName}]");
                    }
                    var loadedDependentAsm = assemblyContext.LoadFromAssemblyPath(projectLib.absoluteOutputDllPath);
                    return loadedDependentAsm;
                };

                var assembly = loadContext.LoadFromAssemblyPath(projectLib.absoluteOutputDllPath);

                foreach (var commandSubset in projectLib.commandClasses)
                {
                    var metadataTypeName = $"{commandSubset}MetaData";
                    var metadataType = assembly.GetType(metadataTypeName);

                    var field = metadataType.GetField("COMMANDS_JSON", BindingFlags.Static | BindingFlags.Public);
                    var metadataJson = field.GetValue(null) as string;

                    var metadata = JsonSerializer.Deserialize<CommandMetadata>(metadataJson, new JsonSerializerOptions
                    {
                        IncludeFields = true,
                        PropertyNameCaseInsensitive = true,
                    });
                    metadata.className = commandSubset;

                    metaDatas.Add(metadata);
                }

                
                
                loadContext.Unload();
                
                // foreach (var )
                // assembly.GetType()
                // var userAssembly = loadContext.LoadFromAssemblyPath(fullPath);
            }

            var docs = ProjectDocMethods.LoadDocs<MarkdownDocParser>(metaDatas);
            var sources = metaDatas.Select(x => (IMethodSource)new VirtualCommandProvider(x)).ToArray();
            return new ProjectCommandInfo
            {
                collection = new CommandCollection(sources),
                docs = docs
            };
        }
        
    }
}