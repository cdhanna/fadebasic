using System.Text.Json;
using ApplicationSupport.Docs;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Lib.Standard;
using StandardCommands = FadeBasic.Lib.Standard.StandardCommands;

namespace Tests.ApplicationSupport;

public class DocHostTests
{
    [Test]
    public async Task HostTest()
    {
        
        var str2 = ConsoleCommandsMetaData.COMMANDS_JSON;
        var metadata2 = JsonSerializer.Deserialize<CommandMetadata>(str2, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        });
        
        var str3 = StandardCommandsMetaData.COMMANDS_JSON;
        var metadata3 = JsonSerializer.Deserialize<CommandMetadata>(str3, new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
        });

        var docs = ProjectDocMethods.LoadDocs<HtmlDocParser>(new List<CommandMetadata> { metadata2,metadata3 });
        
        var host = new DocHost(docs, new DocHostOptions
        {
            port = 8081,
            onLogException = (ex) => throw ex
        });
        
        var server = host.Start();
        
        await Task.Delay(100);
        await host.Kill();
        
        await server;

    }
}