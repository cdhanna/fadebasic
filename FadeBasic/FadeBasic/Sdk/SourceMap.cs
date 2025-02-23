

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FadeBasic.Sdk.Shim;

namespace FadeBasic.Sdk
{
    public struct SourceRange
    {
        public string fileName;
        public int startLine;
        public int startChar;
        public int endLine;
        public int endChar;
    }

    public struct SourceLocation
    {
        public string fileName;
        public int startLine;
        public int startChar;
    }


    public class SourceMap
    {
        public static SourceMap CreateSourceMap(List<string> sourceFilePaths, Func<string, string[]> reader = null)
        {
            if (reader == null)
            {
                reader = (path) => File.ReadAllText(path).SplitNewLines();
            }
        
            var sb = new StringBuilder();
            var totalLines = 0;
            var fileToLineRange = new List<(string, FadeShimRange)>();
            for (var i = 0; i < sourceFilePaths.Count; i++)
            {
                var file = sourceFilePaths[i];
                var lines = reader(file);
                var startLine = totalLines;
                var endLine = totalLines + lines.Length;
                totalLines += lines.Length;
                fileToLineRange.Add((file, new FadeShimRange(startLine, endLine)));
                for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
                {
                    sb.AppendLine(lines[lineNumber]);
                }
            
            }

            return new SourceMap(sb.ToString(), fileToLineRange);
        }
        
        
        public List<(string, FadeShimRange)> fileRanges = new List<(string, FadeShimRange)>();
        public Dictionary<string, FadeShimRange> _fileToRange = new Dictionary<string, FadeShimRange>();
        public Dictionary<int, List<Token>> _lineToTokens = new Dictionary<int, List<Token>>();
        public string fullSource;

        public SourceMap()
        {

        }

        public SourceMap(string fullSource, List<(string, FadeShimRange)> fileRanges)
        {
            this.fullSource = fullSource;
            this.fileRanges = fileRanges;
            foreach (var elem in fileRanges)
            {
                _fileToRange[elem.Item1.NormalizePathString()] = elem.Item2;
            }

        }


        public SourceRange GetOriginalRange(TokenRange range)
        {
            var startLocation = GetOriginalLocation(range.start.lineNumber, range.start.charNumber);
            var endLocation = startLocation;
            if (range.end != null)
            {
                endLocation = GetOriginalLocation(range.end.lineNumber,
                    range.end.charNumber + range.end.raw?.Length ?? 0);
            }

            if (startLocation.fileName != endLocation.fileName)
            {
                throw new NotSupportedException("range cannot span between files");
            }

            return new SourceRange
            {
                fileName = startLocation.fileName,
                startLine = startLocation.startLine,
                startChar = startLocation.startChar,
                endLine = endLocation.startLine,
                endChar = endLocation.startChar
            };
        }

        public SourceLocation GetOriginalLocation(Token token) =>
            GetOriginalLocation(token.lineNumber, token.charNumber);

        public SourceLocation GetOriginalLocation(int lineNumber, int charNumber)
        {
            // TODO: this could be faster with a binary tree search or something...

            for (var i = 0; i < fileRanges.Count; i++)
            {
                var (file, range) = fileRanges[i];
                if (lineNumber >= range.Start.Value && lineNumber < range.End.Value)
                {
                    return new SourceLocation
                    {
                        fileName = file,
                        startLine = lineNumber - range.Start.Value,
                        startChar = charNumber // remains unchanged
                    };
                }
            }


            throw new NotSupportedException($@"given range was not found in sourcemap. 
line=[{lineNumber}] char=[{charNumber}] fileRange-count=[{fileRanges.Count}]

file-ranges=[{string.Join(",", fileRanges.Select(kvp => $"({kvp.Item2.Start},{kvp.Item2.End})"))}]
full-source=[{fullSource}]

");

        }

        public bool GetMappedPosition(string file, int lineNumber, int charNumber, out Token foundToken,
            bool strict = true)
        {
            return GetMappedPosition(file, lineNumber, charNumber, out foundToken, out _, strict);
        }

        public bool GetMappedPosition(string file, int lineNumber, int charNumber, out Token foundToken,
            out string error, bool strict = true)
        {
            error = null;
            foundToken = null;
            file = file.NormalizePathString();
            if (!_fileToRange.TryGetValue(file, out var fileRange))
            {
                error =
                    $"given file=[{file}] is not included in source map. available files=[{string.Join(",", _fileToRange.Keys)}]";
                if (!strict) return false;
                throw new ArgumentOutOfRangeException(nameof(file), error);
            }

            var startLine = fileRange.Start.Value;

            var line = startLine + lineNumber;
            if (!_lineToTokens.TryGetValue(line, out var tokens))
            {
                error = $"given line=[{line}] do no map to an existing tokens";
                if (!strict) return false;
                throw new InvalidOperationException(error);
            }

            // need to find the token that contains the given char number. 
            if (!strict && tokens.Count > 0)
            {
                foundToken = tokens.FirstOrDefault(t => t.type != LexemType.EndStatement);
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.charNumber <= charNumber && token.charNumber + (token.raw?.Length ?? 0) > charNumber)
                {
                    foundToken = token;
                    return true;
                }
            }

            var found = foundToken != null;
            if (!found)
            {
                error = "no found token.";
            }

            return found;
        }

        public void ProvideTokens(LexerResults lexResults)
        {
            for (var i = 0; i < lexResults.combinedTokens.Count; i++)
            {
                var token = lexResults.combinedTokens[i];
                if (!_lineToTokens.TryGetValue(token.lineNumber, out var tokens))
                {
                    tokens = _lineToTokens[token.lineNumber] = new List<Token>();
                }

                tokens.Add(token);
            }
        }
        //
        // struct LineChar
        // {
        //     public bool Equals(LineChar other)
        //     {
        //         return lineNumber == other.lineNumber && charNumber == other.charNumber;
        //     }
        //
        //     public override bool Equals(object? obj)
        //     {
        //         return obj is LineChar other && Equals(other);
        //     }
        //
        //     public override int GetHashCode()
        //     {
        //         return HashCode.Combine(lineNumber, charNumber);
        //     }
        //
        //     public int lineNumber, charNumber;
        // }
    }
}