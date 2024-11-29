using System.Text;
using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Launch;
using FadeBasic.Virtual;

namespace ApplicationSupport.Launch;

public class LaunchableGenerator
{
    public const string TAG_CLASSNAME = "__CLASS_NAME__";
    public const string TAG_BYTECODE = "__BYTE_CODE__";
    public const string TAG_MAIN = "__MAIN__";
    public const string TAG_ENCODED_BYTECODE = "__ENCODED_BYTE_CODE__";
    public const string TAG_ENCODED_DEBUGDATA = "__ENCODED_DEBUG_DATA__";
    public const string TAG_COMMAND_ARRAY = "__COMMAND_ARR__";
    public const string TEMPLATE_BYTECODE_TAB = "        ";
    public const string TEMPLATE_ENCODED_BYTE_VAR = "encodedByteCode";
    public const string TEMPLATE_ENCODED_DEBUGDATA_VAR = "encodedDebugData";
    public const string TEMPLATE_BYTECODE_VAR = "_byteCode";
    public const string TEMPLATE_DEBUGDATA_VAR = "_debugData";

    public static readonly string MainTemplate =
$@"
    public static void Main(string[] args)
    {{
        Launcher.Run<{TAG_CLASSNAME}>();
    }}
";
    public static readonly string ClassTemplate = 
$@"// This is a generated file. Do not edit directly.

using {nameof(System)};
using {nameof(FadeBasic)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Launch)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Virtual)};

public class {TAG_CLASSNAME} : {nameof(ILaunchable)}
{{
    {TAG_MAIN}

    // this byteCode represents a fully compiled program
    public byte[] Bytecode => {TEMPLATE_BYTECODE_VAR};

    // this table represents the baked commands available within the program
    public CommandCollection CommandCollection => _collection;

    public DebugData DebugData => {TEMPLATE_DEBUGDATA_VAR};

    #region method table
    private static readonly CommandCollection _collection = new CommandCollection(
        {TAG_COMMAND_ARRAY}
    );
    #endregion

    #region debugData
    protected DebugData {TEMPLATE_DEBUGDATA_VAR} = {nameof(LaunchUtil)}.{nameof(LaunchUtil.UnpackDebugData)}({TEMPLATE_ENCODED_DEBUGDATA_VAR});
    protected const string {TEMPLATE_ENCODED_DEBUGDATA_VAR} = {TAG_ENCODED_DEBUGDATA};
    #endregion

    #region bytecode
    protected byte[] {TEMPLATE_BYTECODE_VAR} = {nameof(LaunchUtil)}.{nameof(LaunchUtil.Unpack64)}({TEMPLATE_ENCODED_BYTE_VAR});
    protected const string {TEMPLATE_ENCODED_BYTE_VAR} = {TAG_ENCODED_BYTECODE};
    #endregion
}}
";

    public static void GenerateLaunchable(string className, 
        string filePath, 
        CodeUnit unit, 
        CommandCollection collection, 
        List<string> commandClasses, 
        bool includeMain=true,
        bool generateDebug=false)
    {
        var compiler = unit.program.Compile(collection, new CompilerOptions
        {
            GenerateDebugData = generateDebug
        });
        var byteCode = compiler.Program.ToArray();
        var src = ClassTemplate;

        var byteCodeStr = LaunchUtil.Pack64(byteCode);
        string byteCodeReplacement = "\"" + byteCodeStr + "\"";
        var commandArray = GetCommandTable(commandClasses);

        
        var debugDataStr = generateDebug ? LaunchUtil.PackDebugData(compiler.DebugData) : "";
        string debugDataReplacement = "\"" + debugDataStr + "\"";
        
        var main = includeMain ? MainTemplate : "";
        src = src.Replace(TAG_MAIN, main);
        src = src.Replace(TAG_COMMAND_ARRAY, commandArray);
        src = src.Replace(TAG_ENCODED_BYTECODE, byteCodeReplacement);
        src = src.Replace(TAG_ENCODED_DEBUGDATA, debugDataReplacement);
        src = src.Replace(TAG_CLASSNAME, className);

        var dir = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, src);
    }

    static string GetCommandTable(List<string> commandClasses)
    {
        var instantiates = new List<string>();
        foreach (var className in commandClasses)
        {
            instantiates.Add($"new {className}()");
        }
        return string.Join(", ", instantiates);
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
