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
        public Dictionary<int, DebugToken> insToFunction = new Dictionary<int, DebugToken>();
        public List<DebugToken> statementTokens = new List<DebugToken>();
        

        public void AddFakeDebugToken(int insIndex, Token token)
        {
            statementTokens.Add(new DebugToken
            {
                insIndex = insIndex,
                token = token,
                isComputed = 1
            });
        }
        public void AddStatementDebugToken(int insIndex, Token token)
        {
            statementTokens.Add(new DebugToken
            {
                insIndex = insIndex,
                token = token
            });
        }
        
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
            statementTokens.Add(new DebugToken
            {
                insIndex = insIndex,
                token = functionNameToken,
                isComputed = 1
            });
        }

        public void AddVariable(int insIndex, CompiledArrayVariable compiledVar)
        {
            AddVariable(new DebugVariable
            {
                insIndex = insIndex,
                name = compiledVar.name,
                isPtr = 1
            });
        }
        public void AddVariable(int insIndex, CompiledVariable compiledVar)
        {
            
            AddVariable(new DebugVariable
            {
                insIndex = insIndex,
                name = compiledVar.name,
                isPtr = string.IsNullOrEmpty(compiledVar.structType) ? 0 : 1
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
    
    [DebuggerDisplay("[{insIndex}] - {token}")]
    public class DebugToken : IJsonable
    {
        public int insIndex;
        public Token token;
        
        /// <summary>
        /// When the token is being manufactured within the compiler, and ISN'T directly tied to source code. 
        /// </summary>
        public int isComputed;
        
        public void ProcessJson(IJsonOperation op)
        {
            op.IncludeField(nameof(insIndex), ref insIndex);
            op.IncludeField(nameof(token), ref token);
            op.IncludeField(nameof(isComputed), ref isComputed);
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

        public int isPtr;
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
            op.IncludeField(nameof(isPtr), ref isPtr);
        }
    }
}