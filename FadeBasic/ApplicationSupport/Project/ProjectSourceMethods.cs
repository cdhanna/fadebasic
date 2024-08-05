using System.Text;

namespace FadeBasic.ApplicationSupport.Project;

public static class ProjectSourceMethods
{

    public static SourceMap CreateSourceMap(List<string> sourceFilePaths, Func<string, string[]> reader = null)
    {
        if (reader == null)
        {
            reader = File.ReadAllLines;
        }
        
        var sb = new StringBuilder();
        var totalLines = 0;
        var fileToLineRange = new List<(string, Range)>();
        for (var i = 0; i < sourceFilePaths.Count; i++)
        {
            var file = sourceFilePaths[i];
            var lines = reader(file);
            var startLine = totalLines;
            var endLine = totalLines + lines.Length;
            totalLines += lines.Length;
            fileToLineRange.Add((file, new Range(startLine, endLine)));
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                sb.AppendLine(lines[lineNumber]);
            }
            
        }

        return new SourceMap(sb.ToString(), fileToLineRange);
    }
    public static SourceMap CreateSourceMap(this ProjectContext project, Func<string, string[]> reader=null)
    {
        return CreateSourceMap(project.absoluteSourceFiles, reader);
    }
    
    // public static SourceMap CreateSourceMap(this ProjectContext project)
    // {
    //     // need the abliity to convert back from final source into FILE(uri), and line/char
    //     var sb = new StringBuilder();
    //     var fileToRange = new Dictionary<string, Range>();
    //     var rollingIndex = 0;
    //     for (var i = 0; i < project.absoluteSourceFiles.Count; i++)
    //     {
    //         var file = project.absoluteSourceFiles[i];
    //         var content = File.ReadAllText(file);
    //
    //         var startIndex = rollingIndex;
    //         sb.Append(content);
    //         rollingIndex = sb.Length;
    //         var endIndex = rollingIndex;
    //         fileToRange[file] = new Range(startIndex, endIndex);
    //         sb.AppendLine(); // blank line
    //     }
    //
    //     var fullSource = sb.ToString();
    //     return new SourceMap(fullSource, fileToRange);
    // }
}