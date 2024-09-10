using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Virtual;

namespace ApplicationSupport.Docs;

public class DocHostOptions
{
    public Action<Exception> onLogException;
    public int port;
}

public class DocHost
{
    private const string CONTROL_TERMINATE = "/control/terminate";
    
    private ProjectDocs _docs;
    private readonly DocHostOptions _options;

    private Dictionary<CommandDocs, List<CommandDocs>> _overloads = new Dictionary<CommandDocs, List<CommandDocs>>();
    
    public int Port { get; private set; }
    private Task _runningTask;

    public string GetUrlForCommand(string group, string command)
    {
        var url = $"http://localhost:{Port}/command/{HttpUtility.UrlPathEncode(group)}/{HttpUtility.UrlPathEncode(command)}";
        return url;
    }

    public async Task Start()
    {
        if (_runningTask == null)
        {
            _runningTask = Task.Run(() =>
            {
                try
                {
                    Run();
                    _runningTask = null;
                }
                catch (Exception ex)
                {
                    _options.onLogException?.Invoke(ex);
                }
            });
        }
        else
        {
            throw new InvalidOperationException("Cannot start the docHost because it is already running");
        }

        await _runningTask;
    }

    public static int FreeTcpPort()
    {
        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
    
    public DocHost(ProjectDocs docs, DocHostOptions options)
    {
        ChangeData(docs);
        _options = options;
        if (options.port == 0)
        {
            Port = FreeTcpPort();
        }
        else
        {
            Port = options.port;
        }
        
    }

    public async Task Kill()
    {
        var client = new HttpClient();
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{Port}{CONTROL_TERMINATE}"));
    }
    
    private void Run()
    {
        // Define the HTTP listener
        HttpListener listener = new HttpListener();
        var url = $"http://*:{Port}/";
        listener.Prefixes.Add(url); // Set the URL prefix
        listener.Start();

        // Serve files in a loop
        while (true)
        {
            HttpListenerContext context = listener.GetContext();
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            if (request.RawUrl?.StartsWith(CONTROL_TERMINATE) ?? false)
            {
                // terminate.
                response.StatusCode = (int)HttpStatusCode.OK;
                response.OutputStream.Close();
                return; 
            }
            
            // string filePath = GetFilePath(request.RawUrl);
            if (TryGetBytes(_docs, _overloads, request.RawUrl, out var content, out var contentType))
            {
                response.ContentType = contentType;
                response.ContentLength64 = content.Length;
                response.OutputStream.Write(content, 0, content.Length);
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                content = Encoding.UTF8.GetBytes("404 - File Not Found");
                response.ContentType = "text/plain";
                response.ContentLength64 = content.Length;
                response.OutputStream.Write(content, 0, content.Length);
            }

            response.OutputStream.Close();
        }
    }

    static bool TryGetBytes(ProjectDocs _docs, Dictionary<CommandDocs, List<CommandDocs>> overloads, string url, out byte[] bytes, out string contentType)
    {
        bytes = new byte[] { };
        contentType = "";

        
        bool ReadStream(string logicalResourceName, out byte[] bytes, out string contentType)
        {
            bytes = new byte[] { };
            contentType = GetContentType(logicalResourceName);

            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalResourceName);
            if (stream == null) return false;
            using var memoryStream = new MemoryStream();

            stream.CopyTo(memoryStream);
            bytes = memoryStream.ToArray();
            return true;
        }

        var decodedUrl = HttpUtility.UrlDecode(url);
        var route = decodedUrl.Split("/", StringSplitOptions.RemoveEmptyEntries);
        switch (route)
        {
            // case var part when part.StartsWith("/command")
            case ["command", var groupName, var commandName]:
                if (!ReadStream("docs/index.html", out bytes, out contentType))
                {
                    return false;
                }
                
                // make sure the commandName is valid
                if (!TryDoTemplate(_docs, overloads, groupName, commandName, bytes, out bytes))
                {
                    return false;
                }
                
                // convert the entire byte-stream
                return true;
            case []:
                if (!ReadStream("docs/index.html", out bytes, out contentType))
                {
                    return false;
                }
                
                // make sure the commandName is valid
                if (!TryDoTemplate(_docs, overloads, null, null, bytes, out bytes))
                {
                    return false;
                }
                
                // convert the entire byte-stream
                return true;
            case ["styles.css"]:
                return ReadStream("docs/styles.css", out bytes, out contentType);
            case ["F-trans.png"]:
                return ReadStream("docs/f.png", out bytes, out contentType);
            default:
                return false;
        }

    }

