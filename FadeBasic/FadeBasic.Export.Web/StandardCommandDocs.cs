// Builds an ICommandDocsProvider for Core's HoverHandler by reusing the
// existing ApplicationSupport parsing pipeline:
//
//   <Lib>CommandsMetaData.COMMANDS_JSON (raw XML doc strings, source-generator
//   output)
//     → CommandMetadata (System.Text.Json)
//     → ProjectDocs (ProjectDocMethods.LoadDocs<MarkdownDocParser>)
//     → ICommandDocsProvider (ProjectDocsCommandDocsProvider)
//
// One file with two builders: web (StandardCommands only) and monogame
// (FadeMonoGameCommands + StandardCommands). Both go through the same
// LoadDocs pipeline, which accepts a list — so monogame just passes both
// metadata blobs.

using System.Collections.Generic;
using System.Text.Json;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Lib.Standard;
using FadeBasic.LSP.Core;

namespace FadeBasic.Export.Web;

internal static class StandardCommandDocs
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Docs for the 'web' command surface — WebCommands + StandardCommands.</summary>
    /// <remarks>
    /// WebCommands doesn't ship a source-generator metadata blob (it's tiny;
    /// hover falls back to the basic signature header for those). Standard
    /// is the only set with rich docs in this branch.
    /// </remarks>
    public static ICommandDocsProvider BuildWeb() =>
        BuildFromMetadata(StandardCommandsMetaData.COMMANDS_JSON);

    /// <summary>Docs for the 'monogame' command surface — placeholder until dynamic command registration is wired up.</summary>
    public static ICommandDocsProvider BuildMonoGame() =>
        BuildFromMetadata(StandardCommandsMetaData.COMMANDS_JSON);

    private static ICommandDocsProvider BuildFromMetadata(params string[] commandsJsonBlobs)
    {
        try
        {
            var metas = new List<CommandMetadata>(commandsJsonBlobs.Length);
            foreach (var json in commandsJsonBlobs)
            {
                var m = JsonSerializer.Deserialize<CommandMetadata>(json, _opts);
                if (m != null) metas.Add(m);
            }
            if (metas.Count == 0) return null!;
            var docs = metas.LoadDocs<MarkdownDocParser>();
            return new ProjectDocsCommandDocsProvider(docs);
        }
        catch
        {
            // Best-effort — hover falls back to the basic signature header.
            return null!;
        }
    }
}
