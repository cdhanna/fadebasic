

namespace FadeBasic.ApplicationSupport.Project;

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
    public List<(string, Range)> fileRanges = new List<(string, Range)>();
    private Dictionary<string, Range> _fileToRange = new Dictionary<string, Range>();
    private Dictionary<int, List<Token>> _lineToTokens = new Dictionary<int, List<Token>>();
    public string fullSource;
    
    public SourceMap()
    {
        
    }

    public SourceMap(string fullSource, List<(string, Range)> fileRanges)
    {
        this.fullSource = fullSource;
        this.fileRanges = fileRanges;
        foreach (var elem in fileRanges)
        {
            _fileToRange[elem.Item1] = elem.Item2;
        }
        
    }

    public SourceRange GetOriginalRange(TokenRange range)
    {
        var startLocation = GetOriginalLocation(range.start.lineNumber, range.start.charNumber);
        var endLocation = startLocation;
        if (range.end != null)
        {
            endLocation = GetOriginalLocation(range.end.lineNumber, range.end.charNumber + range.end.raw?.Length ?? 0);
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

    public SourceLocation GetOriginalLocation(Token token) => GetOriginalLocation(token.lineNumber, token.charNumber);
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

        throw new NotSupportedException("given range was not found in sourcemap");

    }

    public bool GetMappedPosition(string file, int lineNumber, int charNumber, out Token foundToken, bool strict=true)
    {
        foundToken = null;
        if (!_fileToRange.TryGetValue(file, out var fileRange))
        {
            if (!strict) return false;
            throw new ArgumentOutOfRangeException(nameof(file), $"given file=[{file}] is not included in source map");
        }

        var startLine = fileRange.Start.Value;

        var line = startLine + lineNumber;
        if (!_lineToTokens.TryGetValue(line, out var tokens))
        {
            if (!strict) return false;
            throw new InvalidOperationException($"given line=[{line}] do no map to an existing tokens");
        }
        
        // need to find the token that contains the given char number. 
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.charNumber <= charNumber && token.charNumber + (token.raw?.Length ?? 0) > charNumber)
            {
                foundToken = token;
                return true;
            }
        }

        return false;
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

    struct LineChar
    {
        public bool Equals(LineChar other)
        {
            return lineNumber == other.lineNumber && charNumber == other.charNumber;
        }

        public override bool Equals(object? obj)
        {
            return obj is LineChar other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(lineNumber, charNumber);
        }

        public int lineNumber, charNumber;
    }
}