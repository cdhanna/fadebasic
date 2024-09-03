using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace FadeBasic.ApplicationSupport.Project;

public class XmlDocMethod
{
    public string summary;
}

public static class ProjectDocMethods
{
    // public static ProjectDocs LoadDocs(this ProjectContext context)
    // {
    //     var commands = ProjectBuilder.LoadCommandMetadata(context);
    //     return LoadDocs(commands);
    // } 


    public static XmlDocMethod ParseMethodDocs(string xml)
    {
        var res = new XmlDocMethod();
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var nodes = doc.Nodes().ToList();
        foreach (var node in nodes)
        {
            switch (node)
            { 
                case XElement element when element.Name == "summary":
                    res.summary = ParseHtmlBlock(element);
                    break;
            }
        }
        return res;
    }

    public static string ParseHtmlBlock(XElement summary)
    {
        var sb = new StringBuilder();
        ParseHtmlBlock(summary, sb);
        return sb.ToString();
    }
    public static void ParseHtmlBlock(XElement summary, StringBuilder htmlBuilder=null, bool replaceNewlines=true)
    {
        var nodes = summary.Nodes();
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    var content = text.Value.TrimEnd('\n').TrimStart('\n');
                    if (replaceNewlines)
                    {
                        content = content.Replace(Environment.NewLine, "");
                    }
                    htmlBuilder.Append(content);
                    break;
                case XElement element when element.Name == "para":
                    htmlBuilder.Append("<p>");
                    ParseHtmlBlock(element, htmlBuilder);
                    htmlBuilder.Append("</p>");
                    break;
                case XElement element when element.Name == "b":
                    htmlBuilder.Append("<b>");
                    ParseHtmlBlock(element, htmlBuilder);
                    htmlBuilder.Append("</b>");
                    break;
                case XElement element when element.Name == "i":
                    htmlBuilder.Append("<i>");
                    ParseHtmlBlock(element, htmlBuilder);
                    htmlBuilder.Append("</i>");
                    break;
                case XElement element when element.Name == "c":
                    htmlBuilder.Append("<code>");
                    ParseHtmlBlock(element, htmlBuilder);
                    htmlBuilder.Append("</code>");
                    break;
                case XElement element when element.Name == "code":
                    htmlBuilder.Append("<pre>");
                    ParseHtmlBlock(element, htmlBuilder, replaceNewlines: false);
                    htmlBuilder.Append("</pre>");
                    break;
                case XElement element when element.Name == "list":
                    var listType = element.Attribute("type")?.Value.ToLowerInvariant() ?? "bullet";
                    var listOpenTag = listType == "bullet" ? "<ul>" : "<ol>";
                    var listCloseTag = listType == "bullet" ? "</ul>" : "</ol>";

                    var itemNodes = element.Nodes()
                        .Where(x => x is XElement itemElement && itemElement.Name == "item")
                        .Cast<XElement>()
                        .ToList();
                    htmlBuilder.Append(listOpenTag);
                    foreach (var itemNode in itemNodes)
                    {
                        htmlBuilder.Append("<li>");
                        ParseHtmlBlock(itemNode, htmlBuilder);
                        htmlBuilder.Append("</li>");
                    }
                    htmlBuilder.Append(listCloseTag);
                    break;
                default:
                    break;
            }
        }
    }
    
    public static ProjectDocs LoadDocs(this List<CommandMetadata> metadatas)
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
                doc.commandName = command.callName;

                var xml = command.docString;
                xml = "<xml>" + xml + "</xml>";
                try
                {
                    // var reader = new StreamReader()
                    ParseXml(xml);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("failed to parse xml: " + ex.Message);
                }

                // TODO: parse xml out into markdown?
            }
        }

        return docs;
    }

    static void ParseXml(string xml)
    {
        var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            IgnoreWhitespace = false,
            IgnoreComments = true,
        });

        var completedSnippets = new List<DocHtmlSnippet>();
        var snippets = new Stack<DocHtmlSnippet>();
        DocHtmlSnippet snippet;
        /*
         * desired output, Json
         * {
         *  "summary": hello _there_ [Link](to thing)
         * }
         * 
         */
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    
                    
                    
                    snippet = new DocHtmlSnippet
                    {
                        type = reader.Name
                    };
                    for (var i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        snippet.attributes[reader.Name] = reader.Value;
                    }

                    reader.MoveToElement();
                    snippets.Push(snippet);
                    break;
                case XmlNodeType.Text:
                    if (!snippets.TryPeek(out snippet)) break;
                    var text = reader.Value;
                    snippet.html += text;
                    break;
                case XmlNodeType.EndElement:
                    snippet = snippets.Pop();
                    completedSnippets.Add(snippet);

                    
                    break;
                default:
                    Console.WriteLine("Other node {0} with value {1}",
                        reader.NodeType, reader.Value);
                    break;
            }
        }
        
        Console.WriteLine("done");
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
    public string commandName;
}

public class DocText
{
    public List<string> paragraphs;
}



public class DocHtmlSnippet
{
    public string type;
    public Dictionary<string, string> attributes = new Dictionary<string, string>();
    public string html;
    // public DocHtmlSnippet 
}