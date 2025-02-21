using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using FadeBasic.Launch;
using FadeBasic.Virtual;

namespace FadeBasic.Json
{
    /// <summary>
    /// this is heavily inspired by some json work from Beamable
    /// </summary>
    public interface IJsonable
    {
        void ProcessJson(IJsonOperation op);
    }

    public interface IJsonOperation
    {
        void Process(IJsonable jsonable);
        void IncludeField(string name, ref int fieldValue);
        void IncludeField(string name, ref byte fieldValue);
        void IncludeField(string name, ref bool fieldValue);
        void IncludeField(string name, ref string fieldValue);
        void IncludeField(string name, ref byte[] fieldValue);
        void IncludeField(string name, ref int[] fieldValue);
        void IncludeField(string name, ref DebugMessageType fieldValue);
        void IncludeField(string name, ref Dictionary<string, int> fieldValue);
        void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new();
        void IncludeField<T>(string name, ref List<T> fieldValue) where T : IJsonable, new();
        void IncludeField<T>(string name, ref Dictionary<int, T> fieldValue) where T : IJsonable, new();
        void IncludeField<T>(string name, ref Dictionary<string, T> fieldValue) where T : IJsonable, new();
    }

    public static class JsonableExtensions
    {
        public static T FromJson<T>(string json) where T : IJsonable, new()
        {
            // var data = JsonData.Parse(json);
            var data = Jsonable2.Parse(json);
            return FromJson<T>(data);
        }
        
        // public static T FromJson2<T>(string json) where T : IJsonable, new()
        // {
        //     // var data = JsonData.Parse(json);
        //     var data = Jsonable2.Parse(json);
        //     return FromJson<T>(data);
        // }

        public static T FromJson<T>(JsonData json) where T : IJsonable, new()
        {
            var instance = new T();
            var op = new JsonReadOp(json);
            instance.ProcessJson(op);
            return instance;
        }
        
        public static string Jsonify(this IJsonable jsonable)
        {
            var sb = new StringBuilder();
            var op = new JsonWriteOp(sb);

            op.Process(jsonable);
            
            return sb.ToString();
        }
    }

    static class JsonConstants
    {
        public const char OPEN_BRACKET = '{';
        public const char CLOSE_BRACKET = '}';
        
        public const char OPEN_ARRAY = '[';
        public const char CLOSE_ARRAY = ']';
        public const char COMMA = ',';
        public const char QUOTE = '\"';
        public const char COLON = ':';
        public const char ESCAPE = '\\';
    }

    public class JsonData
    {
        public Dictionary<string, JsonData> objects = new Dictionary<string, JsonData>();
        public Dictionary<string, List<JsonData>> arrays = new Dictionary<string, List<JsonData>>();
        public Dictionary<string, List<int>> numberArrays = new Dictionary<string, List<int>>();
        public Dictionary<string, int> ints = new Dictionary<string, int>();
        public Dictionary<string, string> strings = new Dictionary<string, string>();
        
        public static JsonData Parse(string json)
        {
            return Jsonable2.Parse(json);
        }
    }

    public class JsonReadOp : IJsonOperation
    {
        private readonly JsonData _data;

        public JsonReadOp(JsonData data)
        {
            _data = data;
        }

        public void Process(IJsonable jsonable)
        {
            jsonable.ProcessJson(this);
        }

        public void IncludeField(string name, ref int fieldValue)
        {
            _data.ints.TryGetValue(name, out fieldValue);
        }

        public void IncludeField(string name, ref byte fieldValue)
        {
            _data.ints.TryGetValue(name, out var byteValue);
            fieldValue = (byte)byteValue;
        }

        public void IncludeField(string name, ref bool fieldValue)
        {
            _data.ints.TryGetValue(name, out var byteValue);
            fieldValue = byteValue > 0;
        }

        public void IncludeField(string name, ref string fieldValue)
        {
            _data.strings.TryGetValue(name, out fieldValue);
        }

        public void IncludeField(string name, ref int[] fieldValue)
        {
            if (!_data.numberArrays.TryGetValue(name, out var numbers))
            {
                fieldValue = Array.Empty<int>();
            }
            else
            {
                fieldValue = new int[numbers.Count];
                for (var i = 0; i < numbers.Count; i++)
                {
                    fieldValue[i] = numbers[i];
                }
            }
        }
        
