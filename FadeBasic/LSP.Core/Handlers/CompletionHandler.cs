// Compute completion items at a position. Builds a CompletionContext for the
// existing FadeBasic.Lsp.LSPUtil.GetCompletions which does the real work.

using System;
using System.Collections.Generic;
using System.Linq;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lsp;
using FadeBasic.SourceGenerators; // FadeBasicCommandUsage
using FadeBasic.Virtual;          // TypeCodes
using LspCompletionContext = FadeBasic.Lsp.CompletionContext;

namespace FadeBasic.LSP.Core.Handlers
{
    public static class CompletionHandler
    {
        public static List<LspCompletionItem> Compute(FadeDocument doc, int line, int character)
        {
            if (doc?.LexResults == null || doc.Program == null) return new List<LspCompletionItem>();

            var fakeToken = new Token { lineNumber = line, charNumber = character };

            // Find the nearest token to the left.
            Token leftToken = null;
            for (int i = doc.LexResults.allTokens.Count - 1; i >= 0; i--)
            {
                var token = doc.LexResults.allTokens[i];
                if (token.lineNumber < line)
                {
                    leftToken = token;
                    break;
                }
                if (token.lineNumber == line && token.charNumber <= character)
                {
                    leftToken = token;
                    break;
                }
            }

            if (leftToken == null) return new List<LspCompletionItem>();

            // Suppress completions inside comments. The lexer collapses
            // `rem ...` and `` `... `` lines into a single KeywordRem
            // token spanning the comment body, so leftToken.type ==
            // KeywordRem reliably means the cursor is inside one. The
            // GetCompletions switch below used to treat KeywordRem like
            // an end-of-statement marker (statement-completions fired
            // mid-comment), which surfaced suggestions every time the
            // user paused inside a REM. Bail out here before any of
            // the rescues run; comments don't have a useful completion
            // surface.
            //
            // KeywordRemStart / KeywordRemEnd cover the multi-line case:
            // anything after `remstart` and before a matching `remend`
            // should likewise stay quiet.
            if (leftToken.type == LexemType.KeywordRem
                || leftToken.type == LexemType.KeywordRemStart)
            {
                return new List<LspCompletionItem>();
            }

            bool isMacro = leftToken.flags.HasFlag(TokenFlags.IsMacroToken);

            bool Visit(IAstVisitable v)
            {
                return v is ProgramNode
                    || (Token.IsLocationBeforeOrEqual(v.StartToken, fakeToken)
                        && Token.IsLocationBeforeOrEqual(fakeToken, v.EndToken));
            }

            ProgramNode programNode;
            IEnumerable<IAstVisitable> group;
            if (isMacro && doc.LexResults.macroProgram != null)
            {
                programNode = doc.LexResults.macroProgram;
                group = programNode?.Where(Visit);
            }
            else
            {
                programNode = doc.Program;
                group = programNode?.Where(Visit);
            }

            if (programNode == null) return new List<LspCompletionItem>();

            // Locate the function/scope context the position is inside.
            if (!programNode.scope.positionedVariables.TryFindEntry(fakeToken, out var entry))
            {
                if (programNode.scope.positionedVariables.entries.Count == 0)
                    return new List<LspCompletionItem>();
                entry = programNode.scope.positionedVariables.entries[0];
            }

            var context = new LspCompletionContext
            {
                IsMacro = isMacro,
                FakeToken = fakeToken,
                LeftToken = leftToken,
                Program = programNode,
                Commands = doc.Commands,
                FunctionName = entry.value.Item2,
                Group = group?.ToList(),
                ConstantTable = doc.LexResults.constantTable,
                LocalScope = entry.value.Item1,
            };

            // Struct-field rescue: user just typed `<var>.` (FieldSplitter).
            // The parser may not have produced a clean StructFieldReference
            // node for an incomplete expression, so LSPUtil's switch falls
            // through and returns nothing. Walk back: find the token
            // immediately before the dot, look it up in the active scope,
            // resolve its declared type, then surface the type's fields
            // directly. Returns early because the field list is exhaustive
            // for this position — no other completion category applies
            // after `.` in fbasic.
            if (leftToken.type == LexemType.FieldSplitter)
            {
                var fieldItems = TryGetStructFieldCompletionsAfterDot(
                    doc, programNode, entry.value.Item1, leftToken);
                if (fieldItems != null)
                    return fieldItems.Select(ToLspCompletionItem).ToList();
                // If we can't resolve the LHS we still bail out — letting
                // the switch see `FieldSplitter` would dump arbitrary
                // statement/expression suggestions into a position where
                // they don't belong.
                return new List<LspCompletionItem>();
            }

            var portable = LSPUtil.GetCompletions(context);

            // Command-argument rescue. When the leftToken is a fully-
            // resolved CommandWord but the cursor sits past its end
            // (typical case: trailing whitespace right after the command
            // name, e.g. `sprite |`), Visit() returns false on the
            // CommandStatement node — the cursor isn't strictly inside
            // [StartToken, EndToken] because EndToken's position is its
            // *start* char, not its end char. The switch above then sees
            // ProgramNode + a CommandWord leftToken, which matches none
            // of its cases, and returns empty.
            //
            // Three distinct cursor positions around a CommandWord need
            // different completion sets — and the AST switch above can't
            // tell them apart because Visit() uses StartToken/EndToken
            // (which are TOKEN positions, not span endpoints):
            //
            //   1. cursor INSIDE or AT END of the CommandWord (`sprit|`,
            //      `sprite|`): the user is still typing or has just
            //      finished typing a command name. Show the command list
            //      so they can refine / pick a different one. Variables
            //      are NOT relevant yet — they haven't moved to the arg
            //      slot.
            //
            //   2. cursor PAST CommandWord on the same line (`sprite |`):
            //      the user is in the first-arg slot. Show variables /
            //      symbols matching the first parameter's type. Variables
            //      should dominate; the command list adds noise here.
            //
            //   3. cursor on a different line entirely: leftToken happens
            //      to be a CommandWord just because it was the most-recent
            //      token, but it's no longer relevant. Don't fire anything.
            //
            // (1) is handled below as `cursorAtCommandEnd`; (2) as
            // `cursorPastCommandEnd`. The cursor-inside-the-word case
            // (`sprit|`) doesn't hit either branch because the lexer can't
            // produce a CommandWord for a partial name — leftToken there
            // is VariableGeneral, and Monaco's cached completion list
            // from when the user last typed a trigger character handles
            // filtering.
            var isOnCommandLine =
                leftToken.type == LexemType.CommandWord
                && leftToken.lineNumber == line;
            var cursorPastCommandEnd =
                isOnCommandLine && leftToken.EndCharNumber < character;
            var cursorAtCommandEnd =
                isOnCommandLine && leftToken.EndCharNumber == character;

            if (cursorPastCommandEnd)
            {
                // Case 2: arg-slot rescue. Surface first-parameter
                // symbols/functions/commands for the command word the
                // user just finished typing.
                var cmdName = leftToken.caseInsensitiveRaw
                              ?? leftToken.raw?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(cmdName))
                {
                    var found = false;
                    var cmd = default(CommandInfo);
                    foreach (var c in doc.Commands.Commands)
                    {
                        if (c.name != null
                            && c.name.Equals(cmdName, StringComparison.OrdinalIgnoreCase))
                        {
                            cmd = c;
                            found = true;
                            break;
                        }
                    }
                    if (found && cmd.args != null && cmd.args.Length > 0)
                    {
                        var paramItems = LSPUtil.GetCommandParameterCompletions(
                            cmd,
                            new List<int>(),
                            new List<IExpressionNode>(),
                            context);
                        var seen = new HashSet<string>(
                            portable.Select(p => p.Label ?? string.Empty));
                        foreach (var p in paramItems)
                        {
                            var label = p.Label ?? string.Empty;
                            if (seen.Contains(label)) continue;
                            seen.Add(label);
                            portable.Add(p);
                        }
                    }
                }
            }
            else if (cursorAtCommandEnd)
            {
                // Case 1: cursor at end of CommandWord. Surface every
                // command + function so Monaco can filter by the typed
                // word and the user sees `sprite` / `sprite height` /
                // etc. We bypass GetStatementCompletions because it
                // hardcodes TypeInfo.Void as the wanted return type and
                // therefore filters out every non-void-returning command
                // (`screen width` returns int, etc.) — exactly the items
                // a user mid-typing a command name needs to see. Using
                // TypeInfo.Unset opens the filter completely; type-
                // checking happens when the command is actually used.
                AddAllCommandsAndFunctions(portable, context);
            }

