using System.Diagnostics;
using FadeBasic.ApplicationSupport.Project;
using Microsoft.Build.Locator;

namespace Tests.ApplicationSupport;

public class LoadTests
{
    [Test]
    public void Load()
    {
        ProjectLoader.Initialize();
        var project = ProjectLoader.LoadProjectFromFile("ApplicationSupport/Fixtures/simpleParse.yaml");

        var sw = new Stopwatch();
        sw.Start();
        var datas = ProjectBuilder.LoadCommandMetadata(project);
        sw.Stop();
        Console.WriteLine(sw.ElapsedMilliseconds);
    }
}