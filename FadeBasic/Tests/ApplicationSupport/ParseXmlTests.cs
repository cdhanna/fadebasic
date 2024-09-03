using FadeBasic.ApplicationSupport.Project;

namespace Tests.ApplicationSupport;

public class ParseXmlTests
{
    [Test]
    public void ParseSummary()
    {
        var xml = @"<summary>
hello world
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocs(xml);
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("<p>a</p><p>b 2</p>"));
    }
    
    
    [Test]
    public void ParseSummary_WithBold()
    {
        var xml = @"<summary>
hello <b>world</b>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <b>world</b>"));
    }
    [Test]
    public void ParseSummary_WithItalic()
    {
        var xml = @"<summary>
hello <i>world</i>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <i>world</i>"));
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <code>world</code>"));
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <pre>world\nmulti</pre>"));
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <ul><li> toast </li><li> bread </li></ul>"));
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <ul><li> toast </li><li> bread </li></ul>"));
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
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <ol><li> toast </li><li> bread </li></ol>"));
    }
    
    
    [Test]
    public void ParseSummary_WithList_Number_PlusMore()
    {
        var xml = @"<summary>
hello 
<list type=""number"">
    <item> toast <para> second <b> bold </b> </para> </item>
    <item> bread </item>
</list>
</summary>
";
        var data = ProjectDocMethods.ParseMethodDocs(xml);
        Assert.That(data.summary, Is.EqualTo("hello <ol><li> toast <p> second <b> bold </b></p></li><li> bread </li></ol>"));
    }
}