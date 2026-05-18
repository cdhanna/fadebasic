// Builds an ICommandDocsProvider for Core's HoverHandler by reusing the
// existing ApplicationSupport parsing pipeline:
//
//   StandardCommandsMetaData.COMMANDS_JSON (raw XML doc strings)
//     → CommandMetadata (System.Text.Json)
//     → ProjectDocs (ProjectDocMethods.LoadDocs<MarkdownDocParser>)
//     → ICommandDocsProvider (ProjectDocsCommandDocsProvider)

using System.Collections.Generic;
using System.Text.Json;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Lib.Standard;
using FadeBasic.LSP.Core;

namespace WebRuntime;

internal static class StandardCommandDocs
{
    public static ICommandDocsProvider Build()
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<CommandMetadata>(
                StandardCommandsMetaData.COMMANDS_JSON,
                new JsonSerializerOptions
                {
                    IncludeFields = true,
                    PropertyNameCaseInsensitive = true,
                });
            if (metadata == null) return null!;

            var docs = new List<CommandMetadata> { metadata }
                .LoadDocs<MarkdownDocParser>();
            return new ProjectDocsCommandDocsProvider(docs);
        }
        catch
        {
            // Best-effort — hover falls back to the basic signature header.
            return null!;
        }
    }
}