        public void IncludeField(string name, ref byte[] fieldValue)
        {
            if (!_data.numberArrays.TryGetValue(name, out var numbers))
            {
                fieldValue = Array.Empty<byte>();
            }
            else
            {
                fieldValue = new byte[numbers.Count];
                for (var i = 0; i < numbers.Count; i++)
                {
                    fieldValue[i] = (byte)numbers[i];
                }
            }
        }

        public void IncludeField(string name, ref DebugMessageType fieldValue)
        {
            if (_data.ints.TryGetValue(name, out var fieldInt))
            {
                fieldValue = (DebugMessageType)fieldInt;
            }
        }

        public void IncludeField(string name, ref Dictionary<string, int> fieldValue)
        {
            if (_data.objects.TryGetValue(name, out var dict))
            {
                fieldValue = new Dictionary<string, int>();
                foreach (var kvp in dict.ints)
                {
                    fieldValue[kvp.Key] = kvp.Value;
                }
            }
        }

        public void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new()
        {
            if (_data.objects.TryGetValue(name, out var subData) && subData != null)
            {
                var subOp = new JsonReadOp(subData);
                fieldValue = new T();
                subOp.Process(fieldValue);
            }
        }

        public void IncludeField<T>(string name, ref List<T> fieldValue) where T : IJsonable, new()
        {
            if (_data.arrays.TryGetValue(name, out var arr))
            {
                fieldValue = new List<T>(arr.Count);
                for (var i = 0; i < arr.Count; i++)
                {
                    var subOp = new JsonReadOp(arr[i]);
                    fieldValue.Add(new T());
                    subOp.Process(fieldValue[i]);
                }
            }
        }

        public void IncludeField<T>(string name, ref Dictionary<int, T> fieldValue) where T : IJsonable, new()
        {
            if (_data.objects.TryGetValue(name, out var dict))
            {
                fieldValue = new Dictionary<int, T>();
                foreach (var kvp in dict.objects)
                {
                    if (int.TryParse(kvp.Key, out var intKey))
                    {
                        var subOp = new JsonReadOp(kvp.Value);
                        fieldValue[intKey] = new T();
                        subOp.Process(fieldValue[intKey]);
                    }
                }
            }
        }

        public void IncludeField<T>(string name, ref Dictionary<string, T> fieldValue) where T : IJsonable, new()
        {
            if (_data.objects.TryGetValue(name, out var dict))
            {
                fieldValue = new Dictionary<string, T>();
                foreach (var kvp in dict.objects)
                {
                    var subOp = new JsonReadOp(kvp.Value);
                    fieldValue[kvp.Key] = new T();
                    subOp.Process(fieldValue[kvp.Key]);
                }
            }
        }
    }

    public class JsonWriteOp : IJsonOperation
    {
        private readonly StringBuilder _sb;
        private int fieldCount = 0;

        public JsonWriteOp(StringBuilder sb)
        {
            _sb = sb;
        }

        void IncludePrim<T>(string name, ref T prim) where T : struct
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(prim);
            
            fieldCount++;
        }

        public void Process(IJsonable jsonable)
        {
            _sb.Append(JsonConstants.OPEN_BRACKET);
            jsonable.ProcessJson(this);
            _sb.Append(JsonConstants.CLOSE_BRACKET);
        }

        public void IncludeField(string name, ref int fieldValue) => IncludePrim(name, ref fieldValue);
        public void IncludeField(string name, ref byte fieldValue) => IncludePrim(name, ref fieldValue);

        public void IncludeField(string name, ref bool fieldValue)
        {
            var value = fieldValue ? 1 : 0;
            IncludePrim(name, ref value);
        }

