using FadeBasic.Json;

namespace Tests;

public class Jsonable2Tests
{
    [Test]
    public void Demo()
    {
        var json = "{\"value\":\" \\\\\"}";
        var data = Jsonable2.Parse(json);
    }
}