            // Multi-word command rescue. The lexer only collapses a token
            // span into a CommandWord when the FULL command name has been
            // typed (HandleCommandNames in Lexer.cs only rewrites at
            // isValidCommand=true leaves). Halfway through `set sprite
            // render target` the lexer sees `set` + `sprite` as two plain
            // identifiers, the parser routes the AST through Assignment or
            // a fresh expression node, and LSPUtil.GetCompletions's switch
            // ends up in a case that returns symbol/expression completions
            // — not the command list. Result: the user types `set sprite`
            // and the rest of the command name disappears from the
            // dropdown until they backspace.
            //
            // Sniff the line text from the cursor backwards for a
            // contiguous identifier+spaces run; if it spans more than one
            // word, treat it as a partial multi-word command prefix and
            // union in commands whose name starts with it. The Monaco /
            // VSCode side then sees these alongside whatever the AST
            // walk yielded.
            var prefix = ReadMultiWordCommandPrefix(doc, line, character);
            // When the leftToken is a complete CommandWord AND the cursor
            // is sitting in the trailing whitespace right after it (no
            // character of the next word typed yet), suppress the multi-
            // word continuation rescue. The command-arg rescue above has
            // already populated variable / parameter completions for the
            // first arg slot, and those are what the user wants to see
            // first. Monaco's fuzzy scorer would otherwise rank command
            // continuations (whose filterText starts with the typed
            // prefix) above the variables — score outranks sortText.
            // The user can type the first character of the next word to
            // bring continuations back.
            var suppressMultiWord =
                leftToken.type == LexemType.CommandWord
                && !string.IsNullOrEmpty(prefix)
                && prefix.EndsWith(" ");
            if (!string.IsNullOrEmpty(prefix) && !suppressMultiWord)
            {
                var seenLabels = new HashSet<string>(
                    portable.Select(p => p.Label ?? string.Empty));
                foreach (var cmd in doc.Commands.Commands)
                {
                    // Mirror GetCommandCallCompletions's usage filter so
                    // we don't surface runtime commands inside a `#`
                    // macro block (or vice versa).
                    if (isMacro && !cmd.usage.HasFlag(FadeBasicCommandUsage.Macro)) continue;
                    if (!isMacro && !cmd.usage.HasFlag(FadeBasicCommandUsage.Runtime)) continue;
                    if (cmd.name == null) continue;
                    if (cmd.name.Length <= prefix.Length) continue;
                    if (!cmd.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    // Skip if the same command already came back through
                    // the switch — avoids stacking duplicates when the
                    // AST happened to route correctly.
                    if (seenLabels.Contains(cmd.name)) continue;
                    var hasReturn = cmd.returnType != TypeCodes.VOID;
                    portable.Add(new PortableCompletionItem
                    {
                        InsertTextFormat = PortableInsertTextFormat.Snippet,
                        Kind = PortableCompletionKind.Interface,
                        Label = cmd.name,
                        // FilterText carries the typed prefix so Monaco's
                        // matcher scores these as exact-prefix hits even
                        // though the cursor's "current word" is just the
                        // last partial token. Without this, "set spr" →
                        // "set sprite render target" would score as a
                        // mid-substring match and rank below noise.
                        FilterText = cmd.name,
                        InsertText = cmd.name + (hasReturn ? "($0)" : ""),
                        // Sort just above the other command-call entries
                        // so the partial-prefix match floats to the top.
                        SortText = "b",
                        TriggerParameterHints = true,
                    });
                }
            }

            // Statement-leading-identifier safety net. When the user has
            // typed something like a single letter `s` at the start of a
            // line, the parser interprets it as the LHS of an unfinished
            // assignment. LSPUtil.GetCompletions routes that AST shape to
            // GetAssignmentCompletions, which early-returns empty because
            // LeftToken isn't `=`. None of the rescues above match either
            // (leftToken is VariableGeneral, not CommandWord; no spaces
            // in the prefix). Result: zero completions, even though the
            // user obviously wants to see `sprite`, `sin`, etc.
            //
            // If nothing else has populated `portable` and the leftToken
            // sits on the current line at the start of a statement-leading
            // position, fall back to the full command + function list so
            // Monaco can filter by what was typed. Same reasoning as the
            // cursorAtCommandEnd branch — using GetStatementCompletions
            // here would drop every non-void command.
            if (portable.Count == 0
                && leftToken.lineNumber == line
                && leftToken.EndCharNumber <= character)
            {
                AddAllCommandsAndFunctions(portable, context);
            }

            return portable.Select(ToLspCompletionItem).ToList();
        }

