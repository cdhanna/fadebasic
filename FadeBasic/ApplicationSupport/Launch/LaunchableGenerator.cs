using System.Text;
using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Launch;
using Serilog;

namespace ApplicationSupport.Launch;

public class LaunchableGenerator
{
    public const string TAG_CLASSNAME = "__CLASS_NAME__";
    public const string TAG_BYTECODE = "__BYTE_CODE__";
    public const string TAG_ENCODED_BYTECODE = "__ENCODED_BYTE_CODE__";
    public const string TAG_COMMAND_ARRAY = "__COMMAND_ARR__";
    public const string TEMPLATE_BYTECODE_TAB = "        ";
    public const string TEMPLATE_ENCODED_BYTE_VAR = "encodedByteCode";
    public const string TEMPLATE_BYTECODE_VAR = "_byteCode";
    
    public static readonly string ClassTemplate = 
$@"// This is a generated file. Do not edit directly.

using {nameof(System)};
using {nameof(FadeBasic)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Launch)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Virtual)};

public class {TAG_CLASSNAME} : {nameof(ILaunchable)}
{{
    // this byteCode represents a fully compiled program
    public byte[] Bytecode => {TEMPLATE_BYTECODE_VAR};

    // this table represents the baked commands available within the program
    public CommandCollection CommandCollection => _collection;

    #region method table
    private static readonly CommandCollection _collection = new CommandCollection(
        {TAG_COMMAND_ARRAY}
    );
    #endregion

    #region bytecode
    protected byte[] {TEMPLATE_BYTECODE_VAR} = {nameof(LaunchUtil)}.{nameof(LaunchUtil.Unpack64)}({TEMPLATE_ENCODED_BYTE_VAR});
    protected const string {TEMPLATE_ENCODED_BYTE_VAR} = {TAG_ENCODED_BYTECODE};
    #endregion
}}
";

    public static bool TryGenerateLaunchable(ProjectContext projectContext)
    {
        
        var metadata = ProjectBuilder.LoadCommandMetadata(projectContext);
        Log.Debug($"loaded project metadata-count=[{metadata.Commands.Count}]");

        if (!File.Exists(projectContext.absoluteLaunchCsProjPath))
        {
            Log.Error("no project exists");
            return false;
        }
        Log.Debug($"launch project=[{projectContext.absoluteLaunchCsProjPath}]");

        var absLaunchDirectory = Path.GetDirectoryName(projectContext.absoluteLaunchCsProjPath);
            
        // need to generate the byte-code!
        var source = projectContext.CombineSource();
        Log.Debug("program: " + source);

        var program = source.Parse(metadata);
        var byteCode = program.Compile(metadata);
        Log.Debug("bytecode: " + string.Join(", ", byteCode));

        var className = "Generated_" + projectContext.name;
        var filePath = Path.Combine(absLaunchDirectory, className + ".g.cs");
        
        var src = ClassTemplate;

        var byteCodeStr = LaunchUtil.Pack64(byteCode);
        string byteCodeReplacement = "\"" + byteCodeStr + "\"";
        var commandArray = GetCommandTable(projectContext);
        src = src.Replace(TAG_COMMAND_ARRAY, commandArray);
        src = src.Replace(TAG_ENCODED_BYTECODE, byteCodeReplacement);
        src = src.Replace(TAG_CLASSNAME, className);

        File.WriteAllText(filePath, src);
        return true;
    }

    static string GetCommandTable(ProjectContext context)
    {
        // IMethod collection = new CommandCollection()
        var instantiates = new List<string>();
        foreach (var lib in context.projectLibraries)
        {
            foreach (var className in lib.commandClasses)
            {
                // we know that the className refers to an IMethodSource
                instantiates.Add($"new {className}()");
            }
        }
        return string.Join(", ", instantiates);
    }
}
