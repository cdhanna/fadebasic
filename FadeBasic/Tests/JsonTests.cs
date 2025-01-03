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