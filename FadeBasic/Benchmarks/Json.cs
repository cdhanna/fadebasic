using System.Text.Json;
using BenchmarkDotNet.Attributes;
using FadeBasic;
using FadeBasic.Json;
using FadeBasic.Virtual;
using Newtonsoft.Json;

namespace Benchmarks;

[MemoryDiagnoser]
public class JsonBenchmarks
{
    // ── tiny (original) ─────────────────────────────────────────────────────
    private DebugToken _singleToken;

    // ── realistic DebugData (50 statement tokens, 20 vars, 5 functions) ─────
    private DebugData _debugData;
    private string _debugDataJson;

    // ── realistic InternedData (5 types, 15 functions, 30 strings) ──────────
    private InternedData _internedData;
    private string _internedDataJson;

    private JsonSerializerOptions _sysJsonOptions;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _sysJsonOptions = new JsonSerializerOptions
        {
            IncludeFields = true,
            IgnoreReadOnlyProperties = true
        };

        _singleToken = new DebugToken
        {
            insIndex = 3,
            token = new Token { lineNumber = 12, charNumber = 3, raw = "tuna" }
        };

        _debugData = BuildDebugData();
        _debugDataJson = _debugData.Jsonify();

        _internedData = BuildInternedData();
        _internedDataJson = _internedData.Jsonify();
    }

    // ── single token (baseline, matches old benchmark) ──────────────────────

    [Benchmark(Baseline = true)]
    public string SingleToken_Serialize() => _singleToken.Jsonify();

    [Benchmark]
    public DebugToken SingleToken_Deserialize() => JsonableExtensions.FromJson<DebugToken>(_singleToken.Jsonify());

    // ── DebugData round-trip ─────────────────────────────────────────────────

    [Benchmark]
    public string DebugData_Serialize() => _debugData.Jsonify();

    [Benchmark]
    public DebugData DebugData_Deserialize() => JsonableExtensions.FromJson<DebugData>(_debugDataJson);

    [Benchmark]
    public DebugData DebugData_RoundTrip()
    {
        var json = _debugData.Jsonify();
        return JsonableExtensions.FromJson<DebugData>(json);
    }

    // ── InternedData round-trip ──────────────────────────────────────────────

    [Benchmark]
    public string InternedData_Serialize() => _internedData.Jsonify();

    [Benchmark]
    public InternedData InternedData_Deserialize() => JsonableExtensions.FromJson<InternedData>(_internedDataJson);

    [Benchmark]
    public InternedData InternedData_RoundTrip()
    {
        var json = _internedData.Jsonify();
        return JsonableExtensions.FromJson<InternedData>(json);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    static Token MakeToken(int line, int ch, string raw) =>
        new Token { lineNumber = line, charNumber = ch, raw = raw };

    static DebugData BuildDebugData()
    {
        var d = new DebugData();

        // 50 statement tokens
        for (int i = 0; i < 50; i++)
            d.statementTokens.Add(new DebugToken
            {
                insIndex = i * 4,
                token = MakeToken(i, i % 40, $"stmt{i}"),
                isComputed = i % 7 == 0 ? 1 : 0
            });

        // 20 variables
        for (int i = 0; i < 20; i++)
            d.insToVariable[i * 4] = new DebugVariable
            {
                insIndex = i * 4,
                name = $"variable_{i}",
                isPtr = i % 3 == 0 ? 1 : 0
            };

        // 5 functions
        for (int i = 0; i < 5; i++)
            d.insToFunction[i * 20] = new DebugToken
            {
                insIndex = i * 20,
                token = MakeToken(i * 10, 0, $"function_{i}"),
                isComputed = 0
            };

        return d;
    }

    static InternedData BuildInternedData()
    {
        var d = new InternedData
        {
            types = new Dictionary<string, InternedType>(),
            maxRegisterAddress = 0x1234567890ABCDEF,
        };

        // 5 types with 4 fields each
        for (int t = 0; t < 5; t++)
        {
            var type = new InternedType
            {
                name = $"Type{t}",
                byteSize = 16 + t * 8,
                typeId = t,
                fields = new Dictionary<string, InternedField>()
            };
            for (int f = 0; f < 4; f++)
            {
                type.fields[$"field{f}"] = new InternedField
                {
                    offset = f * 4,
                    length = 4,
                    typeCode = (byte)(f % 8),
                    typeName = $"type{f % 3}",
                    typeId = f % 5
                };
            }
            d.types[$"Type{t}"] = type;
        }

        // 15 functions with 2 parameters each
        for (int fn = 0; fn < 15; fn++)
        {
            var func = new InternedFunction
            {
                name = $"function_{fn}",
                insIndex = fn * 8,
                typeCode = fn % 4,
                typeId = fn % 5,
                parameters = new List<InternedFunctionParameter>
                {
                    new InternedFunctionParameter { name = "a", index = 0, typeCode = 1, typeId = 0 },
                    new InternedFunctionParameter { name = "b", index = 1, typeCode = 2, typeId = 1 },
                }
            };
            d.functions[$"function_{fn}"] = func;
        }

        // 30 strings
        for (int s = 0; s < 30; s++)
            d.strings.Add(new InternedString
            {
                value = $"string literal number {s} with some content",
                indexReferences = new[] { s * 2, s * 2 + 1 }
            });

        return d;
    }
}
