using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Virtual;
using Microsoft.Build.Framework;
using Task = Microsoft.Build.Utilities.Task;

namespace FadeBasic.Build
{
    public class FadeDocsTask : Task
    {
        [Required] public ITaskItem[] Commands { get; set; }
        [Required] public ITaskItem[] References { get; set; }
        [Required] public string OutputFile { get; set; }

        [Output] public string GeneratedDocsFile { get; set; }

        static Dictionary<string, string> PrepareAssemblyToDllMap(ITaskItem[] references)
        {
            var dict = new Dictionary<string, string>();
            foreach (var reference in references)
            {
                var assemblyName = reference.GetMetadata("Filename");
                var dllPath = reference.GetMetadata("FullPath");
                dict[assemblyName] = dllPath;
            }
            return dict;
        }

        public override bool Execute()
        {
            try
            {
                var dllTable = PrepareAssemblyToDllMap(References);

                var libMap = new Dictionary<string, List<string>>();
                for (var i = 0; i < Commands.Length; i++)
                {
                    var command = Commands[i];
                    var identity = command.GetMetadata("Identity");
                    if (dllTable.TryGetValue(identity, out var dllPath))
                    {
                        if (!libMap.TryGetValue(dllPath, out var lib))
                        {
                            libMap[dllPath] = lib = new List<string>();
                        }
                        lib.Add(command.GetMetadata("FullName"));
                    }
                }

                var libraries = new List<ProjectCommandSource>();
                foreach (var kvp in libMap)
                {
                    libraries.Add(new ProjectCommandSource
                    {
                        absoluteOutputDllPath = kvp.Key,
                        commandClasses = kvp.Value
                    });
                }

                if (libraries.Count == 0)
                {
                    Log.LogMessage(MessageImportance.Low, "[FADE] No command libraries resolved, skipping doc generation.");
                    return true;
                }

                var info = ProjectBuilder.LoadCommandMetadata(libraries);
                var markdown = GenerateMarkdown(info.docs);

                var dir = Path.GetDirectoryName(OutputFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(OutputFile, markdown);
                GeneratedDocsFile = OutputFile;

                Log.LogMessage(MessageImportance.Normal, "[FADE] Generated command docs: {0}", OutputFile);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("FadeDocsTask encountered an error. type=[{0}] message=[{1}] stack=[{2}]",
                    ex.GetType().Name, ex.Message, ex.StackTrace);
                return false;
            }
        }

        static string GenerateMarkdown(ProjectDocs docs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FadeBasic Command Reference");
            sb.AppendLine();

            foreach (var group in docs.groups)
            {
                sb.AppendLine($"## {group.title}");
                sb.AppendLine();

                foreach (var cmd in group.commands)
                {
                    sb.AppendLine($"### {cmd.commandName}");
                    sb.AppendLine();

                    if (!string.IsNullOrEmpty(cmd.methodDocs.summary))
                    {
                        sb.AppendLine(cmd.methodDocs.summary.Trim());
                        sb.AppendLine();
                    }

                    if (cmd.command.parameters.Count > 0)
                    {
                        sb.AppendLine("**Parameters**");
                        sb.AppendLine();
                        for (var i = 0; i < cmd.command.parameters.Count; i++)
                        {
                            var arg = cmd.command.parameters[i];
                            if (arg.isVm || arg.isRaw) continue;

                            var paramDoc = i < cmd.methodDocs.parameters.Count
                                ? cmd.methodDocs.parameters[i]
                                : null;

                            var line = new StringBuilder("- ");

                            if (VmUtil.TryGetVariableTypeDisplay(arg.typeCode, out var type))
                                line.Append($"`{type}` ");

                            if (arg.isOptional) line.Append("_(optional)_ ");
                            if (arg.isRef) line.Append("_(ref)_ ");

                            var name = paramDoc?.name ?? $"arg{i + 1}";
                            line.Append($"**{name}**");

                            var body = paramDoc?.body?.Trim();
                            if (!string.IsNullOrEmpty(body))
                                line.Append($" - {body}");

                            sb.AppendLine(line.ToString());
                        }
                        sb.AppendLine();
                    }

                    if (cmd.command.returnTypeCode != TypeCodes.VOID)
                    {
                        sb.Append("**Returns**");
                        if (VmUtil.TryGetVariableTypeDisplay(cmd.command.returnTypeCode, out var returnType))
                            sb.Append($" `{returnType}`");
                        if (!string.IsNullOrEmpty(cmd.methodDocs.returns))
                            sb.Append($" - {cmd.methodDocs.returns.Trim()}");
                        sb.AppendLine();
                        sb.AppendLine();
                    }

                    if (cmd.methodDocs.examples.Count > 0)
                    {
                        sb.AppendLine("**Examples**");
                        sb.AppendLine();
                        foreach (var example in cmd.methodDocs.examples)
                        {
                            sb.AppendLine(example.Trim());
                            sb.AppendLine();
                        }
                    }

                    if (!string.IsNullOrEmpty(cmd.methodDocs.remarks))
                    {
                        sb.AppendLine("**Remarks**");
                        sb.AppendLine();
                        sb.AppendLine(cmd.methodDocs.remarks.Trim());
                        sb.AppendLine();
                    }

                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
    }
}
