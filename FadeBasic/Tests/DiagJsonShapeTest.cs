using System.Collections.Generic;
using FadeBasic.Json;
using FadeBasic.LSP.Core;
using NUnit.Framework;

namespace Tests;

[TestFixture]
public class DiagJsonShapeTest
{
    [Test]
    public void MatchesExpectedWireShape()
    {
        var d = new LspDiagnostic
        {
            Range = new LspRange { Start = new LspPosition { Line = 2, Character = 3 }, End = new LspPosition { Line = 2, Character = 7 } },
            Severity = LspDiagnosticSeverity.Warning,
            Code = "E-1",
            Source = "fade",
            Message = "bad \"thing\"",
        };
        var json = d.Jsonify();
        TestContext.Progress.WriteLine(json);
        Assert.That(json, Is.EqualTo(
            "{\"range\":{\"start\":{\"line\":2,\"character\":3},\"end\":{\"line\":2,\"character\":7}},\"severity\":2,\"code\":\"E-1\",\"source\":\"fade\",\"message\":\"bad \\\"thing\\\"\"}"));
    }
}