    public void ChangeData(ProjectDocs docs)
    {
        _docs = docs;
        _overloads.Clear();
        // there may be method overloads, and we should group those
        foreach (var group in _docs.groups)
        {
            var local = new Dictionary<string, List<CommandDocs>>();
            foreach (var command in group.commands)
            {
                if (!local.TryGetValue(command.commandName, out var existing))
                {
                    existing = local[command.commandName] = new List<CommandDocs>() { command };
                }
                else
                {
                    existing.Add(command);
                }

                _overloads[command] = existing;
            }
        }
    }

    static bool TryGetTemplateText(ProjectDocs docs, Dictionary<CommandDocs, List<CommandDocs>> overloads, CommandGroupDocs groupDoc, CommandDocs commandDoc, string templateName, out string result)
    {
        result = "";
        switch (templateName)
        {
            case "COMMANDS":
                // here, we need to fill in a ul group.
                // <li><strong>Console</strong>
                //     <ul>
                //     <li><a href="/command/cls" >cls</a></li>
                //     <li><a href="/command/print" >print</a></li>
                //     </ul>
                //     </li>
                var sb = new StringBuilder();
                var seen = new HashSet<string>();
                foreach (var group in docs.groups)
                {
                    sb.Append("<li><strong>");
                    sb.Append(group.title);
                    sb.AppendLine("</strong><ul>");
                    foreach (var doc in group.commands)
                    {
                        if (seen.Contains(doc.commandName)) continue;
                        seen.Add(doc.commandName);
                        
                        sb.Append("<li><a href=\"/command/");
                        sb.Append(group.title);
                        sb.Append("/");
                        sb.Append(doc.commandName);
                        sb.Append("\">");
                        sb.Append(doc.commandName);
                        sb.AppendLine("</a></li>");
                    }
                    sb.AppendLine("</ul></li>");
                }

                result = sb.ToString();
                
                return true;
            case "TITLE" when commandDoc != null:
                result = commandDoc.commandName;
                return true;
            case "DESC" when commandDoc != null:
                sb = new StringBuilder();
                
                // append _all_ overloads...s
                var versions = overloads[commandDoc];
                if (versions.Count == 1)
                {
                    AppendCommand(sb, commandDoc);
                }
                else
                {
                    for (var i = 0 ; i < versions.Count; i ++)
                    {
                        sb.AppendLine($"<p class=\"overload-sep\">(overload {i+1})</p>");
                        AppendCommand(sb, versions[i]);
                    }
                }
                
                
                result = sb.ToString();
                return true;
        }

        return false;
    }

