using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace FadeBasic
{
    public class TokenFormatSettings
    {
        public enum CasingSetting
        {
            ToUpper,
            ToLower,
            Ignore
        }
        
        public CasingSetting Casing;
        public int TabSize;
        public bool UseTabs;

        public static readonly TokenFormatSettings Default = new TokenFormatSettings
        {
            Casing = CasingSetting.ToUpper,
            TabSize = 4,
            UseTabs = false
        };
    }
    
    [DebuggerDisplay("[{startLine}:{startChar}]-[{endLine}:{endChar}] -> \"{replacement}\"")]
    public struct TokenFormatEdit
    {
        public int startLine, startChar;
        public int endLine, endChar;
        public string replacement;
    }
    
    public class TokenFormatter
    {
        public static string ApplyEdits(string src, List<TokenFormatEdit> edits)
        {
            // var lines = src.Split(new string[]{Environment.NewLine}, StringSplitOptions.None);
            var lines = src.SplitNewLines();
            // apply the edits backwards so that they don't interfere with the indexes of other edits. 
            for (var i = edits.Count - 1 ; i >= 0; i --)
            {
                var edit = edits[i];
                if (edit.startLine != edit.endLine)
                {
                    throw new NotImplementedException("cannot do multi-line edits yet :( ");
                }

                var line = lines[edit.startLine];
                var before = line.Substring(0, edit.startChar);
                var after = line.Substring(edit.endChar);
                lines[edit.startLine] = before + edit.replacement + after;
            }

            return string.Join(Environment.NewLine, lines);
        }

        [Flags]
        enum LexemFlags
        {
            NONE = 1 << 0,
            NO_SPACE_TRAILING = 1 << 1,
            NO_SPACE_LEADING = 1 << 2,
            NO_SPACE = NO_SPACE_TRAILING | NO_SPACE_LEADING,
            PUSH_INDENT = 1 << 3,
            POP_INDENT = 1 << 4,
            PUSH_AND_POP_INDENT = PUSH_INDENT | POP_INDENT,
            UNCASED = 1 << 5
        }

        private static Dictionary<LexemType, LexemFlags> typeToFlags = new Dictionary<LexemType, LexemFlags>
        {
            [LexemType.EndStatement] = LexemFlags.NO_SPACE,
            
            [LexemType.VariableGeneral] = LexemFlags.UNCASED,
            [LexemType.LiteralString] = LexemFlags.UNCASED,
            [LexemType.Constant] = LexemFlags.UNCASED,
            
            [LexemType.FieldSplitter] = LexemFlags.NO_SPACE,
            [LexemType.ParenOpen] = LexemFlags.NO_SPACE,
            [LexemType.ParenClose] = LexemFlags.NO_SPACE,
            [LexemType.ArgSplitter] = LexemFlags.NO_SPACE_TRAILING,
            
            [LexemType.KeywordIf] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordFor] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordFunction] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordWhile] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordDo] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordRepeat] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordSelect] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordCase] = LexemFlags.PUSH_INDENT,
            [LexemType.KeywordCaseDefault] = LexemFlags.PUSH_INDENT,
            
            [LexemType.KeywordElse] = LexemFlags.PUSH_AND_POP_INDENT,
            
            [LexemType.KeywordEndIf] = LexemFlags.POP_INDENT,
            [LexemType.KeywordNext] = LexemFlags.POP_INDENT,
            [LexemType.KeywordThen] = LexemFlags.POP_INDENT,
            [LexemType.KeywordEndFunction] = LexemFlags.POP_INDENT,
            [LexemType.KeywordEndWhile] = LexemFlags.POP_INDENT,
            [LexemType.KeywordLoop] = LexemFlags.POP_INDENT,
            [LexemType.KeywordUntil] = LexemFlags.POP_INDENT,
            [LexemType.KeywordEndCase] = LexemFlags.POP_INDENT,
            [LexemType.KeywordEndSelect] = LexemFlags.POP_INDENT,
            // TODO: add other keywords
        };

        private static Dictionary<int, string> spaceStrings = new Dictionary<int, string>();
        private static Dictionary<int, string> tabStrings = new Dictionary<int, string>();

        public static List<TokenFormatEdit> Format(List<Token> tokens, TokenFormatSettings settings=default)
        {
            if (settings == null) settings = TokenFormatSettings.Default;
            var edits = new List<TokenFormatEdit>();

            { // remove all invalid tokens from formatting
                var clone = new List<Token>();
                foreach (var t in tokens)
                {
                    if (t.lexem.type == LexemType.EndStatement) continue;
                    clone.Add(t);
                }

                tokens = clone;
            }

            { // when using tabs, the tab size must be >1
                if (settings.UseTabs)
                {
                    // the tab size doesn't matter, because it gets divided out in the end.
                    settings.TabSize = 4;
                }
            }

            Token prevToken = null;
            Token token = null;
            var indentLevel = 0;
            
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                // consider the token...
                
                // formatting can happen on
                //  the LEFT of the token, 
                //  or the RIGHT of the token.
                //  but it cannot happen INSIDE the token, because that would alter the program.

                prevToken = token;
                token = tokens[tokenIndex];
                if (!typeToFlags.TryGetValue(token.type, out var flags))
                {
                    flags = LexemFlags.NONE;
                }

                if (prevToken == null || !typeToFlags.TryGetValue(prevToken.type, out var prevFlags))
                {
                    prevFlags = LexemFlags.NONE;
                }
                
                // compute right-side first so they get applied first.

                var noTrailing = flags.HasFlag(LexemFlags.NO_SPACE_TRAILING);
                var noLeading = prevFlags.HasFlag(LexemFlags.NO_SPACE_LEADING);
                var pushIndent = flags.HasFlag(LexemFlags.PUSH_INDENT);
                var popIndent = flags.HasFlag(LexemFlags.POP_INDENT);
                var changeCasing = !flags.HasFlag(LexemFlags.UNCASED);

                var isNewLine = prevToken == null || prevToken.lineNumber < token.lineNumber;

                var leftGap = 0;
                var startChar = 0;
                var desiredLeftGap = 1;
                
                
                if (noLeading || noTrailing)
                {
                    desiredLeftGap = 0;
                }
                
                if (popIndent) indentLevel--;

                if (isNewLine)
                {
                    startChar = 0;
                    desiredLeftGap = indentLevel * settings.TabSize;
                }
                else
                {
                    startChar = prevToken.EndCharNumber;
                }
                leftGap = token.charNumber - startChar;
                
                if (pushIndent) indentLevel++;

                var needsReCasing = false;
                switch (settings.Casing)
                {
                    case TokenFormatSettings.CasingSetting.ToUpper:
                        needsReCasing = changeCasing && token.raw != token.raw.ToUpperInvariant();
                        break;
                    case TokenFormatSettings.CasingSetting.ToLower:
                        needsReCasing = changeCasing && token.raw != token.raw.ToLowerInvariant();
                        break;
                    case TokenFormatSettings.CasingSetting.Ignore:
                        needsReCasing = false;
                        break;
                }
                if (needsReCasing)
                {
                    var replacement = settings.Casing == TokenFormatSettings.CasingSetting.ToLower
                        ? token.raw.ToLowerInvariant()
                        : token.raw.ToUpperInvariant();
                    var edit = new TokenFormatEdit
                    {
                        startLine = token.lineNumber,
                        endLine = token.lineNumber,
                        startChar = token.charNumber,
                        endChar = token.EndCharNumber,
                        replacement = replacement
                    };
                    edits.Add(edit);
                }

                var isBadGap = leftGap != desiredLeftGap;
                if (isBadGap)
                {
                    string replacement;
                    if (settings.UseTabs && desiredLeftGap > 1) // "likely" a tab situation...
                    {
                        var tabCount = desiredLeftGap / settings.TabSize;
                        if (!tabStrings.TryGetValue(tabCount, out replacement))
                        {
                            replacement = tabStrings[tabCount] = new string('\t', tabCount);
                        }
                    }
                    else
                    {
                        if (!spaceStrings.TryGetValue(desiredLeftGap, out replacement))
                        {
                            replacement = spaceStrings[desiredLeftGap] = new String(' ', desiredLeftGap);
                        }
                    }
                    var edit = new TokenFormatEdit
                    {
                        startLine = token.lineNumber,
                        endLine = token.lineNumber,
                        startChar = startChar,
                        endChar = token.charNumber,
                        replacement = replacement
                    };
                    edits.Add(edit);
                }
                
                
            }
            
            return edits;
        }
            
    }
}