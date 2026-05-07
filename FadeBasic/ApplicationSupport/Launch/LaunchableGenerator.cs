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
    public const string TAG_ENCODED_TESTMANIFEST = "__ENCODED_TEST_MANIFEST__";
    public const string TAG_COMMAND_ARRAY = "__COMMAND_ARR__";
    public const string TEMPLATE_BYTECODE_TAB = "        ";
    public const string TEMPLATE_ENCODED_BYTE_VAR = "encodedByteCode";
    public const string TEMPLATE_ENCODED_DEBUGDATA_VAR = "encodedDebugData";
    public const string TEMPLATE_ENCODED_TESTMANIFEST_VAR = "encodedTestManifest";
    public const string TEMPLATE_BYTECODE_VAR = "_byteCode";
    public const string TEMPLATE_DEBUGDATA_VAR = "_debugData";
    public const string TEMPLATE_TESTMANIFEST_VAR = "_testManifest";

    // Default Main when FadeEnableTesting is off. Forwards args into the
    // existing test-aware Launcher dispatcher (handles --fade-test=name etc.).
    public static readonly string MainTemplate =
$@"
    public static int Main(string[] args)
    {{
        return Launcher.Main<{TAG_CLASSNAME}>(args);
    }}
";

    // Main when FadeEnableTesting is on. Routes Microsoft.Testing.Platform
    // invocations (dotnet test, --list-tests, --filter, --server, ...) through
    // FadeBasic.Testing.FadeTestApplicationBuilder; everything else still goes
    // to the existing Launcher path so `dotnet run` and --fade-test keep working.
    //
    // Custom IFadeTestHost is picked up by attribute-based discovery: tag the
    // class [FadeBasic.Testing.FadeTestHost] and FadeTestApplicationBuilder
    // resolves it at startup. If none is found, DefaultFadeTestHost is used.
    public static readonly string MainTemplateWithTesting =
$@"
    public static int Main(string[] args)
    {{
        if (global::FadeBasic.Testing.FadeTestApplicationBuilder.IsTestInvocation(args))
        {{
            var instance = new {TAG_CLASSNAME}();
            return global::FadeBasic.Testing.FadeTestApplicationBuilder
                .RunAsync(instance, args)
                .GetAwaiter().GetResult();
        }}
        return Launcher.Main<{TAG_CLASSNAME}>(args);
    }}
";

    public static readonly string ClassTemplate =
$@"// This is a generated file. Do not edit directly.

using {nameof(System)};
using {nameof(System)}.{nameof(System.Collections)}.{nameof(System.Collections.Generic)};
using {nameof(FadeBasic)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Launch)};
using {nameof(FadeBasic)}.{nameof(FadeBasic.Virtual)};

public partial class {TAG_CLASSNAME} : {nameof(ITestLaunchable)}
{{
    {TAG_MAIN}

    // this byteCode represents a fully compiled program
    public byte[] Bytecode => {TEMPLATE_BYTECODE_VAR};

    // this table represents the baked commands available within the program
    public CommandCollection CommandCollection => _collection;

    public DebugData DebugData => {TEMPLATE_DEBUGDATA_VAR};

    public IReadOnlyList<TestManifestEntry> TestManifest => {TEMPLATE_TESTMANIFEST_VAR};

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

    #region testManifest
    protected IReadOnlyList<TestManifestEntry> {TEMPLATE_TESTMANIFEST_VAR} = {nameof(LaunchUtil)}.{nameof(LaunchUtil.UnpackTestManifest)}({TEMPLATE_ENCODED_TESTMANIFEST_VAR});
    protected const string {TEMPLATE_ENCODED_TESTMANIFEST_VAR} = {TAG_ENCODED_TESTMANIFEST};
    #endregion
}}
";

    public static void GenerateLaunchable(string className,
        string filePath,
        CodeUnit unit,
        CommandCollection collection,
        List<string> commandClasses,
        bool includeMain=true,
        bool generateDebug=false,
        bool enableTesting=false)
    {
        var compiler = unit.program.Compile(collection, new CompilerOptions
        {
            GenerateDebugData = generateDebug
        });

        // Stamp originating .fbasic file paths onto each test manifest entry
        // before we pack it into the generated launchable. Multi-file projects
        // need this so IDE Test Explorer (Stage 11H VSTest adapter) can
        // source-link each test to the right file. CodeUnit always carries a
        // SourceMap when it comes from the build-task / SDK pipelines.
        FadeBasic.Launch.LaunchUtil.ApplySourceMap(compiler.TestManifest, unit.sourceMap);

        var byteCode = compiler.Program.ToArray();
        var src = ClassTemplate;

        var byteCodeStr = LaunchUtil.Pack64(byteCode);
        string byteCodeReplacement = "\"" + byteCodeStr + "\"";
        var commandArray = GetCommandTable(commandClasses);


        var debugDataStr = generateDebug ? LaunchUtil.PackDebugData(compiler.DebugData) : "";
        string debugDataReplacement = "\"" + debugDataStr + "\"";

        // Always pack the test manifest. Empty when the source has no tests.
        var testManifestStr = LaunchUtil.PackTestManifest(compiler.TestManifest);
        string testManifestReplacement = "\"" + testManifestStr + "\"";

        string mainBlock = "";
        if (includeMain)
        {
            mainBlock = enableTesting ? MainTemplateWithTesting : MainTemplate;
        }

        src = src.Replace(TAG_MAIN, mainBlock);
        src = src.Replace(TAG_COMMAND_ARRAY, commandArray);
        src = src.Replace(TAG_ENCODED_BYTECODE, byteCodeReplacement);
        src = src.Replace(TAG_ENCODED_DEBUGDATA, debugDataReplacement);
        src = src.Replace(TAG_ENCODED_TESTMANIFEST, testManifestReplacement);
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
