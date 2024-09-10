using FadeBasic.ApplicationSupport.Project;

namespace Tests.ApplicationSupport;

public class ParseXmlTests_Markdown
{
    // TODO: vscode needs markdown, so there should be a markdown converter as well. Consider strategy pattern.
    [Test]
    public void ParseSummary()
    {
        var xml = @"<summary>
hello world
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello world"));
    }
    
    [Test]
    public void ParseSummaryNewLineTossedOut()
    {
        var xml = @"<summary>
hello 
world
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello world"));
    }

    
    [Test]
    public void ParseSummary_WithPara()
    {
        var xml = @"<summary>
<para>a</para>
<para>b
 2
</para>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("\n\na\n\nb 2"));
    }
    
    
    [Test]
    public void ParseSummary_WithBold()
    {
        var xml = @"<summary>
hello <b>world</b>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello **world**"));
    }
    
    
    [Test]
    public void ParseSummary_WithBoldSpaces()
    {
        var xml = @"<summary>
hello <b> world </b>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello **world**"));
    }

    
    [Test]
    public void ParseSummary_WithItalic()
    {
        var xml = @"<summary>
hello <i>world</i>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello _world_"));
    }
    
    [Test]
    public void ParseSummary_WithC()
    {
        var xml = @"<summary>
hello <c>world</c>
</summary>
";
        // <code> is a single line in html
        // https://www.w3schools.com/tags/tag_code.asp
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello `world`"));
    }
    
    [Test]
    public void ParseSummary_WithCode()
    {
        var xml = @"<summary>
hello <code>
world
multi
</code>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello ```\nworld\nmulti\n```"));
    }
    
    [Test]
    public void ParseSummary_WithList()
    {
        var xml = @"<summary>
hello 
<list>
    <item> toast </item>
    <item> bread </item>
</list>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello \n-  toast \n-  bread \n\n"));
    }
    
    [Test]
    public void ParseSummary_WithList_Bullet()
    {
        var xml = @"<summary>
hello 
<list type=""bullet"">
    <item> toast </item>
    <item> bread </item>
</list>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello \n-  toast \n-  bread \n\n"));
    }
    
    [Test]
    public void ParseSummary_WithList_Number()
    {
        var xml = @"<summary>
hello 
<list type=""number"">
    <item> toast </item>
    <item> bread </item>
</list>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello \n1.  toast \n2.  bread \n\n"));
    }
    
    
    [Test]
    public void ParseSummary_WithList_Number_PlusMore()
    {
        var xml = @"<summary>
hello 
<list type=""number"">
    <item> toast <b> bold </b> </item>
    <item> bread </item>
</list>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.summary, Is.EqualTo("hello \n1.  toast **bold**\n2.  bread \n\n"));
    }
    
    
    [Test]
    public void ParseParams_Simple()
    {
        var xml = @"<summary>eh</summary>
<param name=""toast""> description </param>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.parameters.Count, Is.EqualTo(1));
        Assert.That(data.parameters[0].name, Is.EqualTo("toast"));
        Assert.That(data.parameters[0].body, Is.EqualTo(" description "));
    }

    [Test]
    public void ParseParams_Simple2()
    {
        var xml = @"<summary>eh</summary>
<param name=""toast""> description </param>
<param name=""frank""> description <i>cool</i> </param>
";
        var data = ProjectDocMethods.ParseMethodDocsMarkdown(xml);
        Assert.That(data.parameters.Count, Is.EqualTo(2));
        Assert.That(data.parameters[0].name, Is.EqualTo("toast"));
        Assert.That(data.parameters[0].body, Is.EqualTo(" description "));
        
        Assert.That(data.parameters[1].name, Is.EqualTo("frank"));
        Assert.That(data.parameters[1].body, Is.EqualTo(" description _cool_"));
    }
}