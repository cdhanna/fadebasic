using System.Text.RegularExpressions;

namespace DarkBasicYo;

public enum LexemType
{
    WhiteSpace,
    OpPlus,
    OpMinus,
    OpEqual,
    LiteralReal,
    LiteralInt,
    LiteralString,
    VariableInteger,
    VariableReal,
    VariableString,
    CommandWord
}

public class Lexer
{
    
    // TODOs
    /*
     * 3. comments REM REMSTART REMEND, `
     * 4. string literals
     * 5. if endif, else
     * 6. for loop stuff
     * 7. repeat until
     */
    
    public static List<Lexem> Lexems = new List<Lexem>
    {
        new Lexem(LexemType.WhiteSpace, new Regex("^(\\s|\\t|\\n)+")),
        new Lexem(LexemType.OpPlus, new Regex("^\\+")),
        new Lexem(LexemType.OpMinus, new Regex("^\\-")),
        new Lexem(LexemType.OpEqual, new Regex("^=")),
        new Lexem(LexemType.LiteralReal, new Regex("^((\\d+\\.(\\d*))|(\\.\\d+))")),
        new Lexem(LexemType.LiteralInt, new Regex("^\\d+")),
        new Lexem(LexemType.VariableString, new Regex("^([a-z,A-Z][a-z,A-Z,0-9,_]*)\\$")),
        new Lexem(LexemType.VariableReal, new Regex("^([a-z,A-Z][a-z,A-Z,0-9,_]*)#")),
        new Lexem(LexemType.VariableInteger, new Regex("^[a-z,A-Z][a-z,A-Z,0-9,_]*")),
    };
    
    
    public List<Token> Tokenize(string input, CommandCollection commands=null)
    {
        var tokens = new List<Token>();
        if (commands == null)
        {
            commands = new CommandCollection();
        }

        var lexems = Lexems.ToList();
        foreach (var command in commands.Commands)
        {
            // var pattern = "";
            var components = command.command.Select(x =>
            {
                switch (x)
                {
                    case ' ':
                        return "(\\s|\\t)+";
                    default:
                        return $"({char.ToLower(x)}|{char.ToUpper(x)})";
                }
            });
            var pattern = "^" + string.Join("", components);
            var commandLexem = new Lexem(-1, LexemType.CommandWord, new Regex(pattern));
            lexems.Add(commandLexem);
        }

        lexems.Sort((a, b) => a.priority.CompareTo(b.priority));

        var lines = input.Split("\n", StringSplitOptions.RemoveEmptyEntries);

        for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
        {
            var line = lines[lineNumber];

            for (var charNumber = 0; charNumber < line.Length; charNumber = charNumber)
            {
                var foundMatch = false;
                var subStr = line.Substring(charNumber);

                for (var lexemId = 0; lexemId < lexems.Count; lexemId++)
                {
                    var lexem = lexems[lexemId];
                    var matches = lexem.regex.Matches(subStr);
                    if (matches.Count == 1)
                    {
                        foundMatch = true;
                        var token = new Token
                        {
                            raw = matches[0].Value,
                            lexem = lexem,
                            lineNumber = lineNumber,
                            charNumber = charNumber
                        };

                        if (lexem.type != LexemType.WhiteSpace)
                        {
                            // we ignore white space in token generation
                            tokens.Add(token);
                        }
                        
                        charNumber += token.raw.Length;
                        break;
                    } else if (matches.Count > 1)
                    {
                        throw new Exception("Token exception! Too many matches!");
                    }
                }

                if (!foundMatch)
                {
                    throw new Exception($"Token exception! No match for {subStr} at {lineNumber}:{charNumber}");
                }
            }
            
        }
        
        return tokens;
    }
    
    
}

public class Lexem
{
    public readonly Regex regex;
    public readonly int priority;
    public readonly LexemType type;

    public Lexem()
    {
    }

    public Lexem(LexemType type, Regex regex)
    {
        this.type = type;
        this.regex = regex;
    }
    public Lexem(int priority, LexemType type, Regex regex)
    {
        this.priority = priority;
        this.type = type;
        this.regex = regex;
    }
}

[Serializable]
public class Token
{
    public int lineNumber;
    public int charNumber;
    public string raw;
    public LexemType type => lexem.type;
    public Lexem lexem;
}

public class TokenStream
{
    private readonly List<Token> _tokens;
    
    public int Index { get; private set; }
    
    public Token Current { get; private set; }

    public TokenStream(List<Token> tokens)
    {
        _tokens = tokens;
        Current = _tokens[0];
    }

    // public Token GetNext(int ahead=0) => _tokens[Index + ahead];
    
    
    public Token Advance()
    {
        return Current = _tokens[Index++];
    }

    public bool IsEof => Index >= _tokens.Count;
}
