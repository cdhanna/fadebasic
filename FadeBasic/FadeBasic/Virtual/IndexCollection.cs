using System;
using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual
{
    public class IndexCollection
    {
        // Sorted by insIndex (see constructor).
        private readonly DebugToken[] _statementTokens;

        // Parallel int[] so binary search reads a dense value array instead of
        // chasing DebugToken heap pointers on every probe (cache miss per probe
        // otherwise, since binary search is non-sequential).
        private readonly int[] _insIndexes;

        // Pre-filtered versions for the ignoreComputed=true hot path, so we
        // never need to walk backward after the binary search.
        private readonly DebugToken[] _nonComputedTokens;
        private readonly int[] _nonComputedIndexes;

        public IndexCollection(List<DebugToken> statementTokens)
        {
            var sorted = statementTokens.ToList();
            sorted.Sort((a, b) => a.insIndex.CompareTo(b.insIndex));
            _statementTokens = sorted.ToArray();

            _insIndexes = new int[_statementTokens.Length];
            for (int i = 0; i < _statementTokens.Length; i++)
                _insIndexes[i] = _statementTokens[i].insIndex;

            var nc = _statementTokens.Where(t => t.isComputed != 1).ToArray();
            _nonComputedTokens = nc;
            _nonComputedIndexes = new int[nc.Length];
            for (int i = 0; i < nc.Length; i++)
                _nonComputedIndexes[i] = nc[i].insIndex;
        }

        public bool TryFindClosestTokenAtLocation(int lineNumber, int colNumber, out DebugToken token)
        {
            token = null;
            var bestLineDiff = int.MaxValue;
            int bestColDiff = int.MaxValue;
            for (var i = 0; i < _statementTokens.Length; i++)
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

        public bool TryFindClosestTokenBeforeIndex(int insIndex, out DebugToken token, bool ignoreComputed=false)
        {
            var indexes = ignoreComputed ? _nonComputedIndexes : _insIndexes;
            var tokens  = ignoreComputed ? _nonComputedTokens  : _statementTokens;

            token = null;
            var count = indexes.Length;
            if (count == 0) return false;

            // Binary search over a dense int[] — no pointer chasing, cache-friendly.
            // Finds the rightmost entry whose insIndex <= query.
            int lo = 0, hi = count - 1, result = -1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (indexes[mid] <= insIndex)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (result < 0) return false;

            token = tokens[result];
            return true;
        }
    }
}