    static void AppendCommand(StringBuilder sb, CommandDocs commandDoc)
    {
        sb.AppendLine(commandDoc.methodDocs.summary);

        if (commandDoc.methodDocs.parameters.Count > commandDoc.command.parameters.Count)
        {
            sb.Append("(invalid number of parameter docs)");
        }
        else if (commandDoc.command.parameters.Count == 0)
        {
            sb.AppendLine("<h4> Parameters </h4>");
            sb.AppendLine("<p><i>none</i></p>");
        }
        else if (commandDoc.command.parameters.Count > 0)
        {
            sb.AppendLine("<h4> Parameters </h4>");
            for (var i = 0; i < commandDoc.command.parameters.Count; i++)
            {
                var arg = commandDoc.command.parameters[i];
                var parameter = i < commandDoc.methodDocs.parameters.Count
                    ? commandDoc.methodDocs.parameters[i]
                    : default;
                sb.Append("<h5>");
                if (VmUtil.TryGetVariableTypeDisplay(arg.typeCode, out var type))
                {
                    sb.Append($"<code>{type}</code> ");
                }
                else
                {
                    sb.Append("<i>unknown</i> ");
                }

                if (arg.isOptional)
                {
                    sb.Append("<i>(optional)</i> ");
                }

                if (arg.isRef)
                {
                    sb.Append("<i>(ref)</i> ");
                }

                if (parameter != default)
                {
                    sb.Append(parameter.name);
                    sb.AppendLine("</h5>");

                    sb.Append(Environment.NewLine);
                    sb.AppendLine(parameter.body.Trim());
                }
                else
                {
                    sb.AppendLine("</h5>");
                    sb.AppendLine("_(doc missing)_");
                }
            }
        }

        if (commandDoc.command.returnTypeCode != TypeCodes.VOID)
        {
            sb.Append(Environment.NewLine);
            sb.Append("<h4> Returns");
            if (VmUtil.TryGetVariableTypeDisplay(commandDoc.command.returnTypeCode, out var type))
            {
                sb.Append($" <code>{type}</code>");
            }

            sb.AppendLine("</h4>");

            if (!string.IsNullOrEmpty(commandDoc.methodDocs.returns))
            {
                sb.Append(Environment.NewLine);
                sb.AppendLine(commandDoc.methodDocs.returns.Trim());
            }
        }

        
        if (commandDoc.methodDocs.examples.Count > 0)
        {
            sb.AppendLine("<h4> Examples </h4>");
            foreach (var example in commandDoc.methodDocs.examples)
            {
                sb.AppendLine("<div class=\"example\">");
                sb.AppendLine(example);
                sb.AppendLine("</div>");
            }
        }

        
        if (!string.IsNullOrEmpty(commandDoc.methodDocs.remarks))
        {
            sb.Append(Environment.NewLine);
            sb.AppendLine("<h4> Remarks </h4>");
            sb.AppendLine(commandDoc.methodDocs.remarks.Trim());
        }

    }

    static bool TryDoTemplate(ProjectDocs docs, Dictionary<CommandDocs, List<CommandDocs>> overloads, string groupName, string command, byte[] rawBytes, out byte[] finalBytes)
    {
        finalBytes = rawBytes;

        CommandGroupDocs group = null;
        CommandDocs commandDoc = null;
        // first, we need to see if the command exists; otherwise it will be a 404 page. 
        if (!string.IsNullOrEmpty(groupName))
        {
            group = docs.groups.FirstOrDefault(g =>
                string.Equals(groupName, g.title, StringComparison.InvariantCultureIgnoreCase));
            if (group == null)
                return false;

            if (!string.IsNullOrEmpty(command))
            {
                commandDoc = group.commands.FirstOrDefault(c =>
                    string.Equals(command, c.commandName, StringComparison.InvariantCultureIgnoreCase));
                if (commandDoc == null)
                    return false;
            }
        }

        // now we need to template the HTML.
        var html = Encoding.UTF8.GetString(rawBytes);
        var htmlSpan = html.AsSpan();
        for (var i = 0; i < htmlSpan.Length; i++)
        {
            var slice = htmlSpan.Slice(i);
            if (!slice.StartsWith("<!-- TEMPLATE_START_"))
                continue;
            
            // ah this is a special replacement!!!
            var templateSlice = slice.Slice("<!-- TEMPLATE_START_".Length);
            // need to read until we find the --> closing section of the opening tag
            for (var j = 0; j < templateSlice.Length; j++)
            {
                if (templateSlice[j] != ' ') continue;
                
                // this must be the end of the opening label.
                var templateName = templateSlice.Slice(0, j).ToString();

                var fragmentSlice = templateSlice.Slice(templateName.Length + " -->".Length);
                
                // at this point, we need to find the closing tag
                for (var k = 0; k < fragmentSlice.Length; k++)
                {
                    var endSlice = fragmentSlice.Slice(k);
                    if (!endSlice.StartsWith("<!-- TEMPLATE_END -->"))
                        continue;
                    
                    // hey we found the end too!
                    var pre = htmlSpan.Slice(0, i);
                    var post = endSlice.Slice("<!-- TEMPLATE_END -->".Length);

                    if (TryGetTemplateText(docs, overloads, group, commandDoc, templateName, out var replacement))
                    {
                        htmlSpan = string.Concat(pre, replacement, post);
                    }
                    else
                    {
                        htmlSpan = string.Concat(pre, post);
                    }
                    
                    break;
                }

                break;
            }
            
            
        }

        html = htmlSpan.ToString();
        
        finalBytes = Encoding.UTF8.GetBytes(html);
        return true;
    }

    // Determine the content type based on the file extension
    static string GetContentType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLower();
        return extension switch
        {
            ".html" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };
    }
}