using System.Linq;
using FadeBasic;
using FadeBasic.Lsp;
using NUnit.Framework;

namespace Tests.HotReload;

[TestFixture]
public class LexClassifyProbeTests
{
    [Test]
    public void EndifAndLoop_ClassifyAsKeyword_InFullLex()
    {
        var src = "print \"hello\"\n\nt = 1\ndo \n    if rightkey()\n        print \"f\", t\n        t = t + 1\n    endif\nloop\n";
        var lexer = new Lexer();
        var results = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var toks = results.allTokens;

        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.raw == null) continue;
            var lower = t.raw.ToLowerInvariant();
            if (lower != "endif" && lower != "loop") continue;
            var prev = i > 0 ? toks[i - 1] : null;
            var r = LSPUtil.ClassifyToken(t, prev);
            TestContext.WriteLine($"'{t.raw}' lexemType={t.type} -> {r.TokenType} skip={r.Skip}");
            Assert.That(r.Skip, Is.False, $"'{t.raw}' should not be skipped");
            Assert.That(r.TokenType, Is.EqualTo(PortableSemanticTokenType.Keyword), $"'{t.raw}' should be keyword");
        }
    }
}
