using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using Microsoft.Build.Locator;

namespace Tests.ApplicationSupport;

public class LoadTests
{
    [Test]
    public void RegexTest()
    {
        var input = @"
    /// test the thing 
    /// <summary>
    ///     goof okay
    /// </summary>
    /// 33 test
    ///
        ///abc
";
        var res = RegexUtil.ReplaceDocSlashes(input);
        Assert.That(res, Is.EqualTo(@"
test the thing 
<summary>
goof okay
</summary>
33 test
abc
"));
    }
    
    [Test]
    public void LoadMetadata()
    {
        var str = FadeBasicCommandsMetaData.COMMANDS_JSON;
        var metadata = JsonSerializer.Deserialize<CommandMetadata>(str, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        });
        Assert.NotNull(metadata);
    }
    
    [Test]
    public void LoadDocs()
    {
        var str = FadeBasicCommandsMetaData.COMMANDS_JSON;
        var metadata = JsonSerializer.Deserialize<CommandMetadata>(str, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        });

        var docs = ProjectDocMethods.LoadDocs<MarkdownDocParser>(new List<CommandMetadata> { metadata });
        
    }
    
}