        // Shared helper for the cursorAtCommandEnd branch + the safety-
        // net fallback: load every command + function (filtered by the
        // doc's macro/runtime usage flags) regardless of return type,
        // dedup by label, and append to `portable`. Using TypeInfo.Unset
        // on both LSPUtil helpers disables the return-type filter that
        // GetStatementCompletions's hardcoded TypeInfo.Void imposes —
        // statement-level positions should still see int/string-returning
        // commands for filtering, even if calling them as a bare
        // statement would discard the return value.
        private static void AddAllCommandsAndFunctions(
            List<PortableCompletionItem> portable,
            LspCompletionContext context)
        {
            var seen = new HashSet<string>(
                portable.Select(p => p.Label ?? string.Empty));
            foreach (var pair in LSPUtil.GetCommandCallCompletions(TypeInfo.Unset, context))
            {
                var label = pair.item.Label ?? string.Empty;
                if (seen.Contains(label)) continue;
                seen.Add(label);
                portable.Add(pair.item);
            }
            foreach (var pair in LSPUtil.GetFunctionCallCompletions(TypeInfo.Unset, context.Scope))
            {
                var label = pair.item.Label ?? string.Empty;
                if (seen.Contains(label)) continue;
                seen.Add(label);
                portable.Add(pair.item);
            }
        }

