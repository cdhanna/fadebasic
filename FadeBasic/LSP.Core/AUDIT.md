# LSP.Core vs Native LSP — Behavioral Audit

After the refactor, the native LSP project (`FadeBasic/LSP/`) is a thin
adapter over `FadeBasic.LSP.Core.Handlers.*`. Each native handler:

1. Looks up the requesting URI's `CodeUnit` via `CompilerService`.
2. Calls `CoreAdapter.ToDocument(unit, uri, projectDocs?)` to build a
   single `FadeDocument` view of the parsed AST.
3. Maps the request's position into the compiled unit's coordinate space
   via `unit.sourceMap.TryGetMappedLocation` (identity for single-file
   projects; non-trivial for multi-file ones).
4. Invokes Core's `Compute(...)`.
5. Translates Core's DTOs into OmniSharp protocol types, mapping ranges
   back through `unit.sourceMap.GetOriginalLocation` so multi-file
   projects resolve to the originating files.

Doc-aware handlers (Hover today, Completion if extended) install a
`ProjectDocsCommandDocsProvider` on the `FadeDocument` so Core's
`ICommandDocsProvider` hook gets the *same* parsed `ProjectDocs` the
native LSP already exposes.

## Per-handler diff vs the pre-refactor native code

| Handler | Pre-refactor native | Core | Diff |
|---|---|---|---|
| **Diagnostics** | Project-aware, multi-file | Per-document | (Unchanged — diagnostics aren't routed through Core yet; lives in `LSP/Handlers/DiagnosticsHandler.cs`.) |
| **SemanticTokens** | Project-aware, source-mapped | Per-document | (Unchanged — lives in `LSP/Handlers/SemanticTokenHandler.cs`.) |
| **Hover** | Walked AST; rich Markdown for commands via `ProjectDocs`; raw `function.Trivia` for function calls; nothing for variables/parameters/labels; no diagnostics on hover | Diagnostics first; rich Markdown for commands via `ICommandDocsProvider` (same `ProjectDocs` underneath); fenced `fade` code-block header + trivia for functions / variables / parameters / labels | ✅ More coverage. Function trivia now has a header (signature) before the doc text. Hovering an error region now shows the error. |
| **Completion** | Returned a `<NO MACRO PROG>` placeholder item when the macro program was absent | Returns an empty list in the same situation | ✅ No more debug-string leakage. Otherwise identical context-building + `LSPUtil.GetCompletions` call. |
| **SignatureHelp** | AST walk + token-fallback for `name(`; per-param documentation from `ProjectDocs` | Same AST walk + token-fallback; per-param docs come via `LspSignatureParameter.Documentation` (currently null because Core doesn't fill it from docs — handler post-fills if needed) | ⚠ Per-param documentation: Core doesn't fetch from docs yet. Native adapter passes `ProjectDocs` to Core but Core ignores it for sig help. **Follow-up: surface command param docs in Core's sig help.** |
| **GotoDefinition** | AST `FindFirst` on allowed types; walked program *and* macroProgram; mapped result range via sourceMap | Same AST `FindFirst`; walks `doc.Program` only | ⚠ Macro-expanded tokens won't resolve to definitions through Core. Multi-file source-map mapping handled at the native adapter layer. |
| **FindReferences** | Single-pass: matched node → DeclaredFromSymbol.source → walk program for DeclaredFromSymbol matches. Walked macroProgram too. | Multi-pass: matched node + DeclaredFromSymbol.source + nodes whose DeclaredFromSymbol.source's token equals the clicked token. **Walks only `doc.Program`.** | ✅ Clicking the declaration site (e.g. LHS of `x = 1`) now returns use sites — old native code returned only the LHS. ⚠ Macro-expanded refs not walked. |
| **DocumentSymbol** | Dumped every "interesting" lexer token as a separate symbol; re-read file from disk per request | AST-driven outline (functions with nested labels, types, top-level declarations, labels); reads in-memory parse tree | ✅ Massive UX improvement. Range now covers full bodies; SelectionRange covers the name token. No more per-keyword/per-string noise. |
| **FoldingRange** | Hardcoded stub (`[2,4]`) | AST-driven (functions, if/for/while/do/repeat blocks, type/test blocks, multi-line `rem` comments) | ✅ Massive UX improvement. |
| **Formatting** | Re-lexed source from disk per request; cased per `conf.language.fade.formatCasing` | Operates on the LexerResults the workspace already has; same casing setting plumbed through `LspFormattingOptions` | ✅ No FS roundtrip; output now strictly follows the LSP's view of the document. Casing behavior preserved. |
| **FormattingRange** | Filtered FormattingHandler result by intersection | (Unchanged composition; just delegates to the refactored FormattingHandler) | (None) |
| **FormattingWhenTyping** | Filtered FormattingHandler result by line-distance | (Unchanged composition; just delegates to the refactored FormattingHandler) | (None) |
| **Rename** | Walked program + macroProgram; emitted edits keyed on the request URI | Walks `doc.Program` only; ranges mapped back through `unit.sourceMap` to originating files | ⚠ Macro-expanded refs not walked. Multi-file projects: edits now correctly target originating source files instead of a single URI. |

## Open follow-ups

1. **Macro program walks.** GotoDef / References / Rename in Core don't yet
   inspect `LexerResults.macroProgram`. Native pre-refactor did. Adding a
   `doc.MacroProgram` field to `FadeDocument` and visiting both is a
   straightforward extension.
2. **Per-parameter docs in SignatureHelp.** Core's `LspSignatureParameter`
   has a `Documentation` field but `SignatureHelpHandler.BuildCommandSignature`
   doesn't yet pull from `ICommandDocsProvider`. The pre-refactor native
   filled this from `ProjectDocs.methodDocs.parameters[i].body`. Wire the
   same lookup in Core to close this gap.
3. **Completion item documentation.** Built-in command completions have no
   per-item documentation in either Core or the native pre-refactor.
   Hover compensates by showing rich docs on hover-over-completion.
   Surface command summaries on the `LspCompletionItem.Documentation`
   field if the suggest popup's doc panel should show more.
4. **Per-token Range output.** The Core Hover handler returns a range
   based on the matched token, not the matched AST node. Single-file
   parity; multi-file output is slightly tighter.
