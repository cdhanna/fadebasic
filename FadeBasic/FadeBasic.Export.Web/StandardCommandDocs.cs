// Builds an ICommandDocsProvider for Core's HoverHandler by reusing the
// existing ApplicationSupport parsing pipeline:
//
//   <Lib>CommandsMetaData.COMMANDS_JSON (raw XML doc strings, source-generator
//   output)
//     → CommandMetadata (System.Text.Json)
//     → ProjectDocs (ProjectDocMethods.LoadDocs<MarkdownDocParser>)
//     → ICommandDocsProvider (ProjectDocsCommandDocsProvider)
//
// Callers pass every COMMANDS_JSON blob that's currently live — Standard
// plus whatever assemblies were dynamically registered. The pipeline
// merges them into one ProjectDocs map keyed by callName + sig.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.LSP.Core;

namespace FadeBasic.Export.Web;

internal static class StandardCommandDocs
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    // System.Text.Json reflects ctors + fields off these types at deserialize
    // time. In the Release/trimmed publish the trimmer drops the default
    // ctors (so STJ throws "Deserialization of types without a parameterless
    // constructor … is not supported") and the public fields (so even with
    // ctors, every field deserializes to default). These dependencies pin
    // both. Without them every command in the Help tab renders as just a
    // signature header, with no summary, parameters, or examples.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(CommandMetadata))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(ProjectCommandMetadata))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(ProjectCommandParameterMetedata))]
    public static ICommandDocsProvider Build(params string[] commandsJsonBlobs)
    {
        try
        {
            var metas = new List<CommandMetadata>(commandsJsonBlobs.Length);
            foreach (var json in commandsJsonBlobs)
            {
                if (string.IsNullOrEmpty(json)) continue;
                var m = JsonSerializer.Deserialize<CommandMetadata>(json, _opts);
                if (m != null) metas.Add(m);
            }
            if (metas.Count == 0) return null!;
            var docs = metas.LoadDocs<MarkdownDocParser>();
            return new ProjectDocsCommandDocsProvider(docs);
        }
        catch
        {
            // Best-effort — hover/help fall back to the basic signature header.
            return null!;
        }
    }
}
