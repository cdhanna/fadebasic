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
        var project = ProjectLoader.LoadCsProject("/Users/chrishanna/Documents/SillyConsumerTest/Demo/Demo.csproj");

        var sw = new Stopwatch();
        sw.Start();
        var datas = ProjectBuilder.LoadCommandMetadata(project);
        sw.Stop();
        Console.WriteLine(sw.ElapsedMilliseconds);
    }
}