        public void IncludeField(string name, ref string fieldValue)
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            
            _sb.Append(JsonConstants.QUOTE);
            
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);

            if (fieldValue == null)
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append(JsonConstants.QUOTE);
                
                // need to escape the string content...
                for (var i = 0; i < fieldValue.Length; i++)
                {
                    var c = fieldValue[i];
                    switch (c)
                    {
                        case '\"':
                            _sb.Append("\\\"");
                            break;
                        case '\\':
                            // if (i + 1 < fieldValue.Length)
                            // {
                            //     // if (fieldValue)
                            // }
                            _sb.Append("\\\\");
                            break;
                        default:
                            _sb.Append(c);
                            break;
                    }
                }
                _sb.Append(JsonConstants.QUOTE);
            }
            
            fieldCount++;
        }

        
        public void IncludeField(string name, ref int[] fieldValue)
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(JsonConstants.OPEN_ARRAY);
            for (var i = 0; i < fieldValue.Length; i++)
            {
                if (i > 0)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
                _sb.Append(fieldValue[i]);

            }
            _sb.Append(JsonConstants.CLOSE_ARRAY);
            fieldCount++;
        }
        
        public void IncludeField(string name, ref byte[] fieldValue)
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(JsonConstants.OPEN_ARRAY);
            for (var i = 0; i < fieldValue.Length; i++)
            {
                if (i > 0)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
                _sb.Append(fieldValue[i]);

            }
            _sb.Append(JsonConstants.CLOSE_ARRAY);
            fieldCount++;
        }

        public void IncludeField(string name, ref DebugMessageType fieldValue)
        {
            var val = (int)fieldValue;
            IncludePrim(name, ref val);
        }

        public void IncludeField(string name, ref Dictionary<string, int> fieldValue)
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }

            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(JsonConstants.OPEN_BRACKET);
            var first = true;
            foreach (var kvp in fieldValue)
            {
                if (!first)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
                first = false;

                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(kvp.Key);
                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(JsonConstants.COLON);
                
                var val = kvp.Value;
                _sb.Append(val);

                // subOp.IncludeField(kvp.ToString(), ref val);
            }
            _sb.Append(JsonConstants.CLOSE_BRACKET);
            fieldCount++;

        }

        public void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new()
        {
            
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);

            if (fieldValue == null)
            {
                _sb.Append("null");
            }
            else
            {
                _sb.Append(JsonConstants.OPEN_BRACKET);
                var subOp = new JsonWriteOp(_sb);
                fieldValue.ProcessJson(subOp);
                _sb.Append(JsonConstants.CLOSE_BRACKET);

            }

            fieldCount++;
        }

        public void IncludeField<T>(string name, ref List<T> fieldValue) where T : IJsonable, new()
        {
           
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }
            
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);

            _sb.Append(JsonConstants.OPEN_ARRAY);
            
            for (var i = 0 ; i < fieldValue.Count; i ++)
            {
                var subOp = new JsonWriteOp(_sb);
                subOp.Process(fieldValue[i]);
                if (i != fieldValue.Count - 1)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
            }
            _sb.Append(JsonConstants.CLOSE_ARRAY);
            fieldCount++;

        }

        public void IncludeField<T>(string name, ref Dictionary<int, T> fieldValue) where T : IJsonable, new()
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }

            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(JsonConstants.OPEN_BRACKET);
            var first = true;
            foreach (var kvp in fieldValue)
            {
                if (!first)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
                first = false;

                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(kvp.Key);
                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(JsonConstants.COLON);
                
                var subOp = new JsonWriteOp(_sb);
                var val = kvp.Value;
                subOp.Process(val);
                // subOp.IncludeField(kvp.ToString(), ref val);
            }
            _sb.Append(JsonConstants.CLOSE_BRACKET);
            fieldCount++;

            
        }

        public void IncludeField<T>(string name, ref Dictionary<string, T> fieldValue) where T : IJsonable, new()
        {
            if (fieldCount > 0)
            {
                _sb.Append(JsonConstants.COMMA);
            }

            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(JsonConstants.OPEN_BRACKET);
            var first = true;
            foreach (var kvp in fieldValue)
            {
                if (!first)
                {
                    _sb.Append(JsonConstants.COMMA);
                }
                first = false;

                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(kvp.Key);
                _sb.Append(JsonConstants.QUOTE);
                _sb.Append(JsonConstants.COLON);
                
                var subOp = new JsonWriteOp(_sb);
                var val = kvp.Value;
                subOp.Process(val);
                // subOp.IncludeField(kvp.ToString(), ref val);
            }
            _sb.Append(JsonConstants.CLOSE_BRACKET);
            fieldCount++;

        }
    }
}