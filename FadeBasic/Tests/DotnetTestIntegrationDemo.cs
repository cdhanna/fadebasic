using FadeBasic;
using FadeBasic.Sdk;

namespace Tests;

/// <summary>
/// Demonstration of the recipe for surfacing Fade tests in a `dotnet test`
/// run. Each Fade `test ... endtest` block becomes a separate NUnit test case
/// via <c>TestCaseSource</c>, so it shows up individually in IDE Test
/// Explorer and CI logs.
///
/// Real-world consumer projects would replace <c>SampleFadeSource</c> with
/// either an embedded resource or a file path that points at the project's
/// .fbasic files, and would reference their own <c>CommandCollection</c>.
/// </summary>
[TestFixture]
public class DotnetTestIntegrationDemo
{
    private const string SampleFadeSource = @"
counter = 0
counter = counter + 1
checkpoint:
end

test counter_increments
    runto checkpoint
    assert counter = 1
endtest

test math_works
    assert 2 + 2 = 4
endtest

test string_compare
    local s as string
    s = ""hello""
    assert s = ""hello""
endtest
";

    // Cache the runtime context across cases so we only compile once. Each
    // test still gets a fresh VM (RunTest builds one per call), so state is
    // isolated between Fade tests.
    private static FadeRuntimeContext _cachedContext;
    private static FadeRuntimeContext SharedContext
    {
        get
        {
            if (_cachedContext != null) return _cachedContext;
            var ok = Fade.TryCreateFromString(SampleFadeSource, TestCommands.CommandsForTesting,
                out var ctx, out var errors);
            if (!ok)
            {
                throw new Exception("Fade compile failed: " + errors.ToDisplay());
            }
            return _cachedContext = ctx;
        }
    }

    // NUnit calls this static method to populate test cases. Each yielded
    // value becomes a parameter to the test method. Returning test names as
    // strings produces test cases like
    // `RunFadeTest(\"counter_increments\")` in the Test Explorer.
    public static IEnumerable<string> DiscoverFadeTests()
    {
        foreach (var t in SharedContext.Tests)
        {
            yield return t.name;
        }
    }

    [Test]
    [TestCaseSource(nameof(DiscoverFadeTests))]
    public void RunFadeTest(string fadeTestName)
    {
        var result = SharedContext.RunTest(fadeTestName);
        Assert.That(result.passed, Is.True,
            $"Fade test `{fadeTestName}` failed: {result.failureMessage}");
    }

    // Companion test: confirm that the discovery returns the expected set.
    [Test]
    public void DiscoverFadeTests_ReturnsAllTopLevelTests()
    {
        var names = DiscoverFadeTests().ToList();
        Assert.That(names, Does.Contain("counter_increments"));
        Assert.That(names, Does.Contain("math_works"));
        Assert.That(names, Does.Contain("string_compare"));
    }
}
