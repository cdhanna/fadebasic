using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using FadeBasic.Virtual;

namespace FadeBasic.ApplicationSupport.Project;

public class XmlDocMethod
{
    public string summary;
    public List<XmlDocMethodParameter> parameters = new List<XmlDocMethodParameter>();
    public string returns;
    public List<string> examples = new List<string>();
    public string remarks;
}

public class XmlDocMethodParameter
{
    public string name;
    public string body;
}

public interface IDocParser
{
    void ConvertParagraph(StringBuilder sb, XElement element);
    void ConvertBold(StringBuilder sb, XElement element);
    void ConvertItalic(StringBuilder sb, XElement element);
    void ConvertCode(StringBuilder sb, XElement element);
    void ConvertCodeBlock(StringBuilder sb, XElement element);
    void ConvertList(StringBuilder sb, XElement element, string listType);
}

public class MarkdownDocParser : IDocParser
{
    public void ConvertParagraph(StringBuilder sb, XElement element)
    {
        sb.Append(Environment.NewLine);
        sb.Append(Environment.NewLine);
        ProjectDocMethods.ParseBlock(this, element, sb);
    }

    public void ConvertBold(StringBuilder sb, XElement element)
    {
        sb.Append("**");
        ProjectDocMethods.ParseBlock(this, element, sb, trimText: true);
        sb.Append("**");
    }

    public void ConvertItalic(StringBuilder sb, XElement element)
    {
        sb.Append("_");
        ProjectDocMethods.ParseBlock(this, element, sb, trimText: true);
        sb.Append("_");
    }

    public void ConvertCode(StringBuilder sb, XElement element)
    {
        sb.Append("`");
        ProjectDocMethods.ParseBlock(this, element, sb, trimText: true);
        sb.Append("`");
    }

    public void ConvertCodeBlock(StringBuilder sb, XElement element)
    {
        sb.Append("```\n");
        ProjectDocMethods.ParseBlock(this, element, sb, replaceNewlines: false);
        sb.Append("\n```");
    }

    public void ConvertList(StringBuilder sb, XElement element, string listType)
    {
        var itemNodes = element.Nodes()
            .Where(x => x is XElement itemElement && itemElement.Name == "item")
            .Cast<XElement>()
            .ToList();

        sb.Append("\n");
        for (var i = 0; i < itemNodes.Count; i++)
        {
            var tag = listType == "bullet" ? "-" : ((i + 1)+".");
            sb.Append(tag);
            sb.Append(" ");
            ProjectDocMethods.ParseBlock(this, itemNodes[i], sb);
            sb.Append("\n");
        }
        sb.Append("\n");

    }
}

public class HtmlDocParser : IDocParser
{
    public void ConvertParagraph(StringBuilder sb, XElement element)
    {
        sb.Append("<p>");
        ProjectDocMethods.ParseBlock(this, element, sb);
        sb.Append("</p>");
    }

    public void ConvertBold(StringBuilder sb, XElement element)
    {
        sb.Append("<b>");
        ProjectDocMethods.ParseBlock(this,element, sb);
        sb.Append("</b>");
    }

    public void ConvertItalic(StringBuilder sb, XElement element)
    {
        sb.Append("<i>");
        ProjectDocMethods.ParseBlock(this,element, sb);
        sb.Append("</i>");
    }

    public void ConvertCode(StringBuilder sb, XElement element)
    {
        sb.Append("<code>");
        ProjectDocMethods.ParseBlock(this,element, sb);
        sb.Append("</code>");
    }

    public void ConvertCodeBlock(StringBuilder sb, XElement element)
    {
        sb.Append("<pre>");
        ProjectDocMethods.ParseBlock(this,element, sb, replaceNewlines: false);
        sb.Append("</pre>");
    }

    public void ConvertList(StringBuilder sb, XElement element, string listType)
    {
        // var listType = element.Attribute("type")?.Value.ToLowerInvariant() ?? "bullet";
        var listOpenTag = listType == "bullet" ? "<ul>" : "<ol>";
        var listCloseTag = listType == "bullet" ? "</ul>" : "</ol>";

        var itemNodes = element.Nodes()
            .Where(x => x is XElement itemElement && itemElement.Name == "item")
            .Cast<XElement>()
            .ToList();
        sb.Append(listOpenTag);
        foreach (var itemNode in itemNodes)
        {
            sb.Append("<li>");
            ProjectDocMethods.ParseBlock(this,itemNode, sb);
            sb.Append("</li>");
        }
        sb.Append(listCloseTag);
    }
}

