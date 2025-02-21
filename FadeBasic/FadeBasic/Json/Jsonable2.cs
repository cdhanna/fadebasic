using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace FadeBasic.Json
{
    public class Jsonable2
    {
        public enum JsonLexem
        {
            NULL,
            OPEN_BRACKET,
            CLOSE_BRACKET,
            OPEN_ARRAY,
            CLOSE_ARRAY,
            COLON,
            COMMA,
            STRING_LITERAL,
            NUMBER_LITERAL,
        }
        
        [DebuggerDisplay("{startIndex}:{length} - [{type}]")]
        public struct JsonToken
        {
            public JsonLexem type;
            public int startIndex, length;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ReadOnlySpan<char> Slice(ref ReadOnlySpan<char> src) => src.Slice(startIndex, length);
        }

        class JsonTokenStream
        {
            public List<JsonToken> tokens;
            public int index;

            public bool IsEof => index >= tokens.Count;
            
            public void Assert(JsonLexem lexem)
            {
                if (tokens[index].type != lexem)
                {
                    throw new Exception($"Invalid Json. Expected=[{lexem}] Found=[{tokens[index].type}] Index=[{tokens[index].startIndex}]");
                }
            }
            
            public JsonToken Next() => tokens[++index];
        }

        public static JsonData Parse(string json)
        {
            var span = json.AsSpan();

            var stream = new JsonTokenStream
            {
                tokens = Lex(ref span),
                index = 0
            };
            
            return ParseObject(stream, ref span);
        }


        static (List<JsonData>, List<int>) ParseArray(JsonTokenStream stream, ref ReadOnlySpan<char> json)
        {
            var datas = new List<JsonData>();
            var nums = new List<int>();

            stream.Assert(JsonLexem.OPEN_ARRAY);

            var searching = true;
            while (searching && !stream.IsEof)
            {
                var next = stream.Next();

                switch (next.type)
                {
                    case JsonLexem.NUMBER_LITERAL:
                        if (!int.TryParse(next.Slice(ref json).ToString(), out var number))
                        {
                            number = 0;
                        }
                        nums.Add(number);
                        
                        break;
                    case JsonLexem.OPEN_BRACKET:
                        var objElem = ParseObject(stream, ref json);
                        datas.Add(objElem);
                        break;
                    case JsonLexem.OPEN_ARRAY:
                        _ = ParseArray(stream, ref json);
                        // TODO: cannot handle arrays of arrays
                        break;
                    case JsonLexem.CLOSE_ARRAY:
                        // end of array!
                        searching = false;
                        break;
                }
            }

            return (datas, nums);
        }
        
        static JsonData ParseObject(JsonTokenStream stream, ref ReadOnlySpan<char> json)
        {
            var data = new JsonData();
            stream.Assert(JsonLexem.OPEN_BRACKET);

            var searching = true;
            while (searching && !stream.IsEof)
            {
                var next = stream.Next();
                switch (next.type)
                {
                    case JsonLexem.STRING_LITERAL:
                        var rawKey = next.Slice(ref json);
                        var key = rawKey.Slice(1, rawKey.Length - 2);
                        // key!
                        var _ = stream.Next();
                        stream.Assert(JsonLexem.COLON);
                        
                        // parse a value? 
                        var next2 = stream.Next();
                        switch (next2.type)
                        {
                            case JsonLexem.STRING_LITERAL:
                                // string field!
                                var rawValue = next2.Slice(ref json);
                                var value = rawValue.Slice(1, rawValue.Length - 2);
                                var valueStr = value.ToString()
                                        .Replace("\\\"", "\"")
                                        .Replace("\\\\", "\\")
                                    ;
                                data.strings.Add(key.ToString(), valueStr);
                                break;
                            case JsonLexem.NUMBER_LITERAL:
                                // number field!
                                if (!int.TryParse(next2.Slice(ref json).ToString(), out var number))
                                {
                                    number = 0;
                                }
                                data.ints.Add(key.ToString(), number);
                                break;
                            case JsonLexem.OPEN_ARRAY:
                                // array field!
                                (data.arrays[key.ToString()], data.numberArrays[key.ToString()]) = ParseArray(stream, ref json);
                                break;
                            case JsonLexem.OPEN_BRACKET:
                                var subObj = ParseObject(stream, ref json);
                                data.objects.Add(key.ToString(), subObj);
                               
                                break;
                        }
                        
                        break;
                    case JsonLexem.CLOSE_BRACKET:
                        searching = false;
                        break;
                }
            }

            return data;
        }

        static void AssertToken(JsonTokenStream stream, JsonLexem lexem)
        {
            stream.Assert(lexem);
        }
        
        static List<JsonToken> Lex( ref ReadOnlySpan<char> span)
        {
            var tokens = new List<JsonToken>();

            { // lex tokens
                for (var i = 0; i < span.Length; i++)
                {
                    var c = span[i];

                    if (char.IsWhiteSpace(c)) continue; // skip white space!

                    switch (c)
                    {
                        case 'n': // null
                            // hack; just assume that a valid null keyword is there :shrug:
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = "null".Length,
                                type = JsonLexem.NULL
                            });
                            i += 3;
                            break;
                        case '{':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.OPEN_BRACKET
                            });
                            break;
                        case '}':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.CLOSE_BRACKET
                            });
                            break;
                        case '[':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.OPEN_ARRAY
                            });
                            break;
                        case ']':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.CLOSE_ARRAY
                            });
                            break;
                        case ':':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.COLON
                            });
                            break;
                        case ',':
                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = 1,
                                type = JsonLexem.COMMA
                            });
                            break;

                        case '-': // negative number?
                        case var d when char.IsDigit(d):
                            // there is a number ahead! Parse until the number is done. 
                            var endNumberIndex = -1;
                            for (var j = i + 1; j < span.Length && endNumberIndex == -1; j++)
                            {
                                var c2 = span[j];
                                if (!char.IsDigit(c2) && c2 != '.')
                                {
                                    endNumberIndex = j;
                                }
                            }

                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = endNumberIndex - i,
                                type = JsonLexem.NUMBER_LITERAL
                            });
                            i = endNumberIndex - 1;

                            break;
                        case '"':
                            // now we need to parse a string, and handle escaping correctly.

                            var endStringIndex = -1;
                            var skipNextQuote = false;
                            for (var j = i + 1; j < span.Length && endStringIndex < 0; j++)
                            {
                                var c2 = span[j];
                                switch (c2)
                                {
                                    case '\\':
                                        
                                        // exactly the next character is ignored from special string handling. 
                                        j++;
                                        
                                        break;
                                    case '"':
                                        // the string is over!
                                        endStringIndex = j;
                                        break;
                                    default:
                                        break;
                                }
                            }

                            tokens.Add(new JsonToken
                            {
                                startIndex = i,
                                length = (endStringIndex - i) + 1,
                                type = JsonLexem.STRING_LITERAL
                            });
                            i = endStringIndex;

                            break;
                    }

                }

            }

            return tokens;
        }
    }
}