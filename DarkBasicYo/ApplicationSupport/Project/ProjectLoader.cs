using System.Text.Json;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DarkBasicYo.ApplicationSupport.Project;

public static class ProjectLoader
{
    public static void Initialize()
    {
        MSBuildLocator.RegisterDefaults();
    }
    
    public static ProjectContext LoadProjectFromFile(string projectPath)
    {
        var yaml = File.ReadAllText(projectPath);
        return LoadProject(yaml, Path.GetDirectoryName(projectPath));
    }
    
    public static ProjectContext LoadProject(string yaml, string projectDir)
    {
        var buildEngine = new ProjectCollection();
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        
        var model = deserializer.Deserialize<ProjectModel>(yaml);

        projectDir = Path.GetFullPath(projectDir);

        var projectLibraries = new List<ProjectCommandSource>();
        foreach (var reference in model.projectReferences)
        {
            var absProjectPath = Path.Combine(projectDir, reference.csProjPath.Trim());
            var msProject = buildEngine.LoadProject(absProjectPath);
            var outDir = msProject.GetProperty("OutDir");
            var asmName = msProject.GetProperty("AssemblyName");
            var root = Path.GetDirectoryName(absProjectPath);
            var absDllPath = Path.Combine(root, outDir.EvaluatedValue.Replace("\\", "/"), asmName.EvaluatedValue.Replace("\\", "/") + ".dll");
            var dllExists = Path.Exists(absDllPath);
            projectLibraries.Add(new ProjectCommandSource
            {
                commandClasses = reference.classNames,
                absoluteProjectPath = absProjectPath,
                absoluteOutputDllPath = absDllPath,
                hasBuiltDll = dllExists
            });
        }

        var sourceFiles = new List<string>();
        foreach (var source in model.sourceFiles)
        {
            var absSource = Path.GetFullPath(source);
            sourceFiles.Add(absSource);
        }

        var absLaunchProject = Path.Combine(projectDir,model.launchProject);
        
        var ctx = new ProjectContext
        {
            name = model.name,
            absoluteContextDir = projectDir,
            absoluteLaunchCsProjPath = absLaunchProject,
            targetFramework = model.targetFramework,
            absoluteSourceFiles = sourceFiles,
            projectLibraries = projectLibraries
        };
        return ctx;
    }


    public class ProjectModel
    {
        public string name;
        public string targetFramework;
        public string launchProject;
        public List<string> sourceFiles = new List<string>();
        public List<ProjectCsharpReference> projectReferences = new List<ProjectCsharpReference>();
    }

    public class ProjectCsharpReference
    {
        public string csProjPath;
        public List<string> classNames = new List<string>();
    }
}