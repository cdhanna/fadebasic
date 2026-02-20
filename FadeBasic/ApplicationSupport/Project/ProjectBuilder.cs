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

        public static byte[] GetDefaultByteArrayForTypeCode(byte typeCode)
        {
            
            switch (typeCode)
            {
                case TypeCodes.STRUCT:
                case TypeCodes.STRING:
                case TypeCodes.VOID:
                    return null;
                default:
                    // create a byte array full of zeros.
                    var size = TypeCodes.GetByteSize(typeCode);
                    return new byte[size];
            }

            return null;
        }
        
        public static CommandExecution BuildNoopExecution(ProjectCommandMetadata command)
        {
            return vm =>
            {
                // TODO: pull inputs off the stack. 
                for (var i = command.parameters.Count - 1;
                     i >= 0; i--)
                {
                   
                    var parameter = command.parameters[i];
                    
                    if (i == command.parameters.Count - 1) // last parameter...
                    {
                        if (parameter.isParams)
                        {
                            // ah, we need to read the number of actually bound parameters...
                            // TODO: is it possible to have ref optionals???
                            VmUtil.ReadAsInt(ref vm.stack, out var optionalArgs);
                            for (var j = 0; j < optionalArgs; j++)
                            {
                                VmUtil.ReadNoopValue(vm, default, out _, out _, out _);
                            }
                            // skip the rest of the handling...
                            continue;
                        }
                    }
                    
                    var tc = (byte)parameter.typeCode;
                    VmUtil.ReadNoopValue(vm, default, out var val, out var state, out var addr);
                    if (parameter.isRef)
                    {
                        if (tc == TypeCodes.STRING)
                        {
                            VmUtil.HandleValueString(vm, "", TypeCodes.STRING, state, addr);
                        }
                        else
                        {
                            var refBytes = GetDefaultByteArrayForTypeCode(tc);
                            VmUtil.HandleValueAny(vm, refBytes, tc, state, addr);
                        }
                    }
                }
                
                var returnTypeCode = (byte)command.returnTypeCode;
                switch (returnTypeCode)
                {
                    case TypeCodes.STRUCT:
                        throw new Exception("Cannot mock a struct return");
                    case TypeCodes.PTR_REG:
                        throw new Exception("Cannot mock a reg ptr return");
                    case TypeCodes.PTR_GLOBAL_REG:
                        throw new Exception("Cannot mock a global reg ptr return");
                    case TypeCodes.PTR_HEAP:
                        throw new Exception("Cannot mock a heap ptr return");

                    case TypeCodes.STRING:
                        // TODO: handle string returns
                        VmConverter.FromStringSpan("<noop>", out var blankStringSpan);
                        vm.heap.AllocateString(blankStringSpan.Length, out var blankStringPtr);
                        vm.heap.WriteSpan(blankStringPtr, blankStringSpan.Length, blankStringSpan);
                        var blankPtrBytes = VmPtr.GetBytes(ref blankStringPtr);
                        VmUtil.PushSpan(ref vm.stack, blankPtrBytes, TypeCodes.STRING);

                        break;
                    case TypeCodes.VOID:
                        // don't do anything.
                        break;
                    default:
                        // push the default return value onto the stack.
                        
                        var size = TypeCodes.GetByteSize(returnTypeCode);
                        var blankData = new byte[size];
                        VmUtil.PushSpan(ref vm.stack, blankData, returnTypeCode);
                        
                        break;
                }
            };
        }
        
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
                    usage = command.usage,
                    sig = command.sig,
                    returnType = (byte)command.returnTypeCode, // todo; range check?
                    executor = BuildNoopExecution(command),
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
                
            }

            var docs = ProjectDocMethods.LoadDocs<MarkdownDocParser>(metaDatas);
            // TODO: this is a bug! 
            //  macro DLLs are not loaded at this time, so we cannot actually invoke into them :( 
            //  we need to load the macro DLL's once per compilation? and pray they don't explode? 
            var sources = metaDatas.Select(x => (IMethodSource)new VirtualCommandProvider(x)).ToArray();
            return new ProjectCommandInfo
            {
                collection = new CommandCollection(sources),
                docs = docs
            };
        }
        
        
        public static (ProjectCommandInfo, AssemblyLoadContext) LoadCommands(string libPath, List<ProjectCommandSource> libraries, Action<string> log)
        {
            var metaDatas = new List<CommandMetadata>();
            var sources = new List<IMethodSource>();
            var loadContext = new AssemblyLoadContext("metadata", isCollectible: true);

            // TODO: technically there could be multiple lib paths, right? one for each library?
            // var libPath = Path.GetDirectoryName(libraries[0].absoluteOutputDllPath);
            // var libPath = AppContext.BaseDirectory;
            
            loadContext.Resolving += (assemblyContext, assemblyName) =>
            {
                if (assemblyName.FullName == typeof(IMethodSource).Assembly.GetName().FullName)
                {
                    // log("!!! Trying to load common assembly.");
                    return typeof(IMethodSource).Assembly;
                }
                
                
                // log($"!!! Requested: [{assemblyName.FullName}]");
                //log($"!!! Compared: [{typeof(IMethodSource).Assembly.GetName().FullName}]");
                
                string candidatePath = Path.Combine(
                    libPath,
                    assemblyName.Name + ".dll");

                // log($"!!! candidate-path=[{candidatePath}]");
                
                if (!File.Exists(candidatePath))
                    return null;

                var foundName = AssemblyName.GetAssemblyName(candidatePath);
                // log($"!!! candidate-name=[{foundName.Name}] vs requested=[{assemblyName.Name}]");
                
                if (foundName.Name != assemblyName.Name)
                    return null;

                // log($"!!! Proxied: [{candidatePath}]");
                return assemblyContext.LoadFromAssemblyPath(candidatePath);
            };
            using var _ = loadContext.EnterContextualReflection();
            
            foreach (var projectLib in libraries)
            {
                // var assembly = Assembly.GetAssembly()
                var assembly = loadContext.LoadFromAssemblyPath(projectLib.absoluteOutputDllPath);
                // assembly.
                foreach (var commandSubset in projectLib.commandClasses)
                {
                    var metadataTypeName = $"{commandSubset}MetaData";
                    var commandType = assembly.GetType(commandSubset);
                    var metadataType = assembly.GetType(metadataTypeName);
                    //
                    // log($"Looking for type=[{commandSubset}]");
                    // log($"Found metadata as=[{metadataType}]");
                    //
                    // log($"Found type as=[{commandType}]");
                    if (commandType == null)
                    {
                        // log("Did not find command! These are the types");
                        // var allTypes = assembly.GetTypes();
                        // foreach (var t in allTypes)
                        // {
                        //     log($"   t:[{t.Name}]");
                        // }
                    }
                    var instance = Activator.CreateInstance(commandType);
                    
                    
                    sources.Add(instance as IMethodSource);
                    
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
            }

            var docs = ProjectDocMethods.LoadDocs<MarkdownDocParser>(metaDatas);
            // TODO: this is a bug! 
            //  macro DLLs are not loaded at this time, so we cannot actually invoke into them :( 
            //  we need to load the macro DLL's once per compilation? and pray they don't explode? 
            // var sources = metaDatas.Select(x => (IMethodSource)new VirtualCommandProvider(x)).ToArray();
            return (new ProjectCommandInfo
            {
                collection = new CommandCollection(sources.ToArray()),
                docs = docs
            }, loadContext);
        }
        
    }
}