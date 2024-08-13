using System.Diagnostics;
using System.Text.Json;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using Microsoft.Build.Locator;

namespace Tests.ApplicationSupport;

public class LoadTests
{
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

        var docs = ProjectDocMethods.LoadDocs(new List<CommandMetadata> { metadata });
        
    }
    
    [Test]
    public void Load()
    {
        ProjectLoader.Initialize();
        var project = ProjectLoader.LoadCsProject("/Users/chrishanna/Documents/SillyConsumerTest/Demo/Demo.csproj");

        var sw = new Stopwatch();
        sw.Start();
        var datas = ProjectBuilder.LoadCommandMetadata(project);
        sw.Stop();
        Console.WriteLine(sw.ElapsedMilliseconds);
    }
}