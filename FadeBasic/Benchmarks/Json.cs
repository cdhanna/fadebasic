using System.Text.Json;
using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Json;
using FadeBasic.Virtual;
using Newtonsoft.Json;

namespace Benchmarks;

[MemoryDiagnoser]
public class Json
{
    private DebugToken _token;
    private JsonSerializerOptions _options;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _options = new JsonSerializerOptions
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true
        };
        _token = new DebugToken
        {
            insIndex = 3,
            token = new Token
            {
                lineNumber = 12,
                charNumber = 3,
                raw = "tuna"
            }
        };
    }
    
    [Benchmark]
    public DebugToken SystemJson()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(_token, _options);
        return System.Text.Json.JsonSerializer.Deserialize<DebugToken>(json);
    }
    
    [Benchmark]
    public DebugToken Newton()
    {
        var json = JsonConvert.SerializeObject(_token);
        return JsonConvert.DeserializeObject<DebugToken>(json);
    }
    
    [Benchmark]
    public DebugToken Fade()
    {
        var json = _token.Jsonify();
        return JsonableExtensions.FromJson<DebugToken>(json);
    }
}