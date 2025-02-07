using FadeBasic;
using FadeBasic.Json;
using FadeBasic.Launch;
using FadeBasic.Virtual;
using DebugVariable = FadeBasic.Launch.DebugVariable;

namespace Tests;

public class JsonTests
{
    class RecurseList : IJsonable
    {
        public int n;
        public List<RecurseList> l = new List<RecurseList>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(n), ref n);
            op.IncludeField(nameof(l), ref l);
        }
    }

    class ByteArray : IJsonable
    {
        public byte[] numbers;
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(numbers), ref numbers);
        }
    }

    class StringInt : IJsonable
    {
        public string reason;
        public int status;

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(reason), ref reason);
            op.IncludeField(nameof(status), ref status);
        }
    }

    class Dud : IJsonable
    {
        public int x;

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField("x", ref x);
        }
    }
    
    class DictTest : IJsonable
    {
        public Dictionary<int, Dud> duds = new Dictionary<int, Dud>();
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField("duds", ref duds);
        }
    }

    class DictStringInt : IJsonable
    {
        public Dictionary<string, int> duds = new Dictionary<string, int>();

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(duds), ref duds);
        }
    }
    
    class DoubleDict : IJsonable
    {
        public Dictionary<string, Dud> beeps = new Dictionary<string, Dud>();
        public Dictionary<string, Dud> boops = new Dictionary<string, Dud>();

        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(beeps), ref beeps);
            op.IncludeField(nameof(boops), ref boops);
        }
    }

    [Test]
    public void DebugScopeTest()
    {
        var msg = new ScopesMessage
        {
            id = 3,
            type = DebugMessageType.PROTO_ACK,
            scopes = new List<DebugScope>
            {
                new DebugScope
                {
                    variables = new List<DebugVariable>
                    {
                        new DebugVariable
                        {
                            name = "toast",
                            type = "int",
                            value = "32"
                        }
                    }
                }
            }
        };

        var json = msg.Jsonify();

        var parsed = JsonableExtensions.FromJson<ScopesMessage>(json);
        Assert.That(parsed.scopes[0].variables[0].type, Is.EqualTo("int"));
    }

    [Test]
    public void StringIntTest()
    {
        var x = new StringInt
        {
            reason = "derp derp tuna",
            status = -1
        };
        var j = x.Jsonify();
        var y = JsonableExtensions.FromJson<StringInt>(j);
        Assert.That(y.status, Is.EqualTo(x.status));
        Assert.That(y.reason, Is.EqualTo(x.reason));
    }
    
    
    [Test]
    public void DoubleDictTest_Empty()
    {
        var x = new DoubleDict()
        {
            beeps = new Dictionary<string, Dud>(),
            boops = new Dictionary<string, Dud>()
        };
        var j = x.Jsonify();
        var y = JsonableExtensions.FromJson<DoubleDict>(j);
        
        // there should be at least a comma in there...
        Assert.That(j.Contains(","), $"the json needs at least one field separator.\n{j}");
    }

    [Test]
    public void DictStringIntTest()
    {
        var x = new DictStringInt
        {
            duds = new Dictionary<string, int>
            {
                ["a"] = 1,
                ["b"] = 2
            }
        };
        var json = x.Jsonify();
        var y = JsonableExtensions.FromJson<DictStringInt>(json);
        Assert.That(x.duds["a"], Is.EqualTo(y.duds["a"]));
        Assert.That(x.duds["b"], Is.EqualTo(y.duds["b"]));
    }
    
    [Test]
    public void Dict()
    {
        var x = new DictTest
        {
            duds = new Dictionary<int, Dud>
            {
                [1] = new Dud { x = 2 },
                [2] = new Dud { x = 4 },
                [3] = new Dud { x = 6 },
            }
        };
        var j = x.Jsonify();
        var y = JsonableExtensions.FromJson<DictTest>(j);

        Assert.That(x.duds[1].x, Is.EqualTo(y.duds[1].x));
        Assert.That(x.duds[2].x, Is.EqualTo(y.duds[2].x));
        Assert.That(x.duds[3].x, Is.EqualTo(y.duds[3].x));
    }

    [Test]
    public void ByteArray_Test()
    {
        var x = new ByteArray
        {
            numbers = new byte[]
            {
                1, 2, 3
            }
        };
        var j = x.Jsonify();
        var y = JsonableExtensions.FromJson<ByteArray>(j);
        
        Assert.That(y.numbers.Length, Is.EqualTo(3));
    }
    
    [Test]
    public void List()
    {
        var x = new RecurseList
        {
            n = 1,
            l = new List<RecurseList>
            {
                new RecurseList
                {
                    n = 2,
                    l = new List<RecurseList>
                    {
                        new RecurseList
                        {
                            n = 3,
                        }
                    },
                },
                new RecurseList
                {
                    n = 4
                }
            }
        };

        var j = x.Jsonify();
        var y = JsonableExtensions.FromJson<RecurseList>(j);
        
        Assert.That(x.l[0].n, Is.EqualTo(y.l[0].n));
        Assert.That(x.l[1].n, Is.EqualTo(y.l[1].n));
    }
    
    [Test]
    public void Simple()
    {
        var x = new DebugToken
        {
            insIndex = 3
        };
        var json = x.Jsonify();
        
        // Assert.That(json, Is.EqualTo($"{{\"{nameof(x.insIndex)}\":3,\"{nameof(x.token)}\":null}}"));

        var data = JsonData.Parse(json);
        Assert.That(data.ints[nameof(x.insIndex)], Is.EqualTo(3));
        Assert.That(data.objects[nameof(x.token)], Is.Null);
        
        // var y = JsonableExtensions.FromJson<DebugToken>(json);

        // Assert.That(x.insIndex, Is.EqualTo(y.insIndex));
    }

    [TestCase("an \"extra\" quote")]
    [TestCase("an \\ slash")]
    [TestCase("an end \\\\")]
    [TestCase("an end \\")]
    public void RandomStringSituations(string str)
    {
        var obj = new DebugEvalResult
        {
            value = str
        };
        var json = obj.Jsonify();

        var obj2= JsonableExtensions.FromJson<DebugEvalResult>(json);
        Assert.That(obj.value, Is.EqualTo(obj2.value));
    }
    

    [Test]
    public void NestedNull()
    {
        var x = new DebugToken()
        {
            insIndex = 5,
            token = null
        };
        var json = x.Jsonify();
        var y = JsonableExtensions.FromJson<DebugToken>(json);
        Assert.That(x.insIndex, Is.EqualTo(y.insIndex));
        Assert.That(x.token, Is.EqualTo(y.token));
    }

    [Test]
    public void Interned()
    {
        var interned = new InternedData
        {
            types = new Dictionary<string, InternedType>(),
            functions = new Dictionary<string, InternedFunction>(),
            strings = new List<InternedString>
            {
                new InternedString
                {
                    value = "\\",
                    indexReferences = new int[] { 3, 53 }
                }
            }
        };
        
        var json = interned.Jsonify();
        var obj = JsonableExtensions.FromJson<InternedData>(json);
        Assert.That(interned.strings[0].value, Is.EqualTo(obj.strings[0].value));
    }
    
    [Test]
    public void Nested()
    {
        var x = new DebugToken
        {
            insIndex = 3,
            token = new Token
            {
                lineNumber = 12,
                raw = "tuna",
                flags = TokenFlags.FunctionCall
            }
        };
        var json = x.Jsonify();
        
//         Assert.That(json, Is.EqualTo(@$"{{""{nameof(x.insIndex)}"":3,""{nameof(x.token)}"":{{
// ""{nameof(Token.lineNumber)}"":12,""{nameof(Token.charNumber)}"":0,""{nameof(Token.raw)}"":""tuna"",""{nameof(Token.caseInsensitiveRaw)}"":null}}
// }}".ReplaceLineEndings("")));

        var data = JsonData.Parse(json);
        Assert.That(data.ints[nameof(x.insIndex)], Is.EqualTo(3));
        Assert.That(data.objects[nameof(x.token)], Is.Not.Null);
        Assert.That(data.objects[nameof(x.token)].strings[nameof(Token.raw)], Is.EqualTo("tuna"));
        Assert.That(data.objects[nameof(x.token)].ints[nameof(Token.lineNumber)], Is.EqualTo(12));
        Assert.That(data.objects[nameof(x.token)].ints[nameof(Token.charNumber)], Is.EqualTo(0));
        
        var y = JsonableExtensions.FromJson<DebugToken>(json);

        Assert.That(x.insIndex, Is.EqualTo(y.insIndex));
        Assert.That(x.token.lineNumber, Is.EqualTo(y.token.lineNumber));
        Assert.That(x.token.raw, Is.EqualTo(y.token.raw));
    }
}