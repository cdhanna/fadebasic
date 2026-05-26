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

    public interface IJsonableSerializationCallbacks : IJsonable
    {
        void OnAfterDeserialized();
        void OnBeforeSerialize();
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
        void IncludeField(string name, ref double fieldValue);
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
            int start = 0;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t' || json[start] == '\r' || json[start] == '\n'))
                start++;
            var instance = new T();
            var op = new StreamingJsonReadOp(json, start);
            op.Process(instance);
            return instance;
        }

        public static T FromJson<T>(JsonData json) where T : IJsonable, new()
        {
            var instance = new T();
            var op = new JsonReadOp(json);
            op.Process(instance);
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
                        var elementPeek = span[index];

                        if (elementPeek == JsonConstants.CLOSE_ARRAY)
                        {
                            obj.arrays[field.ToString()] = new List<JsonData>();
                            obj.numberArrays[field.ToString()] = new List<int>();
                        } else if (elementPeek == JsonConstants.OPEN_BRACKET)
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

                            obj.arrays[field.ToString()] = list;
                            obj.numberArrays[field.ToString()] = new List<int>();
                        } else if (char.IsNumber(elementPeek))
                        {
                            var numberList = new List<int>();

                            while (span[index] != JsonConstants.CLOSE_ARRAY)
                            {
                                if (numberList.Count > 0)
                                {
                                    ReadAndAssert(ref span, JsonConstants.COMMA);
                                }
                                ReadInteger(ref span, out var number);
                                numberList.Add(number);
                            }
                            obj.arrays[field.ToString()] = new List<JsonData>();
                            obj.numberArrays[field.ToString()] = numberList;
                        }
                        
                        // while (span[index] != JsonConstants.CLOSE_ARRAY)
                        // {
                        //     if (list.Count > 0)
                        //     {
                        //         ReadAndAssert(ref span, JsonConstants.COMMA);
                        //     }
                        //     ReadObject(ref span, out var element);
                        //     list.Add(element);
                        // }

                        ReadAndAssert(ref span, JsonConstants.CLOSE_ARRAY);


                        // obj.arrays[field.ToString()] = list;
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
                var requireEscapeRemoval = false;
                while (!found)
                {
                    if (!TryRead(ref span, out var curr))
                    {
                        throw new NotImplementedException("end of stream unhandled - reading field");
                    }

                    if (curr == JsonConstants.ESCAPE)
                    {
                        // skip!
                        requireEscapeRemoval = true;
                        if (!TryRead(ref span, out var next))
                        {
                            throw new NotImplementedException("end of stream unhandled - reading field");
                        }
                        else
                        {
                            switch (next)
                            {
                                case JsonConstants.QUOTE:
                                case JsonConstants.ESCAPE:
                                    break;
                                default:
                                    throw new NotSupportedException(
                                        "hit escape character, but found no character that requires escaping. Add support for more escape chars");
                            }
                        }
                    } else if (curr == JsonConstants.QUOTE)
                    {
                        found = true;
                    }
                }

                field = span.Slice(start, index - start - 1);
                if (requireEscapeRemoval)
                {
                    var buffer = new StringBuilder();
                    for (var i = 0; i < field.Length; i++)
                    {
                        var c = field[i];
                        switch (c)
                        {
                            // case JsonConstants.QUOTE:
                            case JsonConstants.ESCAPE:
                                // peek at the next character... 
                                //
                                if (i + 1 < field.Length)
                                {
                                    var peek = field[i + 1];
                                    switch (peek)
                                    {
                                        // skip certain characters? 
                                        case JsonConstants.ESCAPE:
                                            buffer.Append(c);
                                            i++;
                                            break;
                                    }
                                }
                                
                                // skip
                                break;
                            default:
                                buffer.Append(c);
                                break;
                        }
                    }

                    field = buffer.ToString().AsSpan();
                }
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
            
            if (jsonable is IJsonableSerializationCallbacks cbr)
            {
                cbr.OnAfterDeserialized();
            }
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

        public void IncludeField(string name, ref ulong fieldValue)
        {
            throw new NotImplementedException();
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

        public void IncludeField(string name, ref double fieldValue)
        {
            if (_data.ints.TryGetValue(name, out var intVal))
                fieldValue = intVal;
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

        private void AppendEscaped(string value)
        {
            // Fast path: scan for first char that needs escaping
            var segStart = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"' || c == '\\')
                {
                    if (i > segStart) _sb.Append(value, segStart, i - segStart);
                    _sb.Append(c == '"' ? "\\\"" : "\\\\");
                    segStart = i + 1;
                }
            }
            if (segStart < value.Length) _sb.Append(value, segStart, value.Length - segStart);
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
            if (jsonable is IJsonableSerializationCallbacks cbr)
            {
                cbr.OnBeforeSerialize();
            }
            jsonable.ProcessJson(this);
            _sb.Append(JsonConstants.CLOSE_BRACKET);
        }

        public void IncludeField(string name, ref int fieldValue) => IncludePrim(name, ref fieldValue);
        public void IncludeField(string name, ref byte fieldValue) => IncludePrim(name, ref fieldValue);
        public void IncludeField(string name, ref ulong fieldValue) => IncludePrim(name, ref fieldValue);

        public void IncludeField(string name, ref double fieldValue)
        {
            if (fieldCount > 0) _sb.Append(JsonConstants.COMMA);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(name);
            _sb.Append(JsonConstants.QUOTE);
            _sb.Append(JsonConstants.COLON);
            _sb.Append(fieldValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            fieldCount++;
        }

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
                AppendEscaped(fieldValue);
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

    public class StreamingJsonReadOp : IJsonOperation
    {
        private readonly string _json;
        // Flat field index: [nameStart, nameLen, valuePos] per field (stride 3)
        private int[] _fields;
        private int _fieldCount;
        internal int ObjEnd;

        private const int FieldStride = 3;
        private const int InitialCapacity = 8; // covers most IJsonable types

        public StreamingJsonReadOp(string json, int objStart)
        {
            _json = json;
            _fields = new int[InitialCapacity * FieldStride];
            BuildIndex(objStart);
        }

        private void BuildIndex(int i)
        {
            i++; // skip '{'
            SkipWs(ref i);
            while (i < _json.Length && _json[i] != '}')
            {
                // Record field name span without allocating a string
                i++; // skip opening '"'
                var nameStart = i;
                while (i < _json.Length && _json[i] != '"')
                {
                    if (_json[i] == '\\') i++; // skip escaped char
                    i++;
                }
                var nameLen = i - nameStart;
                i++; // skip closing '"'
                SkipWs(ref i);
                i++; // skip ':'
                SkipWs(ref i);

                if (_fieldCount * FieldStride >= _fields.Length)
                {
                    var grown = new int[_fields.Length * 2];
                    Array.Copy(_fields, grown, _fields.Length);
                    _fields = grown;
                }
                var slot = _fieldCount * FieldStride;
                _fields[slot]     = nameStart;
                _fields[slot + 1] = nameLen;
                _fields[slot + 2] = i; // value position
                _fieldCount++;

                SkipValue(ref i);
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
            ObjEnd = i + 1; // past '}'
        }

        private int FindField(string name)
        {
            var len = name.Length;
            for (var f = 0; f < _fieldCount; f++)
            {
                var slot = f * FieldStride;
                if (_fields[slot + 1] != len) continue;
                var start = _fields[slot];
                var match = true;
                for (var j = 0; j < len; j++)
                    if (_json[start + j] != name[j]) { match = false; break; }
                if (match) return _fields[slot + 2];
            }
            return -1;
        }

        public void Process(IJsonable jsonable)
        {
            jsonable.ProcessJson(this);
            if (jsonable is IJsonableSerializationCallbacks cb)
                cb.OnAfterDeserialized();
        }

        private void SkipWs(ref int i)
        {
            while (i < _json.Length && (_json[i] == ' ' || _json[i] == '\t' || _json[i] == '\r' || _json[i] == '\n'))
                i++;
        }

        private void SkipString(ref int i)
        {
            i++; // skip '"'
            while (i < _json.Length)
            {
                var c = _json[i++];
                if (c == '\\') i++;
                else if (c == '"') return;
            }
        }

        private void SkipBraced(ref int i)
        {
            var open = _json[i];
            var close = open == '{' ? '}' : ']';
            var depth = 1;
            i++;
            while (i < _json.Length && depth > 0)
            {
                var c = _json[i++];
                if (c == '"') { i--; SkipString(ref i); }
                else if (c == open) depth++;
                else if (c == close) depth--;
            }
        }

        private void SkipValue(ref int i)
        {
            if (i >= _json.Length) return;
            var c = _json[i];
            if (c == '"') SkipString(ref i);
            else if (c == '{' || c == '[') SkipBraced(ref i);
            else if (c == 'n') i += 4; // null
            else if (c == 't') i += 4; // true
            else if (c == 'f') i += 5; // false
            else // number
            {
                while (i < _json.Length && (_json[i] == '-' || _json[i] == '+' || char.IsDigit(_json[i]) || _json[i] == '.' || _json[i] == 'e' || _json[i] == 'E'))
                    i++;
            }
        }

        private string ReadStringValue(ref int i)
        {
            i++; // skip '"'
            var start = i;
            var hasEscape = false;
            while (i < _json.Length)
            {
                var c = _json[i++];
                if (c == '\\') { hasEscape = true; i++; }
                else if (c == '"') break;
            }
            var contentEnd = i - 1;
            if (!hasEscape)
                return _json.Substring(start, contentEnd - start);
            var sb = new StringBuilder(contentEnd - start);
            for (var j = start; j < contentEnd; j++)
            {
                var c = _json[j];
                if (c == '\\' && j + 1 < contentEnd)
                {
                    j++;
                    switch (_json[j])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(_json[j]); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private bool IsNull(int i) => i < _json.Length && _json[i] == 'n';

        private int ParseIntAt(int i)
        {
            var end = i;
            if (end < _json.Length && _json[end] == '-') end++;
            while (end < _json.Length && char.IsDigit(_json[end])) end++;
            int.TryParse(_json.Substring(i, end - i), out var value);
            return value;
        }

        private double ParseDoubleAt(int i)
        {
            var end = i;
            if (end < _json.Length && (_json[end] == '-' || _json[end] == '+')) end++;
            while (end < _json.Length && (char.IsDigit(_json[end]) || _json[end] == '.' || _json[end] == 'e' || _json[end] == 'E' || _json[end] == '+' || _json[end] == '-'))
                end++;
            double.TryParse(_json.Substring(i, end - i), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value);
            return value;
        }

        public void IncludeField(string name, ref int fieldValue)
        {
            if (FindField(name) is var i && i >= 0 && !IsNull(i))
                fieldValue = ParseIntAt(i);
        }

        public void IncludeField(string name, ref byte fieldValue)
        {
            if (FindField(name) is var i && i >= 0 && !IsNull(i))
                fieldValue = (byte)ParseIntAt(i);
        }

        public void IncludeField(string name, ref bool fieldValue)
        {
            if (FindField(name) is var i && i >= 0 && !IsNull(i))
                fieldValue = ParseIntAt(i) != 0;
        }

        public void IncludeField(string name, ref string fieldValue)
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) { fieldValue = null; return; }
            fieldValue = ReadStringValue(ref i);
        }

        public void IncludeField(string name, ref byte[] fieldValue)
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) { fieldValue = Array.Empty<byte>(); return; }
            i++; // skip '['
            SkipWs(ref i);
            if (i < _json.Length && _json[i] == ']') { fieldValue = Array.Empty<byte>(); return; }
            var list = new List<byte>();
            while (i < _json.Length && _json[i] != ']')
            {
                SkipWs(ref i);
                list.Add((byte)ParseIntAt(i));
                SkipValue(ref i);
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
            fieldValue = list.ToArray();
        }

        public void IncludeField(string name, ref int[] fieldValue)
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) { fieldValue = Array.Empty<int>(); return; }
            i++; // skip '['
            SkipWs(ref i);
            if (i < _json.Length && _json[i] == ']') { fieldValue = Array.Empty<int>(); return; }
            var list = new List<int>();
            while (i < _json.Length && _json[i] != ']')
            {
                SkipWs(ref i);
                list.Add(ParseIntAt(i));
                SkipValue(ref i);
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
            fieldValue = list.ToArray();
        }

        public void IncludeField(string name, ref double fieldValue)
        {
            if (FindField(name) is var i && i >= 0 && !IsNull(i))
                fieldValue = ParseDoubleAt(i);
        }

        public void IncludeField(string name, ref DebugMessageType fieldValue)
        {
            if (FindField(name) is var i && i >= 0 && !IsNull(i))
                fieldValue = (DebugMessageType)ParseIntAt(i);
        }

        public void IncludeField(string name, ref Dictionary<string, int> fieldValue)
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) return;
            fieldValue = new Dictionary<string, int>();
            i++; // skip '{'
            SkipWs(ref i);
            while (i < _json.Length && _json[i] != '}')
            {
                var key = ReadStringValue(ref i);
                SkipWs(ref i);
                i++; // skip ':'
                SkipWs(ref i);
                fieldValue[key] = ParseIntAt(i);
                SkipValue(ref i);
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
        }

        public void IncludeField<T>(string name, ref T fieldValue) where T : IJsonable, new()
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) return;
            var subOp = new StreamingJsonReadOp(_json, i);
            fieldValue = new T();
            subOp.Process(fieldValue);
        }

        public void IncludeField<T>(string name, ref List<T> fieldValue) where T : IJsonable, new()
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) return;
            i++; // skip '['
            SkipWs(ref i);
            fieldValue = new List<T>();
            while (i < _json.Length && _json[i] != ']')
            {
                var subOp = new StreamingJsonReadOp(_json, i);
                var item = new T();
                subOp.Process(item);
                i = subOp.ObjEnd;
                fieldValue.Add(item);
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
        }

        public void IncludeField<T>(string name, ref Dictionary<int, T> fieldValue) where T : IJsonable, new()
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) return;
            fieldValue = new Dictionary<int, T>();
            i++; // skip '{'
            SkipWs(ref i);
            while (i < _json.Length && _json[i] != '}')
            {
                var key = ReadStringValue(ref i);
                SkipWs(ref i);
                i++; // skip ':'
                SkipWs(ref i);
                int.TryParse(key, out var intKey);
                var subOp = new StreamingJsonReadOp(_json, i);
                var item = new T();
                subOp.Process(item);
                i = subOp.ObjEnd;
                fieldValue[intKey] = item;
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
        }

        public void IncludeField<T>(string name, ref Dictionary<string, T> fieldValue) where T : IJsonable, new()
        {
            var i = FindField(name); if (i < 0) return;
            if (IsNull(i)) return;
            fieldValue = new Dictionary<string, T>();
            i++; // skip '{'
            SkipWs(ref i);
            while (i < _json.Length && _json[i] != '}')
            {
                var key = ReadStringValue(ref i);
                SkipWs(ref i);
                i++; // skip ':'
                SkipWs(ref i);
                var subOp = new StreamingJsonReadOp(_json, i);
                var item = new T();
                subOp.Process(item);
                i = subOp.ObjEnd;
                fieldValue[key] = item;
                SkipWs(ref i);
                if (i < _json.Length && _json[i] == ',') { i++; SkipWs(ref i); }
            }
        }
    }
}