using FadeBasic.Sdk;

namespace FadeBasic.ApplicationSupport.Project;

public static class ProjectSourceMethods
{
    public static SourceMap CreateSourceMap(this ProjectContext project, Func<string, string[]> reader=null)
    {
        return SourceMap.CreateSourceMap(project.absoluteSourceFiles, reader);
    }
}