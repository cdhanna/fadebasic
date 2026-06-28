// Signature help — thin adapter over FadeBasic.LSP.Core.Handlers.SignatureHelpHandler.
//
// Audit vs the pre-refactor native handler:
//   * Both walk to the innermost CommandStatement/CommandExpression at the
//     cursor and, failing that, walk tokens back to the enclosing `(` to
//     handle the "user just typed name(" case.
//   * Both build the same "name(arg1, arg2, …)" label and parameter list.
//   * The old native handler additionally consulted ProjectDocs for per-param
//     documentation. Core's interface doesn't yet expose that — we therefore
//     post-fill `Documentation` here from ProjectDocs when available, so the
//     hover behavior previously seen by users is preserved.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using LSP.Services;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using CoreSigHandler = FadeBasic.LSP.Core.Handlers.SignatureHelpHandler;

namespace LSP.Handlers;

public class SignatureHelpHandler : SignatureHelpHandlerBase
{
    private readonly CompilerService _compiler;
    private readonly ProjectService _project;

    public SignatureHelpHandler(CompilerService compiler, ProjectService project)
    {
        _compiler = compiler;
        _project = project;
    }

    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability,
        ClientCapabilities clientCapabilities) => new SignatureHelpRegistrationOptions
    {
        DocumentSelector = TextDocumentSelector.ForLanguage(FadeBasicConstants.FadeBasicLanguage),
        TriggerCharacters = new Container<string>("(", ","),
        RetriggerCharacters = new Container<string>(","),
    };

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        if (!_compiler.TryGetProjectsFromSource(request.TextDocument.Uri, out var units) || units.Count == 0)
            return Task.FromResult(default(SignatureHelp?));

        var unit = units[0];

        // Map the URI-space position into the compiled unit's coordinate space.
        if (!unit.sourceMap.TryGetMappedLocation(
                request.TextDocument.Uri.GetFileSystemPath(),
                request.Position.Line,
                request.Position.Character,
                out _,
                out var mappedLine,
                out var mappedChar))
        {
            return Task.FromResult(default(SignatureHelp?));
        }

        // Pick up project docs so we can fill per-parameter Documentation.
        ProjectDocs? projectDocs = null;
        if (_compiler.TryGetProjectContexts(request.TextDocument.Uri, out var ctxs)
            && _project.TryGetProject(ctxs[0], out var projectData))
        {
            projectDocs = projectData.Item2.docs;
        }

        var doc = CoreAdapter.ToDocument(unit, request.TextDocument.Uri.ToString(), projectDocs);
        var core = CoreSigHandler.Compute(doc, mappedLine, mappedChar);
        if (core == null || core.Signatures == null || core.Signatures.Count == 0)
            return Task.FromResult(default(SignatureHelp?));

        var sigs = new List<SignatureInformation>(core.Signatures.Count);
        foreach (var s in core.Signatures)
        {
            var paramInfos = new List<ParameterInformation>();
            for (var i = 0; i < (s.Parameters?.Count ?? 0); i++)
            {
                var p = s.Parameters![i];
                paramInfos.Add(new ParameterInformation
                {
                    Label = new ParameterInformationLabel(p.Label ?? string.Empty),
                    Documentation = string.IsNullOrEmpty(p.Documentation)
                        ? null
                        : new StringOrMarkupContent(new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = p.Documentation!,
                        }),
                });
            }
            sigs.Add(new SignatureInformation
            {
                Label = s.Label ?? string.Empty,
                Documentation = string.IsNullOrEmpty(s.Documentation)
                    ? null
                    : new StringOrMarkupContent(new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = s.Documentation!,
                    }),
                Parameters = new Container<ParameterInformation>(paramInfos),
                ActiveParameter = s.ActiveParameter,
            });
        }

        return Task.FromResult<SignatureHelp?>(new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(sigs),
            ActiveSignature = core.ActiveSignature,
            ActiveParameter = core.ActiveParameter,
        });
    }
}
