namespace FadeBasic.Lsp
{
    public class PortableCompletionItem
    {
        public string Label;
        public string InsertText;
        public PortableCompletionKind Kind;
        public string Detail;
        public string SortText;
        public string FilterText;
        public string Documentation;
        public PortableInsertTextFormat InsertTextFormat;
        public bool TriggerParameterHints;
    }

    public enum PortableCompletionKind
    {
        Variable,
        Function,
        Interface,
        Keyword,
        Field,
        Class,
        Constant,
        Reference,
        Folder,
    }

    public enum PortableInsertTextFormat
    {
        PlainText = 1,
        Snippet = 2,
    }
}
