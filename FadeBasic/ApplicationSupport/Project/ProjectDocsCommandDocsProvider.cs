// Adapter: ProjectDocs ⟶ FadeBasic.LSP.Core.ICommandDocsProvider.
//
// Lets any host that already has a ProjectDocs (native LSP, WebRuntime,
// docs site) plug into LSP.Core's hover/completion handlers without
// duplicating the XML-doc parsing pipeline. The lookup is by
// `CommandInfo.sig`, matching the key ProjectDocs builds.

using FadeBasic.LSP.Core;
using FadeBasic.Virtual;

namespace FadeBasic.ApplicationSupport.Project;

public sealed class ProjectDocsCommandDocsProvider : ICommandDocsProvider
{
    private readonly ProjectDocs _docs;
    private readonly Func<string, string>? _urlForCommand;

    public ProjectDocsCommandDocsProvider(ProjectDocs docs, Func<string, string>? urlForCommand = null)
    {
        _docs = docs;
        _urlForCommand = urlForCommand;
    }

    public ICommandDocs? Lookup(CommandInfo command)
    {
        if (_docs?.map == null) return null;
        if (!_docs.map.TryGetValue(command.sig ?? string.Empty, out var found)) return null;
        return new CommandDocsAdapter(found, _urlForCommand);
    }

    private sealed class CommandDocsAdapter : ICommandDocs
    {
        private readonly CommandDocs _src;
        private readonly Func<string, string>? _urlForCommand;
        public CommandDocsAdapter(CommandDocs src, Func<string, string>? urlForCommand)
        {
            _src = src;
            _urlForCommand = urlForCommand;
        }
        public string? Summary => _src.methodDocs?.summary;
        public string? Returns => _src.methodDocs?.returns;
        public string? Remarks => _src.methodDocs?.remarks;
        public IReadOnlyList<ICommandParameterDoc> Parameters =>
            _src.methodDocs?.parameters?.Select(p => (ICommandParameterDoc)new ParamAdapter(p)).ToList()
            ?? new List<ICommandParameterDoc>();
        public IReadOnlyList<string> Examples => _src.methodDocs?.examples ?? new List<string>();
        public string? Url => _urlForCommand?.Invoke(_src.commandName ?? string.Empty);
    }

    private sealed class ParamAdapter : ICommandParameterDoc
    {
        private readonly XmlDocMethodParameter _src;
        public ParamAdapter(XmlDocMethodParameter src) { _src = src; }
        public string? Name => _src.name;
        public string? Body => _src.body;
    }
}