        // Scan the line text backwards from the cursor for a contiguous
        // run of identifier characters and single spaces. Returns the
        // prefix only when it contains at least one space (multi-word) —
        // single-word prefixes are already handled by the existing switch
        // path. Returns null/empty when the prefix isn't a plausible
        // command-name fragment (e.g. starts with a digit, contains an
        // operator, or has no spaces).
        private static string ReadMultiWordCommandPrefix(FadeDocument doc, int line, int character)
        {
            if (doc?.Text == null) return string.Empty;
            // Carve out just the current line so we don't accidentally walk
            // over a newline boundary on documents with very long single
            // lines (split is fine — we only need a few chars at the end).
            var lines = doc.Text.Split('\n');
            if (line < 0 || line >= lines.Length) return string.Empty;
            var lineText = lines[line];
            var endCol = character < lineText.Length ? character : lineText.Length;
            if (endCol <= 0) return string.Empty;

            int start = endCol;
            while (start > 0)
            {
                var c = lineText[start - 1];
                // Allow identifier chars + single spaces. Stop on anything
                // else (operators, parens, punctuation) — that's a strong
                // signal we're not in a command-name context anymore.
                if (char.IsLetterOrDigit(c) || c == '_' || c == ' ')
                {
                    start--;
                    continue;
                }
                break;
            }
            // Trim only the LEFT side; trailing/internal spaces are part of
            // the user's typed prefix and matter for the StartsWith check.
            var prefix = lineText.Substring(start, endCol - start).TrimStart();
            if (prefix.Length == 0) return string.Empty;
            // The first character must be a letter (or underscore) —
            // commands always start with a letter; bailing here avoids
            // matching purely-numeric runs like " 12 34" at the cursor.
            var head = prefix[0];
            if (!char.IsLetter(head) && head != '_') return string.Empty;
            // Multi-word only — single-word fragments are already covered
            // by the normal switch path's GetCommandCallCompletions.
            return prefix.Contains(' ') ? prefix : string.Empty;
        }

