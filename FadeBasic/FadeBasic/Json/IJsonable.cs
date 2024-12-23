using System;
using System.Collections.Generic;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using FadeBasic.Launch;

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
        void IncludeField(string name, ref string fieldValue);
        void IncludeField(string name, ref DebugMessageType fieldValue);
        void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new();
        void IncludeField<T>(string name, ref List<T> fieldValue) where T : IJsonable, new();
        void IncludeField<T>(string name, ref Dictionary<int, T> fieldValue) where T : IJsonable, new();
    }

    public static class JsonableExtensions
    {
        public static T FromJson<T>(string json) where T : IJsonable, new()
        {
            var data = JsonData.Parse(json);
            return FromJson<T>(data);
        }

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
    }

    public class JsonData
    {
        public Dictionary<string, JsonData> objects = new Dictionary<string, JsonData>();
        public Dictionary<string, List<JsonData>> arrays = new Dictionary<string, List<JsonData>>();
        public Dictionary<string, int> ints = new Dictionary<string, int>();
        public Dictionary<string, string> strings = new Dictionary<string, string>();
        
        public static JsonData Parse(string json)
        {
            var span = json.AsSpan();
            var index = 0;
            
            // ReadAndAssert(ref span, JsonConstants.OPEN_BRACKET);
            // if (!TryRead(ref span, out var curr))
            // {
            //     throw new NotImplementedException("end of stream unhandled");
            // }

            var topObj = new JsonData();
            switch (span[0])
            {
                case JsonConstants.OPEN_BRACKET:
                    ReadObject(ref span, out topObj);
                    break;
                default:
                    throw new Exception("can only read top level objects");
            }

            void ReadObject(ref ReadOnlySpan<char> span, out JsonData obj)
            {
                obj = new JsonData();

                ReadAndAssert(ref span, JsonConstants.OPEN_BRACKET);

                // now need to read the value...
                while (span[index] != JsonConstants.CLOSE_BRACKET)
                {
                    // parse an object!
                    ReadString(ref span, out var field);
                    ReadAndAssert(ref span, JsonConstants.COLON);


                    Read(ref span, out var valuePeek);
                    // var valuePeek = span[index];

                    if (char.IsDigit(valuePeek) || valuePeek == '-')
                    {
                        index--;
                        ReadInteger(ref span, out var value);
                        obj.ints[field.ToString()] = value;
                    }
                    else if (valuePeek == 'n') // special null character
                    {
                        index += 3;
                        obj.objects[field.ToString()] = null;
                        obj.strings[field.ToString()] = null;
                    }
                    else if (valuePeek == JsonConstants.OPEN_BRACKET)
                    {
                        index--;
                        ReadObject(ref span, out var subObj);
                        obj.objects[field.ToString()] = subObj;
                    }
                    else if (valuePeek == JsonConstants.QUOTE)
                    {
                        index--;
                        ReadString(ref span, out var strValue);
                        obj.strings[field.ToString()] = strValue.ToString();
                    }
                    else if (valuePeek == JsonConstants.OPEN_ARRAY)
                    {
                        var list = new List<JsonData>();
                        while (span[index] != JsonConstants.CLOSE_ARRAY)
                        {
                            if (list.Count > 0)
                            {
                                ReadAndAssert(ref span, JsonConstants.COMMA);
                            }
                            ReadObject(ref span, out var element);
                            list.Add(element);
                        }

                        ReadAndAssert(ref span, JsonConstants.CLOSE_ARRAY);


                        obj.arrays[field.ToString()] = list;
                    }

                    // read comma if it exists...
                    var prePeakIndex = index;
                    if (TryRead(ref span, out valuePeek))
                    {
                        if (valuePeek != JsonConstants.COMMA)
                        {
                            index = prePeakIndex;

                        }
                    }
                    // Read(ref span, out valuePeek);
                    // if (valuePeek == JsonConstants.COMMA)
                    // {
                    //     // this is an allowed skip
                    // }
                    // else
                    // {
                    //     // revert the peak!
                    //     index = prePeakIndex;
                    // }
                }

                ReadAndAssert(ref span, JsonConstants.CLOSE_BRACKET);

            }

            void Read(ref ReadOnlySpan<char> span, out char next)
            {
                if (!TryRead(ref span, out next))
                {
                    throw new Exception("hit end of json stream");
                }
            }
            
            bool TryRead(ref ReadOnlySpan<char> span, out char next)
            {
                next = ' ';
                if (index >= span.Length) return false;
                while (char.IsWhiteSpace(next))
                {
                    next = span[index++];
                }
                
                return true;
            }

            void ReadInteger(ref ReadOnlySpan<char> span, out int value)
            {
                var found = false;
                var start = index;
                while (!found)
                {
                    Read(ref span, out var curr);
                    if (!char.IsDigit(curr) && curr != '-')
                    {
                        found = true;
                    }
                }

                index--;

                var intSpan = span.Slice(start, index - start );
                int.TryParse(intSpan.ToString(), out value);
            }
            
            void ReadString(ref ReadOnlySpan<char> span, out ReadOnlySpan<char> field)
            {
                ReadAndAssert(ref span, JsonConstants.QUOTE);
                var found = false;
                var start = index;
                while (!found)
                {
                    if (!TryRead(ref span, out var curr))
                    {
                        throw new NotImplementedException("end of stream unhandled - reading field");
                    }

                    if (curr == JsonConstants.QUOTE)
                    {
                        found = true;
                    }
                }

                field = span.Slice(start, index - start - 1);
            }
            
            void ReadAndAssert(ref ReadOnlySpan<char> span, char next)
            {
                if (!TryRead(ref span, out var curr))
                {
                    throw new Exception($"json error. Expected [{next}] but hit end of stream");
                } else if (curr != next)
                {
                    throw new Exception($"json error. Expected [{next}] but found [{curr}]");
                }
            }

            return topObj;
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

        public void IncludeField(string name, ref string fieldValue)
        {
            _data.strings.TryGetValue(name, out fieldValue);
        }

        public void IncludeField(string name, ref DebugMessageType fieldValue)
        {
            if (_data.ints.TryGetValue(name, out var fieldInt))
            {
                fieldValue = (DebugMessageType)fieldInt;
            }
        }


        public void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new()
        {
            if (_data.objects.TryGetValue(name, out var subData))
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
                _sb.Append(fieldValue);
                _sb.Append(JsonConstants.QUOTE);
            }
            
            fieldCount++;
        }

        public void IncludeField(string name, ref DebugMessageType fieldValue)
        {
            var val = (int)fieldValue;
            IncludePrim(name, ref val);
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

            
        }
    }
}