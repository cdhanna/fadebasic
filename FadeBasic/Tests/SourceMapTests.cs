using ApplicationSupport.Code;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;

namespace Tests;

public class SourceMapTests
{
    [Test]
    public void Test()
    {
        var file = @"print ""hello""
x = 3
print ""igloo""";
        var fileName = "mock.fbasic";

        var map = ProjectSourceMethods.CreateSourceMap(new List<string> { fileName }, _ => file.SplitNewLines());
        var unit = map.Parse(TestCommands.CommandsForTesting);

        var expr = unit.program.statements[2];
        var range = map.GetOriginalRange(new TokenRange { start = expr.StartToken, end = expr.EndToken });
        
        // the fact that code reaches here is good!
        Assert.That(range.startLine, Is.EqualTo(2));
        Assert.That(range.endLine, Is.EqualTo(2));
    }
    
    
    [Test]
    public void Test2()
    {
        var file = @"print ""hello""
a = 1
print str$(3)";
        var fileName = "mock.fbasic";

        var map = ProjectSourceMethods.CreateSourceMap(new List<string> { fileName }, _ => file.SplitNewLines());
        var unit = map.Parse(TestCommands.CommandsForTesting);

        var expr = unit.program.statements[2];
        var range = map.GetOriginalRange(new TokenRange { start = expr.StartToken, end = expr.EndToken });
        
        // the fact that code reaches here is good!
        Assert.That(range.startLine, Is.EqualTo(2));
        Assert.That(range.endLine, Is.EqualTo(2));
    }
}