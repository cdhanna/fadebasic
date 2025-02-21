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

    [Test]
    public void ReadDspecFile()
    {
        // TODO: 
        // parse this, so that it can be used to find the dlls for the libraries
        // without needing to BUILD the project. 
        // JsonData.Parse(DSpecFile_Frag1);
        // JsonData.Parse(DSpecFile_Frag2);
        var dSpec = Jsonable2.Parse(DSpecFile);
        var assets = Jsonable2.Parse(ProjectAssetJson);
    }

    public const string ProjectAssetJson = @"{
  ""version"": 3,
  ""targets"": {
    ""net8.0"": {
      ""FadeBasic.Build/0.13.12.272"": {
        ""type"": ""package"",
        ""build"": {
          ""build/FadeBasic.Build.props"": {},
          ""build/FadeBasic.Build.targets"": {}
        }
      },
      ""FadeBasic.Lang.Core/0.13.12.272"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""System.Runtime.Loader"": ""4.3.0""
        },
        ""compile"": {
          ""lib/netstandard2.1/FadeBasic.Lang.Core.dll"": {}
        },
        ""runtime"": {
          ""lib/netstandard2.1/FadeBasic.Lang.Core.dll"": {}
        }
      },
      ""FadeBasic.Lib.Standard/0.13.12.272"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""FadeBasic.Lang.Core"": ""0.13.12.272""
        },
        ""compile"": {
          ""lib/netstandard2.0/FadeBasic.Lib.Standard.dll"": {
            ""related"": "".xml""
          }
        },
        ""runtime"": {
          ""lib/netstandard2.0/FadeBasic.Lib.Standard.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""Microsoft.NETCore.Platforms/1.1.0"": {
        ""type"": ""package"",
        ""compile"": {
          ""lib/netstandard1.0/_._"": {}
        },
        ""runtime"": {
          ""lib/netstandard1.0/_._"": {}
        }
      },
      ""Microsoft.NETCore.Targets/1.1.0"": {
        ""type"": ""package"",
        ""compile"": {
          ""lib/netstandard1.0/_._"": {}
        },
        ""runtime"": {
          ""lib/netstandard1.0/_._"": {}
        }
      },
      ""System.IO/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0"",
          ""System.Runtime"": ""4.3.0"",
          ""System.Text.Encoding"": ""4.3.0"",
          ""System.Threading.Tasks"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.5/System.IO.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""System.Reflection/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0"",
          ""System.IO"": ""4.3.0"",
          ""System.Reflection.Primitives"": ""4.3.0"",
          ""System.Runtime"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.5/System.Reflection.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""System.Reflection.Primitives/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0"",
          ""System.Runtime"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.0/System.Reflection.Primitives.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""System.Runtime/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0""
        },
        ""compile"": {
          ""ref/netstandard1.5/System.Runtime.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""System.Runtime.Loader/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""System.IO"": ""4.3.0"",
          ""System.Reflection"": ""4.3.0"",
          ""System.Runtime"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.5/System.Runtime.Loader.dll"": {
            ""related"": "".xml""
          }
        },
        ""runtime"": {
          ""lib/netstandard1.5/System.Runtime.Loader.dll"": {}
        }
      },
      ""System.Text.Encoding/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0"",
          ""System.Runtime"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.3/System.Text.Encoding.dll"": {
            ""related"": "".xml""
          }
        }
      },
      ""System.Threading.Tasks/4.3.0"": {
        ""type"": ""package"",
        ""dependencies"": {
          ""Microsoft.NETCore.Platforms"": ""1.1.0"",
          ""Microsoft.NETCore.Targets"": ""1.1.0"",
          ""System.Runtime"": ""4.3.0""
        },
        ""compile"": {
          ""ref/netstandard1.3/System.Threading.Tasks.dll"": {
            ""related"": "".xml""
          }
        }
      }
    }
  },
  ""libraries"": {
    ""FadeBasic.Build/0.13.12.272"": {
      ""sha512"": ""O47oAw1qnwYd/f64nqSdc41dbuyZqBgxrNsSDOhI8YaHfJeEqt0hlBlLqnTBIzccfO3XZG/EmdpMe8jj/MtLVg=="",
      ""type"": ""package"",
      ""path"": ""fadebasic.build/0.13.12.272"",
      ""files"": [
        "".nupkg.metadata"",
        ""build/FadeBasic.Build.props"",
        ""build/FadeBasic.Build.targets"",
        ""fadebasic.build.0.13.12.272.nupkg.sha512"",
        ""fadebasic.build.nuspec"",
        ""tasks/net8.0/FadeBasic.Build.deps.json"",
        ""tasks/net8.0/FadeBasic.Build.dll"",
        ""tasks/net8.0/FadeBasic.Lang.ApplicationSupport.dll"",
        ""tasks/net8.0/FadeBasic.Lang.Core.dll""
      ]
    },
    ""FadeBasic.Lang.Core/0.13.12.272"": {
      ""sha512"": ""S24IC6HgHyKSHltLHScBuRIoZB9WWwyAjVCJks45mTZNs/dE5UdciNXPeKw2Qskf2g4nneTvtxsnyxeiVEX6bQ=="",
      ""type"": ""package"",
      ""path"": ""fadebasic.lang.core/0.13.12.272"",
      ""files"": [
        "".nupkg.metadata"",
        ""fadebasic.lang.core.0.13.12.272.nupkg.sha512"",
        ""fadebasic.lang.core.nuspec"",
        ""lib/netstandard2.0/FadeBasic.Lang.Core.dll"",
        ""lib/netstandard2.1/FadeBasic.Lang.Core.dll""
      ]
    },
    ""FadeBasic.Lib.Standard/0.13.12.272"": {
      ""sha512"": ""SprDjesipqBL/qdjDqT9cXLc5R9mhK0QV385EksPB0aLgTX7S/UL6Uq9Lab7ZW7XjV3E5ZPATnza+lpvTz4blg=="",
      ""type"": ""package"",
      ""path"": ""fadebasic.lib.standard/0.13.12.272"",
      ""files"": [
        "".nupkg.metadata"",
        ""fadebasic.lib.standard.0.13.12.272.nupkg.sha512"",
        ""fadebasic.lib.standard.nuspec"",
        ""lib/netstandard2.0/FadeBasic.Lib.Standard.dll"",
        ""lib/netstandard2.0/FadeBasic.Lib.Standard.xml""
      ]
    },
    ""Microsoft.NETCore.Platforms/1.1.0"": {
      ""sha512"": ""kz0PEW2lhqygehI/d6XsPCQzD7ff7gUJaVGPVETX611eadGsA3A877GdSlU0LRVMCTH/+P3o2iDTak+S08V2+A=="",
      ""type"": ""package"",
      ""path"": ""microsoft.netcore.platforms/1.1.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/netstandard1.0/_._"",
        ""microsoft.netcore.platforms.1.1.0.nupkg.sha512"",
        ""microsoft.netcore.platforms.nuspec"",
        ""runtime.json""
      ]
    },
    ""Microsoft.NETCore.Targets/1.1.0"": {
      ""sha512"": ""aOZA3BWfz9RXjpzt0sRJJMjAscAUm3Hoa4UWAfceV9UTYxgwZ1lZt5nO2myFf+/jetYQo4uTP7zS8sJY67BBxg=="",
      ""type"": ""package"",
      ""path"": ""microsoft.netcore.targets/1.1.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/netstandard1.0/_._"",
        ""microsoft.netcore.targets.1.1.0.nupkg.sha512"",
        ""microsoft.netcore.targets.nuspec"",
        ""runtime.json""
      ]
    },
    ""System.IO/4.3.0"": {
      ""sha512"": ""3qjaHvxQPDpSOYICjUoTsmoq5u6QJAFRUITgeT/4gqkF1bajbSmb1kwSxEA8AHlofqgcKJcM8udgieRNhaJ5Cg=="",
      ""type"": ""package"",
      ""path"": ""system.io/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/net462/System.IO.dll"",
        ""lib/portable-net45+win8+wp8+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/net462/System.IO.dll"",
        ""ref/netcore50/System.IO.dll"",
        ""ref/netcore50/System.IO.xml"",
        ""ref/netcore50/de/System.IO.xml"",
        ""ref/netcore50/es/System.IO.xml"",
        ""ref/netcore50/fr/System.IO.xml"",
        ""ref/netcore50/it/System.IO.xml"",
        ""ref/netcore50/ja/System.IO.xml"",
        ""ref/netcore50/ko/System.IO.xml"",
        ""ref/netcore50/ru/System.IO.xml"",
        ""ref/netcore50/zh-hans/System.IO.xml"",
        ""ref/netcore50/zh-hant/System.IO.xml"",
        ""ref/netstandard1.0/System.IO.dll"",
        ""ref/netstandard1.0/System.IO.xml"",
        ""ref/netstandard1.0/de/System.IO.xml"",
        ""ref/netstandard1.0/es/System.IO.xml"",
        ""ref/netstandard1.0/fr/System.IO.xml"",
        ""ref/netstandard1.0/it/System.IO.xml"",
        ""ref/netstandard1.0/ja/System.IO.xml"",
        ""ref/netstandard1.0/ko/System.IO.xml"",
        ""ref/netstandard1.0/ru/System.IO.xml"",
        ""ref/netstandard1.0/zh-hans/System.IO.xml"",
        ""ref/netstandard1.0/zh-hant/System.IO.xml"",
        ""ref/netstandard1.3/System.IO.dll"",
        ""ref/netstandard1.3/System.IO.xml"",
        ""ref/netstandard1.3/de/System.IO.xml"",
        ""ref/netstandard1.3/es/System.IO.xml"",
        ""ref/netstandard1.3/fr/System.IO.xml"",
        ""ref/netstandard1.3/it/System.IO.xml"",
        ""ref/netstandard1.3/ja/System.IO.xml"",
        ""ref/netstandard1.3/ko/System.IO.xml"",
        ""ref/netstandard1.3/ru/System.IO.xml"",
        ""ref/netstandard1.3/zh-hans/System.IO.xml"",
        ""ref/netstandard1.3/zh-hant/System.IO.xml"",
        ""ref/netstandard1.5/System.IO.dll"",
        ""ref/netstandard1.5/System.IO.xml"",
        ""ref/netstandard1.5/de/System.IO.xml"",
        ""ref/netstandard1.5/es/System.IO.xml"",
        ""ref/netstandard1.5/fr/System.IO.xml"",
        ""ref/netstandard1.5/it/System.IO.xml"",
        ""ref/netstandard1.5/ja/System.IO.xml"",
        ""ref/netstandard1.5/ko/System.IO.xml"",
        ""ref/netstandard1.5/ru/System.IO.xml"",
        ""ref/netstandard1.5/zh-hans/System.IO.xml"",
        ""ref/netstandard1.5/zh-hant/System.IO.xml"",
        ""ref/portable-net45+win8+wp8+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.io.4.3.0.nupkg.sha512"",
        ""system.io.nuspec""
      ]
    },
    ""System.Reflection/4.3.0"": {
      ""sha512"": ""KMiAFoW7MfJGa9nDFNcfu+FpEdiHpWgTcS2HdMpDvt9saK3y/G4GwprPyzqjFH9NTaGPQeWNHU+iDlDILj96aQ=="",
      ""type"": ""package"",
      ""path"": ""system.reflection/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/net462/System.Reflection.dll"",
        ""lib/portable-net45+win8+wp8+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/net462/System.Reflection.dll"",
        ""ref/netcore50/System.Reflection.dll"",
        ""ref/netcore50/System.Reflection.xml"",
        ""ref/netcore50/de/System.Reflection.xml"",
        ""ref/netcore50/es/System.Reflection.xml"",
        ""ref/netcore50/fr/System.Reflection.xml"",
        ""ref/netcore50/it/System.Reflection.xml"",
        ""ref/netcore50/ja/System.Reflection.xml"",
        ""ref/netcore50/ko/System.Reflection.xml"",
        ""ref/netcore50/ru/System.Reflection.xml"",
        ""ref/netcore50/zh-hans/System.Reflection.xml"",
        ""ref/netcore50/zh-hant/System.Reflection.xml"",
        ""ref/netstandard1.0/System.Reflection.dll"",
        ""ref/netstandard1.0/System.Reflection.xml"",
        ""ref/netstandard1.0/de/System.Reflection.xml"",
        ""ref/netstandard1.0/es/System.Reflection.xml"",
        ""ref/netstandard1.0/fr/System.Reflection.xml"",
        ""ref/netstandard1.0/it/System.Reflection.xml"",
        ""ref/netstandard1.0/ja/System.Reflection.xml"",
        ""ref/netstandard1.0/ko/System.Reflection.xml"",
        ""ref/netstandard1.0/ru/System.Reflection.xml"",
        ""ref/netstandard1.0/zh-hans/System.Reflection.xml"",
        ""ref/netstandard1.0/zh-hant/System.Reflection.xml"",
        ""ref/netstandard1.3/System.Reflection.dll"",
        ""ref/netstandard1.3/System.Reflection.xml"",
        ""ref/netstandard1.3/de/System.Reflection.xml"",
        ""ref/netstandard1.3/es/System.Reflection.xml"",
        ""ref/netstandard1.3/fr/System.Reflection.xml"",
        ""ref/netstandard1.3/it/System.Reflection.xml"",
        ""ref/netstandard1.3/ja/System.Reflection.xml"",
        ""ref/netstandard1.3/ko/System.Reflection.xml"",
        ""ref/netstandard1.3/ru/System.Reflection.xml"",
        ""ref/netstandard1.3/zh-hans/System.Reflection.xml"",
        ""ref/netstandard1.3/zh-hant/System.Reflection.xml"",
        ""ref/netstandard1.5/System.Reflection.dll"",
        ""ref/netstandard1.5/System.Reflection.xml"",
        ""ref/netstandard1.5/de/System.Reflection.xml"",
        ""ref/netstandard1.5/es/System.Reflection.xml"",
        ""ref/netstandard1.5/fr/System.Reflection.xml"",
        ""ref/netstandard1.5/it/System.Reflection.xml"",
        ""ref/netstandard1.5/ja/System.Reflection.xml"",
        ""ref/netstandard1.5/ko/System.Reflection.xml"",
        ""ref/netstandard1.5/ru/System.Reflection.xml"",
        ""ref/netstandard1.5/zh-hans/System.Reflection.xml"",
        ""ref/netstandard1.5/zh-hant/System.Reflection.xml"",
        ""ref/portable-net45+win8+wp8+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.reflection.4.3.0.nupkg.sha512"",
        ""system.reflection.nuspec""
      ]
    },
    ""System.Reflection.Primitives/4.3.0"": {
      ""sha512"": ""5RXItQz5As4xN2/YUDxdpsEkMhvw3e6aNveFXUn4Hl/udNTCNhnKp8lT9fnc3MhvGKh1baak5CovpuQUXHAlIA=="",
      ""type"": ""package"",
      ""path"": ""system.reflection.primitives/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/portable-net45+win8+wp8+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/netcore50/System.Reflection.Primitives.dll"",
        ""ref/netcore50/System.Reflection.Primitives.xml"",
        ""ref/netcore50/de/System.Reflection.Primitives.xml"",
        ""ref/netcore50/es/System.Reflection.Primitives.xml"",
        ""ref/netcore50/fr/System.Reflection.Primitives.xml"",
        ""ref/netcore50/it/System.Reflection.Primitives.xml"",
        ""ref/netcore50/ja/System.Reflection.Primitives.xml"",
        ""ref/netcore50/ko/System.Reflection.Primitives.xml"",
        ""ref/netcore50/ru/System.Reflection.Primitives.xml"",
        ""ref/netcore50/zh-hans/System.Reflection.Primitives.xml"",
        ""ref/netcore50/zh-hant/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/System.Reflection.Primitives.dll"",
        ""ref/netstandard1.0/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/de/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/es/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/fr/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/it/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/ja/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/ko/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/ru/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/zh-hans/System.Reflection.Primitives.xml"",
        ""ref/netstandard1.0/zh-hant/System.Reflection.Primitives.xml"",
        ""ref/portable-net45+win8+wp8+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.reflection.primitives.4.3.0.nupkg.sha512"",
        ""system.reflection.primitives.nuspec""
      ]
    },
    ""System.Runtime/4.3.0"": {
      ""sha512"": ""JufQi0vPQ0xGnAczR13AUFglDyVYt4Kqnz1AZaiKZ5+GICq0/1MH/mO/eAJHt/mHW1zjKBJd7kV26SrxddAhiw=="",
      ""type"": ""package"",
      ""path"": ""system.runtime/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/net462/System.Runtime.dll"",
        ""lib/portable-net45+win8+wp80+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/net462/System.Runtime.dll"",
        ""ref/netcore50/System.Runtime.dll"",
        ""ref/netcore50/System.Runtime.xml"",
        ""ref/netcore50/de/System.Runtime.xml"",
        ""ref/netcore50/es/System.Runtime.xml"",
        ""ref/netcore50/fr/System.Runtime.xml"",
        ""ref/netcore50/it/System.Runtime.xml"",
        ""ref/netcore50/ja/System.Runtime.xml"",
        ""ref/netcore50/ko/System.Runtime.xml"",
        ""ref/netcore50/ru/System.Runtime.xml"",
        ""ref/netcore50/zh-hans/System.Runtime.xml"",
        ""ref/netcore50/zh-hant/System.Runtime.xml"",
        ""ref/netstandard1.0/System.Runtime.dll"",
        ""ref/netstandard1.0/System.Runtime.xml"",
        ""ref/netstandard1.0/de/System.Runtime.xml"",
        ""ref/netstandard1.0/es/System.Runtime.xml"",
        ""ref/netstandard1.0/fr/System.Runtime.xml"",
        ""ref/netstandard1.0/it/System.Runtime.xml"",
        ""ref/netstandard1.0/ja/System.Runtime.xml"",
        ""ref/netstandard1.0/ko/System.Runtime.xml"",
        ""ref/netstandard1.0/ru/System.Runtime.xml"",
        ""ref/netstandard1.0/zh-hans/System.Runtime.xml"",
        ""ref/netstandard1.0/zh-hant/System.Runtime.xml"",
        ""ref/netstandard1.2/System.Runtime.dll"",
        ""ref/netstandard1.2/System.Runtime.xml"",
        ""ref/netstandard1.2/de/System.Runtime.xml"",
        ""ref/netstandard1.2/es/System.Runtime.xml"",
        ""ref/netstandard1.2/fr/System.Runtime.xml"",
        ""ref/netstandard1.2/it/System.Runtime.xml"",
        ""ref/netstandard1.2/ja/System.Runtime.xml"",
        ""ref/netstandard1.2/ko/System.Runtime.xml"",
        ""ref/netstandard1.2/ru/System.Runtime.xml"",
        ""ref/netstandard1.2/zh-hans/System.Runtime.xml"",
        ""ref/netstandard1.2/zh-hant/System.Runtime.xml"",
        ""ref/netstandard1.3/System.Runtime.dll"",
        ""ref/netstandard1.3/System.Runtime.xml"",
        ""ref/netstandard1.3/de/System.Runtime.xml"",
        ""ref/netstandard1.3/es/System.Runtime.xml"",
        ""ref/netstandard1.3/fr/System.Runtime.xml"",
        ""ref/netstandard1.3/it/System.Runtime.xml"",
        ""ref/netstandard1.3/ja/System.Runtime.xml"",
        ""ref/netstandard1.3/ko/System.Runtime.xml"",
        ""ref/netstandard1.3/ru/System.Runtime.xml"",
        ""ref/netstandard1.3/zh-hans/System.Runtime.xml"",
        ""ref/netstandard1.3/zh-hant/System.Runtime.xml"",
        ""ref/netstandard1.5/System.Runtime.dll"",
        ""ref/netstandard1.5/System.Runtime.xml"",
        ""ref/netstandard1.5/de/System.Runtime.xml"",
        ""ref/netstandard1.5/es/System.Runtime.xml"",
        ""ref/netstandard1.5/fr/System.Runtime.xml"",
        ""ref/netstandard1.5/it/System.Runtime.xml"",
        ""ref/netstandard1.5/ja/System.Runtime.xml"",
        ""ref/netstandard1.5/ko/System.Runtime.xml"",
        ""ref/netstandard1.5/ru/System.Runtime.xml"",
        ""ref/netstandard1.5/zh-hans/System.Runtime.xml"",
        ""ref/netstandard1.5/zh-hant/System.Runtime.xml"",
        ""ref/portable-net45+win8+wp80+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.runtime.4.3.0.nupkg.sha512"",
        ""system.runtime.nuspec""
      ]
    },
    ""System.Runtime.Loader/4.3.0"": {
      ""sha512"": ""DHMaRn8D8YCK2GG2pw+UzNxn/OHVfaWx7OTLBD/hPegHZZgcZh3H6seWegrC4BYwsfuGrywIuT+MQs+rPqRLTQ=="",
      ""type"": ""package"",
      ""path"": ""system.runtime.loader/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net462/_._"",
        ""lib/netstandard1.5/System.Runtime.Loader.dll"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/netstandard1.5/System.Runtime.Loader.dll"",
        ""ref/netstandard1.5/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/de/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/es/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/fr/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/it/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/ja/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/ko/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/ru/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/zh-hans/System.Runtime.Loader.xml"",
        ""ref/netstandard1.5/zh-hant/System.Runtime.Loader.xml"",
        ""system.runtime.loader.4.3.0.nupkg.sha512"",
        ""system.runtime.loader.nuspec""
      ]
    },
    ""System.Text.Encoding/4.3.0"": {
      ""sha512"": ""BiIg+KWaSDOITze6jGQynxg64naAPtqGHBwDrLaCtixsa5bKiR8dpPOHA7ge3C0JJQizJE+sfkz1wV+BAKAYZw=="",
      ""type"": ""package"",
      ""path"": ""system.text.encoding/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/portable-net45+win8+wp8+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/netcore50/System.Text.Encoding.dll"",
        ""ref/netcore50/System.Text.Encoding.xml"",
        ""ref/netcore50/de/System.Text.Encoding.xml"",
        ""ref/netcore50/es/System.Text.Encoding.xml"",
        ""ref/netcore50/fr/System.Text.Encoding.xml"",
        ""ref/netcore50/it/System.Text.Encoding.xml"",
        ""ref/netcore50/ja/System.Text.Encoding.xml"",
        ""ref/netcore50/ko/System.Text.Encoding.xml"",
        ""ref/netcore50/ru/System.Text.Encoding.xml"",
        ""ref/netcore50/zh-hans/System.Text.Encoding.xml"",
        ""ref/netcore50/zh-hant/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/System.Text.Encoding.dll"",
        ""ref/netstandard1.0/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/de/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/es/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/fr/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/it/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/ja/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/ko/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/ru/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/zh-hans/System.Text.Encoding.xml"",
        ""ref/netstandard1.0/zh-hant/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/System.Text.Encoding.dll"",
        ""ref/netstandard1.3/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/de/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/es/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/fr/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/it/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/ja/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/ko/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/ru/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/zh-hans/System.Text.Encoding.xml"",
        ""ref/netstandard1.3/zh-hant/System.Text.Encoding.xml"",
        ""ref/portable-net45+win8+wp8+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.text.encoding.4.3.0.nupkg.sha512"",
        ""system.text.encoding.nuspec""
      ]
    },
    ""System.Threading.Tasks/4.3.0"": {
      ""sha512"": ""LbSxKEdOUhVe8BezB/9uOGGppt+nZf6e1VFyw6v3DN6lqitm0OSn2uXMOdtP0M3W4iMcqcivm2J6UgqiwwnXiA=="",
      ""type"": ""package"",
      ""path"": ""system.threading.tasks/4.3.0"",
      ""files"": [
        "".nupkg.metadata"",
        "".signature.p7s"",
        ""ThirdPartyNotices.txt"",
        ""dotnet_library_license.txt"",
        ""lib/MonoAndroid10/_._"",
        ""lib/MonoTouch10/_._"",
        ""lib/net45/_._"",
        ""lib/portable-net45+win8+wp8+wpa81/_._"",
        ""lib/win8/_._"",
        ""lib/wp80/_._"",
        ""lib/wpa81/_._"",
        ""lib/xamarinios10/_._"",
        ""lib/xamarinmac20/_._"",
        ""lib/xamarintvos10/_._"",
        ""lib/xamarinwatchos10/_._"",
        ""ref/MonoAndroid10/_._"",
        ""ref/MonoTouch10/_._"",
        ""ref/net45/_._"",
        ""ref/netcore50/System.Threading.Tasks.dll"",
        ""ref/netcore50/System.Threading.Tasks.xml"",
        ""ref/netcore50/de/System.Threading.Tasks.xml"",
        ""ref/netcore50/es/System.Threading.Tasks.xml"",
        ""ref/netcore50/fr/System.Threading.Tasks.xml"",
        ""ref/netcore50/it/System.Threading.Tasks.xml"",
        ""ref/netcore50/ja/System.Threading.Tasks.xml"",
        ""ref/netcore50/ko/System.Threading.Tasks.xml"",
        ""ref/netcore50/ru/System.Threading.Tasks.xml"",
        ""ref/netcore50/zh-hans/System.Threading.Tasks.xml"",
        ""ref/netcore50/zh-hant/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/System.Threading.Tasks.dll"",
        ""ref/netstandard1.0/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/de/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/es/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/fr/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/it/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/ja/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/ko/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/ru/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/zh-hans/System.Threading.Tasks.xml"",
        ""ref/netstandard1.0/zh-hant/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/System.Threading.Tasks.dll"",
        ""ref/netstandard1.3/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/de/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/es/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/fr/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/it/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/ja/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/ko/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/ru/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/zh-hans/System.Threading.Tasks.xml"",
        ""ref/netstandard1.3/zh-hant/System.Threading.Tasks.xml"",
        ""ref/portable-net45+win8+wp8+wpa81/_._"",
        ""ref/win8/_._"",
        ""ref/wp80/_._"",
        ""ref/wpa81/_._"",
        ""ref/xamarinios10/_._"",
        ""ref/xamarinmac20/_._"",
        ""ref/xamarintvos10/_._"",
        ""ref/xamarinwatchos10/_._"",
        ""system.threading.tasks.4.3.0.nupkg.sha512"",
        ""system.threading.tasks.nuspec""
      ]
    }
  },
  ""projectFileDependencyGroups"": {
    ""net8.0"": [
      ""FadeBasic.Build >= 0.13.12.272"",
      ""FadeBasic.Lang.Core >= 0.13.12.272"",
      ""FadeBasic.Lib.Standard >= 0.13.12.272""
    ]
  },
  ""packageFolders"": {
    ""/Users/chrishanna/.nuget/packages/"": {}
  },
  ""project"": {
    ""version"": ""1.0.0"",
    ""restore"": {
      ""projectUniqueName"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"",
      ""projectName"": ""example"",
      ""projectPath"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"",
      ""packagesPath"": ""/Users/chrishanna/.nuget/packages/"",
      ""outputPath"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/obj/"",
      ""projectStyle"": ""PackageReference"",
      ""configFilePaths"": [
        ""/Users/chrishanna/.nuget/NuGet/NuGet.Config""
      ],
      ""originalTargetFrameworks"": [
        ""net8.0""
      ],
      ""sources"": {
        ""/Users/chrishanna/Documents/Github/BeamableProduct/BeamableNugetSource"": {},
        ""/Users/chrishanna/Documents/Github/dby/FadeBasic/obj/localNugetSource"": {},
        ""/usr/local/share/dotnet/library-packs"": {},
        ""https://api.nuget.org/v3/index.json"": {}
      },
      ""frameworks"": {
        ""net8.0"": {
          ""targetAlias"": ""net8.0"",
          ""projectReferences"": {}
        }
      },
      ""warningProperties"": {
        ""warnAsError"": [
          ""NU1605""
        ]
      },
      ""restoreAuditProperties"": {
        ""enableAudit"": ""true"",
        ""auditLevel"": ""low"",
        ""auditMode"": ""direct""
      },
      ""SdkAnalysisLevel"": ""9.0.100""
    },
    ""frameworks"": {
      ""net8.0"": {
        ""targetAlias"": ""net8.0"",
        ""dependencies"": {
          ""FadeBasic.Build"": {
            ""target"": ""Package"",
            ""version"": ""[0.13.12.272, )""
          },
          ""FadeBasic.Lang.Core"": {
            ""target"": ""Package"",
            ""version"": ""[0.13.12.272, )""
          },
          ""FadeBasic.Lib.Standard"": {
            ""target"": ""Package"",
            ""version"": ""[0.13.12.272, )""
          }
        },
        ""imports"": [
          ""net461"",
          ""net462"",
          ""net47"",
          ""net471"",
          ""net472"",
          ""net48"",
          ""net481""
        ],
        ""assetTargetFallback"": true,
        ""warn"": true,
        ""downloadDependencies"": [
          {
            ""name"": ""Microsoft.AspNetCore.App.Ref"",
            ""version"": ""[8.0.12, 8.0.12]""
          },
          {
            ""name"": ""Microsoft.NETCore.App.Host.osx-arm64"",
            ""version"": ""[8.0.12, 8.0.12]""
          },
          {
            ""name"": ""Microsoft.NETCore.App.Ref"",
            ""version"": ""[8.0.12, 8.0.12]""
          }
        ],
        ""frameworkReferences"": {
          ""Microsoft.NETCore.App"": {
            ""privateAssets"": ""all""
          }
        },
        ""runtimeIdentifierGraphPath"": ""/usr/local/share/dotnet/sdk/9.0.102/PortableRuntimeIdentifierGraph.json""
      }
    }
  }
}";
    public const string DSpecFile = @"{
  ""format"": 1,
  ""restore"": {
    ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"": {} 
  },
  ""projects"": {
    ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"": {
      ""version"": ""1.0.0"",
      ""restore"": {
        ""projectUniqueName"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"",
        ""projectName"": ""example"",
        ""projectPath"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/example.csproj"",
        ""packagesPath"": ""/Users/chrishanna/.nuget/packages/"",
        ""outputPath"": ""/Users/chrishanna/Documents/Github/DarkBasicVsCode/sample/obj/"",
        ""projectStyle"": ""PackageReference"",
        ""configFilePaths"": [
          ""/Users/chrishanna/.nuget/NuGet/NuGet.Config""
        ],
        ""originalTargetFrameworks"": [
          ""net8.0""
        ],
        ""sources"": {
          ""/Users/chrishanna/Documents/Github/BeamableProduct/BeamableNugetSource"": {},
          ""/Users/chrishanna/Documents/Github/dby/FadeBasic/obj/localNugetSource"": {},
          ""/usr/local/share/dotnet/library-packs"": {},
          ""https://api.nuget.org/v3/index.json"": {}
        },
        ""frameworks"": {
          ""net8.0"": {
            ""targetAlias"": ""net8.0"",
            ""projectReferences"": {}
          }
        },
        ""warningProperties"": {
          ""warnAsError"": [
            ""NU1605""
          ]
        },
        ""restoreAuditProperties"": {
          ""enableAudit"": ""true"",
          ""auditLevel"": ""low"",
          ""auditMode"": ""direct""
        },
        ""SdkAnalysisLevel"": ""9.0.100""
      },
      ""frameworks"": {
        ""net8.0"": {
          ""targetAlias"": ""net8.0"",
          ""dependencies"": {
            ""FadeBasic.Build"": {
              ""target"": ""Package"",
              ""version"": ""[0.13.12.272, )""
            },
            ""FadeBasic.Lang.Core"": {
              ""target"": ""Package"",
              ""version"": ""[0.13.12.272, )""
            },
            ""FadeBasic.Lib.Standard"": {
              ""target"": ""Package"",
              ""version"": ""[0.13.12.272, )""
            }
          },
          ""imports"": [
            ""net461"",
            ""net462"",
            ""net47"",
            ""net471"",
            ""net472"",
            ""net48"",
            ""net481""
          ],
          ""assetTargetFallback"": true,
          ""warn"": true,
          ""downloadDependencies"": [
            {
              ""name"": ""Microsoft.AspNetCore.App.Ref"",
              ""version"": ""[8.0.12, 8.0.12]""
            },
            {
              ""name"": ""Microsoft.NETCore.App.Host.osx-arm64"",
              ""version"": ""[8.0.12, 8.0.12]""
            },
            {
              ""name"": ""Microsoft.NETCore.App.Ref"",
              ""version"": ""[8.0.12, 8.0.12]""
            }
          ],
          ""frameworkReferences"": {
            ""Microsoft.NETCore.App"": {
              ""privateAssets"": ""all""
            }
          },
          ""runtimeIdentifierGraphPath"": ""/usr/local/share/dotnet/sdk/9.0.102/PortableRuntimeIdentifierGraph.json""
        }
      }
    }
  }
}";
}