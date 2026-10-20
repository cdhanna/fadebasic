using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Tests.CSharpBackend;

/// <summary>
/// The differential corpus: hand-written programs that pin VM semantics (and quirks), plus every
/// program literal harvested from the existing VM test files, so the C# backend is checked against
/// everything the VM is already tested on.
/// </summary>
public static class CSharpBackendCorpus
{
    static string ThisDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    /// <summary>Test files whose `var src = @"..."` programs are harvested.</summary>
    static readonly string[] HarvestFiles =
    {
        "TokenVm.cs",
        "TokenVm_GC.cs",
        "TokenVm_LenDimsBytes.cs",
        "TokenVM_Primitives.cs",
        "FunctionVmTests.cs",
        "DeferTests.cs",
        "VariableTests.cs",
        "ArraySpreadParamsTests.cs",
        "LenKeywordTests.cs",
        "RegisterWrapRepro.cs",
        "ExpressionTests.cs",
        "TestExecutionTests.cs",
        "RuntoCompilerTests.cs",
        "MockExecutionTests.cs",
        "TestFunctionTests.cs",
        "TestFromChainTests.cs",
        "AssertMacroTests.cs",
        "TestScopeTests.cs",
        "RuntoNavigationTests.cs",
        "FadeTestRunnerTests.cs",
    };

    static readonly Regex VerbatimSource = new(@"\bsrc\s*=\s*@""((?:[^""]|"""")*)""", RegexOptions.Compiled);
    static readonly Regex PlainSource = new(@"\bsrc\s*=\s*""((?:[^""\\\r\n]|\\.)*)""", RegexOptions.Compiled);

    public static IEnumerable<(string name, string src)> Harvested()
    {
        var testsDir = Path.GetDirectoryName(ThisDir())!;
        var seen = new HashSet<string>();
        foreach (var file in HarvestFiles)
        {
            var path = Path.Combine(testsDir, file);
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            var index = 0;
            foreach (Match m in VerbatimSource.Matches(text))
            {
                var src = m.Groups[1].Value.Replace("\"\"", "\"");
                if (seen.Add(src)) yield return ($"{Path.GetFileNameWithoutExtension(file)}#{index++}", src);
            }
            foreach (Match m in PlainSource.Matches(text))
            {
                var src = Regex.Unescape(m.Groups[1].Value);
                if (seen.Add(src)) yield return ($"{Path.GetFileNameWithoutExtension(file)}#p{index++}", src);
            }
        }
    }

    /// <summary>Programs that would differ between runs on either backend (randomness, clocks).</summary>
    public static bool IsNondeterministic(string src) =>
        Regex.IsMatch(src, @"\b(rnd|now|timer)\b", RegexOptions.IgnoreCase);
}