        // After-dot rescue. Locate the token immediately preceding the
        // FieldSplitter (`.`) — that's the struct variable name. Look it
        // up in local then global scope, resolve its declared TypeInfo,
        // and return completion items for that struct's fields.
        // Returns null when:
        //   - There's no identifier token immediately before the dot
        //     (e.g. the user typed `.` after an operator like `5.`).
        //   - The identifier doesn't resolve to a known variable.
        //   - The variable's type isn't a struct (TypeInfo.structName
        //     is null/empty) or the type's field table is missing.
        // Caller treats null as "abort the whole completion" — after a
        // dot there's no useful fallback set.
        private static List<PortableCompletionItem> TryGetStructFieldCompletionsAfterDot(
            FadeDocument doc,
            ProgramNode programNode,
            SymbolTable localScope,
            Token dotToken)
        {
            // Walk allTokens backwards from the dot to find the previous
            // identifier-bearing token. Skip over whitespace tokens —
            // they're not in allTokens in practice, but the iteration is
            // O(N) anyway and a defensive skip lets future lexer tweaks
            // not subtly break this rescue.
            Token lhsToken = null;
            var tokens = doc.LexResults.allTokens;
            for (var i = tokens.Count - 1; i >= 0; i--)
            {
                var t = tokens[i];
                if (t == dotToken) continue;
                // We need a token strictly BEFORE the dot. The dot's
                // position is on the dot character; anything at the same
                // (line, char) wouldn't precede it.
                if (t.lineNumber > dotToken.lineNumber) continue;
                if (t.lineNumber == dotToken.lineNumber && t.charNumber >= dotToken.charNumber) continue;
                lhsToken = t;
                break;
            }
            if (lhsToken == null) return null;
            // Only identifier tokens can be the LHS of a struct access.
            // Bail on operators, literals, etc. — `5.` is a number, not
            // a member access.
            if (lhsToken.type != LexemType.VariableGeneral
                && lhsToken.type != LexemType.VariableReal
                && lhsToken.type != LexemType.VariableString)
            {
                return null;
            }

            var name = lhsToken.caseInsensitiveRaw
                       ?? lhsToken.raw?.ToLowerInvariant()
                       ?? string.Empty;
            if (string.IsNullOrEmpty(name)) return null;

            // Try local scope first (function/test locals + params),
            // then global. Mirrors how identifier resolution works
            // elsewhere — locals shadow globals.
            Symbol symbol = null;
            if (localScope != null && localScope.TryGetValue(name, out var localSym))
            {
                symbol = localSym;
            }
            else if (programNode.scope.globalVariables.TryGetValue(name, out var globalSym))
            {
                symbol = globalSym;
            }
            if (symbol == null) return null;

            // The scope-aware visitor populates Symbol.typeInfo.structName
            // for struct-typed variables declared via `local`/`global
            // <name> as <Type>`. Bail when the symbol isn't actually a
            // struct (primitive variables can't have a `.field` follow-up).
            var structName = symbol.typeInfo.structName;
            if (string.IsNullOrEmpty(structName)) return null;
            if (!programNode.scope.typeNameToTypeMembers.TryGetValue(structName, out var members))
                return null;

            var list = new List<PortableCompletionItem>(members.Count);
            foreach (var kvp in members)
            {
                var fieldName = kvp.Key;
                var fieldSymbol = kvp.Value;
                var trivia = fieldSymbol.source is IHasTriviaNode triviaNode ? triviaNode.Trivia : string.Empty;
                list.Add(new PortableCompletionItem
                {
                    InsertTextFormat = PortableInsertTextFormat.Snippet,
                    Kind = PortableCompletionKind.Field,
                    Label = fieldName,
                    InsertText = fieldName,
                    SortText = "a",
                    Detail = fieldSymbol.typeInfo.ToDisplay(),
                    Documentation = trivia ?? string.Empty,
                });
            }
            return list;
        }

        private static LspCompletionItem ToLspCompletionItem(PortableCompletionItem p)
        {
            return new LspCompletionItem
            {
                Label = p.Label,
                InsertText = p.InsertText,
                Kind = ToKind(p.Kind),
                Detail = p.Detail,
                Documentation = p.Documentation,
                SortText = p.SortText,
                FilterText = p.FilterText,
                InsertTextFormat = p.InsertTextFormat == PortableInsertTextFormat.Snippet
                    ? LspInsertTextFormat.Snippet
                    : LspInsertTextFormat.PlainText,
                TriggerParameterHints = p.TriggerParameterHints,
            };
        }

        private static LspCompletionKind ToKind(PortableCompletionKind kind)
        {
            switch (kind)
            {
                case PortableCompletionKind.Variable: return LspCompletionKind.Variable;
                case PortableCompletionKind.Function: return LspCompletionKind.Function;
                case PortableCompletionKind.Interface: return LspCompletionKind.Interface;
                case PortableCompletionKind.Keyword: return LspCompletionKind.Keyword;
                case PortableCompletionKind.Field: return LspCompletionKind.Field;
                case PortableCompletionKind.Class: return LspCompletionKind.Class;
                case PortableCompletionKind.Constant: return LspCompletionKind.Constant;
                case PortableCompletionKind.Reference: return LspCompletionKind.Reference;
                case PortableCompletionKind.Folder: return LspCompletionKind.Folder;
                default: return LspCompletionKind.Text;
            }
        }
    }
}
