using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FadeBasic.Json;

namespace FadeBasic.Virtual
{
    /// <summary>
    /// Any given program can be compiled for Release, or for Debug.
    /// When compiled for debug, a DebugData instance is created.
    /// The data represents
    /// 1. a source map of symbols to original file location
    /// 2. a source map of symbols to compiled byte-code location
    ///    (and thus, a source map from file location to byte-code location)
    /// 3. variable names and such by their symbol info
    ///
    ///
    /// 
    /// 
    /// </summary>
    public class DebugData : IJsonable
    {
        public Dictionary<int, DebugVariable> insToVariable = new Dictionary<int, DebugVariable>();
        // public HashSet<int> insBreakpoints = new HashSet<int>();

        public Dictionary<int, DebugToken> insToFunction = new Dictionary<int, DebugToken>();
        public List<DebugToken> statementTokens = new List<DebugToken>();
        // public List<DebugMap> points = new List<DebugMap>();

        // public List<DebugMap> GetFlatPoints()
        // {
        //     var flat = new List<DebugMap>();
        //     var stack = new Stack<DebugMap>();
        //     for (var i = points.Count - 1; i >= 0; i--)
        //     {
        //         stack.Push(points[i]);
        //     }
        //     while (stack.Count > 0)
        //     {
        //         var p = stack.Pop();
        //         flat.Add(p);
        //         if (p.innerMaps != null)
        //         {
        //             for (var i = p.innerMaps.Count - 1; i >= 0; i--)
        //             {
        //                 stack.Push(p.innerMaps[i]);
        //             }
        //         }
        //     }
        //
        //     return flat;
        // }

        // this is just used for building the points
        // private Stack<DebugMap> _currentPointBuilder = new Stack<DebugMap>();


        
        public void AddStatementDebugToken(int insIndex, Token token)
        {
            statementTokens.Add(new DebugToken
            {
                insIndex = insIndex,
                token = token
            });
        }
   
        /// <summary>
        /// Every call to this method must be paired with a call to <see cref="AddVariable(FadeBasic.Virtual.DebugVariable)"/>
        /// </summary>
        /// <param name="insIndex"></param>
        /// <param name="token"></param>
        // public void AddStartToken(int insIndex, Token token)
        // {
        //     var next = new DebugMap
        //     {
        //         depth = _currentPointBuilder.Count,
        //         range = new DebugTokenRange
        //         {
        //             startToken = new DebugToken
        //             {
        //                 insIndex = insIndex,
        //                 token = token
        //             }
        //         },
        //         innerMaps = new List<DebugMap>()
        //     };
        //
        //     /*
        //      * for every start call, add to the current, and keep track of current using a stack
        //      */
        //     if (_currentPointBuilder.Count > 0)
        //     {
        //         _currentPointBuilder.Peek().innerMaps.Add(next);
        //     }
        //     else
        //     {
        //         points.Add(next);
        //     }
        //     _currentPointBuilder.Push(next);
        // }
        //
        // public void AddStopToken(int insIndex, Token token)
        // {
        //     var current = _currentPointBuilder.Pop();
        //     current.range.stopToken = new DebugToken
        //     {
        //         insIndex = insIndex,
        //         token = token
        //     };
        // }
        
        public void AddVariable(DebugVariable variable)
        {
            insToVariable[variable.insIndex] = variable;
        }

        public void AddFunction(int insIndex, Token functionNameToken)
        {
            insToFunction[insIndex] = new DebugToken
            {
                token = functionNameToken,
                insIndex = insIndex
            };
        }
        
        public void AddVariable(int insIndex, CompiledVariable compiledVar)
        {
            AddVariable(new DebugVariable
            {
                insIndex = insIndex,
                name = compiledVar.name
            });
        }

        public void ProcessJson(IJsonOperation op)
        {
            // op.IncludeField(nameof(points), ref points);
            op.IncludeField(nameof(insToVariable), ref insToVariable);
            op.IncludeField(nameof(statementTokens), ref statementTokens);
            op.IncludeField(nameof(insToFunction), ref insToFunction);
        }

    }
    //
    // public static class DebugDataJson
    // {
    //     private const string OBJ_OPEN = "{";
    //     private const string OBJ_CLOSE = "}";
    //     private const string QUOTE = "\"";
    //     private const string COLON = ":";
    //     public static void ToJson(this DebugData data, StringBuilder sb)
    //     {
    //         sb.Append(OBJ_OPEN);
    //         { // map field 
    //             sb.Append(QUOTE);
    //             sb.Append(nameof(data.points));
    //             sb.Append(QUOTE);
    //             sb.Append(COLON);
    //         }
    //         sb.Append(OBJ_CLOSE);
    //        
    //     }
    //
    //     static void ToJson(this DebugMap map, StringBuilder sb)
    //     {
    //         sb.Append(OBJ_OPEN);
    //         map.range
    //         sb.Append(OBJ_CLOSE);
    //     }
    // }

    [DebuggerDisplay("[{insIndex}] - {token}")]
    public class DebugToken : IJsonable
    {
        public int insIndex;
        public Token token;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(insIndex), ref insIndex);
            op.IncludeField(nameof(token), ref token);
        }
    }

    [DebuggerDisplay("[{startToken}-{stopToken}]")]
    public class DebugTokenRange : IJsonable
    {
        public DebugToken startToken;
        public DebugToken stopToken;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(startToken), ref startToken);
            op.IncludeField(nameof(stopToken), ref stopToken);
        }
    }
    
   

    public class DebugMap : IJsonable
    {
        public DebugTokenRange range; // the extents of this map
        public List<DebugMap> innerMaps;
        public int depth;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(depth), ref depth);
            op.IncludeField(nameof(range), ref range);
            op.IncludeField(nameof(innerMaps), ref innerMaps);
        }
    }
    
    public class DebugVariable : IJsonable
    {
        public int insIndex;
        // public byte byteSize;
        // public byte typeCode;
        public string name;
        // public string structType;
        // public byte registerAddress;
        // public bool isGlobal;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(insIndex), ref insIndex);
            op.IncludeField(nameof(name), ref name);
        }
    }
}