public static class ProjectDocMethods
{
    public static XmlDocMethod ParseMethodDocsHtml(string xml) => ParseMethodDocs<HtmlDocParser>(xml);
    public static XmlDocMethod ParseMethodDocsMarkdown(string xml) => ParseMethodDocs<MarkdownDocParser>(xml);
    
    
    public static XmlDocMethod ParseMethodDocs<T>(string xml) where T : IDocParser, new()
    {
        var res = new XmlDocMethod();
        if (!xml.StartsWith("<root>"))
        {
            xml = "<root>" + xml + "</root>";
        }
        
        var doc = XDocument.Parse(xml, LoadOptions.None);
        
        
        
        var nodes = doc.Root.Nodes().ToList();
        foreach (var node in nodes)
        {
            switch (node)
            { 
                case XElement element when element.Name == "example":
                    var ex = ParseBlock<T>(element);
                    res.examples.Add(ex);
                    break;
                case XElement element when element.Name == "summary":
                    res.summary = ParseBlock<T>(element);
                    break;
                case XElement element when element.Name == "returns":
                    res.returns = ParseBlock<T>(element);
                    break;
                case XElement element when element.Name == "remarks":
                    res.remarks = ParseBlock<T>(element);
                    break;
                case XElement element when element.Name == "param":
                    var parameterName = element.Attribute("name")?.Value;
                    res.parameters.Add(new XmlDocMethodParameter
                    {
                        name = parameterName,
                        body = ParseBlock<T>(element)
                    });
                    break;
            }
        }
        return res;
    }

    public static string ParseHtmlBlock(XElement summary) => ParseBlock<HtmlDocParser>(summary);
    public static string ParseMdBlock(XElement summary) => ParseBlock<MarkdownDocParser>(summary);
    
    public static string ParseBlock<T>(XElement summary, bool replaceNewlines=true, bool trimText=false) where T : IDocParser, new()
    {
        var sb = new StringBuilder();
        var parser = new T();
        ParseBlock(parser, summary, sb, replaceNewlines, trimText);
        return sb.ToString();
    }
    
    public static void ParseBlock(IDocParser parser, XElement summary, StringBuilder sb=null, bool replaceNewlines=true, bool trimText=false)
    {
        var nodes = summary.Nodes();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    var content = text.Value.TrimEnd('\n').TrimStart('\n');
                    if (trimText)
                    {
                        content = content.Trim();
                    }
                    if (replaceNewlines)
                    {
                        content = content.Replace(Environment.NewLine, "");
                    }
                    sb.Append(content);
                    break;
                case XElement element when element.Name == "para":
                    parser.ConvertParagraph(sb, element);
                    break;
                case XElement element when element.Name == "b":
                    parser.ConvertBold(sb, element);
                    break;
                case XElement element when element.Name == "i":
                    parser.ConvertItalic(sb, element);
                    break;
                case XElement element when element.Name == "c":
                    parser.ConvertCode(sb, element);
                    break;
                case XElement element when element.Name == "code":
                    parser.ConvertCodeBlock(sb, element);
                    break;
                case XElement element when element.Name == "list":
                    var listType = element.Attribute("type")?.Value.ToLowerInvariant() ?? "bullet";
                    parser.ConvertList(sb, element, listType);
                    break;
                default:
                    break;
            }
        }
    }
    
    public static ProjectDocs LoadDocs<T>(this List<CommandMetadata> metadatas)
        where T : IDocParser, new()
    {
        var docs = new ProjectDocs();
        foreach (var metadata in metadatas)
        {
            var group = new CommandGroupDocs();
            docs.groups.Add(group);

            group.title = metadata.className;

            foreach (var command in metadata.commands)
            {
                var doc = new CommandDocs();
                group.commands.Add(doc);
                doc.command = command;
                doc.commandName = command.callName;
                doc.methodDocs = ParseMethodDocs<T>(command.docString);
            }
        }

        return docs;
    }

}

public class ProjectDocs
{
    public List<CommandGroupDocs> groups = new List<CommandGroupDocs>();
}

public class CommandGroupDocs
{
    public string title;
    public List<CommandDocs> commands = new List<CommandDocs>();
}

public class CommandDocs
{
    public ProjectCommandMetadata command;
    public string commandName;
    public XmlDocMethod methodDocs;
}
