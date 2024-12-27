using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual
{
    public class IndexCollection
    {
        private readonly List<DebugToken> _statementTokens;

        public IndexCollection(List<DebugToken> statementTokens)
        {
            _statementTokens = statementTokens.ToList();
            _statementTokens.Sort((a, b) => a.insIndex.CompareTo(b.insIndex));
        }

        public bool TryFindClosestTokenAtLocation(int lineNumber, int colNumber, out DebugToken token)
        {
            token = null;
            var bestLineDiff = int.MaxValue;
            int bestColDiff = int.MaxValue;
            for (var i = 0; i < _statementTokens.Count; i++)
            {
                var statementToken = _statementTokens[i];

                var lineDiff = Math.Abs(statementToken.token.lineNumber - lineNumber);
                if (lineDiff < bestLineDiff)
                {
                    bestColDiff = int.MaxValue;
                }
                if (lineDiff <= bestLineDiff)
                {
                    bestLineDiff = lineDiff;

                    var colDiff = Math.Abs(statementToken.token.charNumber - colNumber);
                    if (colDiff < bestColDiff)
                    {
                        bestColDiff = colDiff;
                        token = statementToken;

                        if (colDiff == 0 && lineDiff == 0) return true;
                    }
                }
            }

            return token != null;
        }
        
        public bool TryFindClosestTokenBeforeIndex(int insIndex, out DebugToken token)
        {
            // TODO: make this a binary search tree or something... Later? 

            token = null;
            for (var i = 1; i < _statementTokens.Count; i++)
            {
               
                if (_statementTokens[i].insIndex > insIndex)
                {
                    token = _statementTokens[i - 1];
                    return true;
                }
                if (i == _statementTokens.Count - 1)
                {
                    token = _statementTokens[i];
                    return true;
                }
            }

            return false;
        }
    }
}