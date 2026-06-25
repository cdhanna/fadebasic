// Explicit, decision-tree agent loop (a parallel alternative to Agent).
//
// Instead of one monolithic ReAct turn that emits prose + code + tool-calls as a
// single blob, this orchestrator runs a small decision tree of FOCUSED nodes,
// each a single model call emitting ONE artifact:
//   - classify  → intent enum
//   - research  → search_docs per need
//   - emit-code → Fade source
//   - prose     → explanation
//
// It emits the same AgentEvent shape as Agent so the existing chat UI renders it
// unchanged. Code correctness comes from a tight, command-specific context plus a
// fast post-emit verify (detectors + LSP), not decode-time constraint.

import type { ChatProvider, Msg, StreamOptions } from '../providers/types';
import type { AgentEvent, AgentListener } from '../agent';
import { Retriever, formatHits } from '../rag/retrieval';
import type { SearchHit } from '../rag/types';
import type { ToolRegistry, ToolContext } from '../tools';
import { FADE_RULES } from '../fade-rules';
import { MONOGAME_RULES } from '../monogame-rules';
import { detectFadeAntiPatterns } from '../fade-antipatterns';
import { detectUnknownCommands, detectCommandAsVariable, detectMissingValueCallParens, detectAssetExtension, detectAssignToCommandCall } from '../fade-command-check';

export type AgentIntent = 'write_code' | 'edit_code' | 'debug' | 'explain' | 'chat';

/** A command's authoritative docs, as surfaced to the coder node. `markdown`
 *  is the LSP-rendered hover doc (signature header + summary + params). */
export interface CommandDocLite { name: string; signature: string; markdown: string; }

const INTENTS: AgentIntent[] = ['write_code', 'edit_code', 'debug', 'explain', 'chat'];

export interface GrammarAgentOptions {
    provider: ChatProvider;
    retriever?: Retriever | null;
    getCommandNames?: () => Promise<string[]>;
    /** Names of commands that return a value (call-with-parens check). */
    getValueReturningCommands?: () => Promise<string[]>;
    /** Full per-command docs (name + raw sig + markdown). Lets the coder node
     *  inject EXACT signatures/params for the commands a program will use. */
    getCommandDocs?: () => Promise<CommandDocLite[]>;
    getProjectType?: () => string | undefined;
    /** Base system persona; rules/catalog are appended per node as needed. */
    systemPrompt?: string;
    autoRetrievalK?: number;
    /** Tool registry + context for the edit/debug branches (read_file,
     *  get_diagnostics, apply_edit). When absent, those branches degrade to
     *  showing the code without applying it. */
    tools?: ToolRegistry | null;
    toolContext?: ToolContext | null;
    /** Ask the user to approve importing a catalog asset before it's
     *  downloaded into the project. Resolves true to import, false to skip.
     *  When absent, the catalog node discovers + suggests but never imports. */
    confirmCatalogImport?: (entry: CatalogCandidate) => Promise<boolean>;
}

/** A catalog match surfaced for possible import (trimmed projection). */
export interface CatalogCandidate {
    id: number;
    name: string;
    kind: string;
    mime: string;
    tags: string[];
    bytes: number;
    license: string;
}

export class GrammarAgent {
    private readonly provider: ChatProvider;
    private readonly retriever: Retriever | null;
    private readonly getCommandNames: (() => Promise<string[]>) | null;
    private readonly getValueReturningCommands: (() => Promise<string[]>) | null;
    private readonly getCommandDocs: (() => Promise<CommandDocLite[]>) | null;
    private readonly getProjectType: (() => string | undefined) | null;
    private readonly systemPrompt: string;
    private readonly tools: ToolRegistry | null;
    private readonly toolContext: ToolContext | null;
    private readonly confirmCatalogImport: ((entry: CatalogCandidate) => Promise<boolean>) | null;

    private listeners = new Set<AgentListener>();
    private toolSeq = 0;
    private history: Msg[] = [];
    private abortController: AbortController | null = null;
    private cachedCommands: string[] | null = null;
    private cachedValueCommands: string[] | null = null;
    private cachedCommandDocs: CommandDocLite[] | null = null;
    /** Set by emitCodeRaw when the model hit the output-token ceiling (code cut
     *  off mid-program). verifyAndFix checks it to avoid looping on a truncation. */
    private lastEmitTruncated = false;

    constructor(opts: GrammarAgentOptions) {
        this.provider = opts.provider;
        this.retriever = opts.retriever ?? null;
        this.getCommandNames = opts.getCommandNames ?? null;
        this.getValueReturningCommands = opts.getValueReturningCommands ?? null;
        this.getCommandDocs = opts.getCommandDocs ?? null;
        this.getProjectType = opts.getProjectType ?? null;
        this.systemPrompt = opts.systemPrompt ?? 'You are a helpful Fade BASIC coding assistant.';
        this.tools = opts.tools ?? null;
        this.toolContext = opts.toolContext ?? null;
        this.confirmCatalogImport = opts.confirmCatalogImport ?? null;
    }

    // ── Agent-compatible surface (so ai-chat can switch to this) ─────────────
    on(listener: AgentListener): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }
    private emit(ev: AgentEvent): void {
        for (const l of this.listeners) { try { l(ev); } catch { /* ignore */ } }
    }
    abort(): void { this.abortController?.abort(); }
    getAbortSignal(): AbortSignal | undefined { return this.abortController?.signal; }
    getHistory(): readonly Msg[] { return this.history; }
    setHistory(msgs: Msg[]): void { this.history = msgs.slice(); }
    clearHistory(): void { this.history = []; }

    private async commandNames(): Promise<string[]> {
        if (!this.getCommandNames) return [];
        if (!this.cachedCommands) {
            try { this.cachedCommands = await this.getCommandNames(); }
            catch { this.cachedCommands = []; }
        }
        return this.cachedCommands ?? [];
    }

    private async valueCommands(): Promise<string[]> {
        if (!this.getValueReturningCommands) return [];
        if (!this.cachedValueCommands) {
            try { this.cachedValueCommands = await this.getValueReturningCommands(); }
            catch { this.cachedValueCommands = []; }
        }
        return this.cachedValueCommands ?? [];
    }

    private async commandDocs(): Promise<CommandDocLite[]> {
        if (!this.getCommandDocs) return [];
        if (!this.cachedCommandDocs) {
            try { this.cachedCommandDocs = await this.getCommandDocs(); }
            catch { this.cachedCommandDocs = []; }
        }
        return this.cachedCommandDocs ?? [];
    }

    // ── Conversation memory ───────────────────────────────────────────────────
    // The nodes are otherwise single-shot (system + current user msg). These let
    // a follow-up turn see what came before — without this the loop is stateless
    // and a request like "make it faster / add friction" has nothing to modify.

    /** The most recent ```fade code block the assistant emitted this
     *  conversation, if any — the base a follow-up edit builds on. Excludes the
     *  current (just-pushed) user turn. */
    private lastEmittedCode(): string | null {
        for (let i = this.history.length - 1; i >= 0; i--) {
            if (this.history[i].role !== 'assistant') continue;
            const code = extractLastFenced(this.history[i].content);
            if (code) return code;
        }
        return null;
    }

    /** Compact recent conversation for context — the last `maxMsgs` messages
     *  (before the current one) with code blocks elided (the base code is
     *  supplied separately, so we don't duplicate it). */
    private historyContext(maxMsgs = 4): string {
        const prior = this.history.slice(0, -1);            // drop the current user turn
        const msgs = prior.slice(-maxMsgs);
        if (msgs.length === 0) return '';
        return msgs.map(m => {
            const role = m.role === 'user' ? 'User' : 'Assistant';
            const c = m.content.replace(/```[\s\S]*?```/g, '[code]').trim().slice(0, 400);
            return `${role}: ${c}`;
        }).join('\n');
    }

    /** Is a dedicated data-model design step worth a model call for this
     *  request? Simple single-actor demos skip it (latency on slow local
     *  models); requests that imply MANY things or structured entities run it.
     *  The array/UDT RULES stay in the prompt regardless, so the coder can still
     *  reach for them — this only gates the extra planning call. */
    private needsDesign(text: string): boolean {
        return DESIGN_CUES.test(text);
    }

    /** Does this follow-up ask to MODIFY the previous program (vs. start a new
     *  one)? A fresh "write me a <thing>" is NOT a modification even after prior
     *  code; "make it faster / add friction / bounce" is. */
    private isModification(text: string): boolean {
        const t = text.toLowerCase();
        if (/\b(write|create|build|generate)\s+(me\s+)?(a|an|the)\b/.test(t)) return false;
        return /\b(it|this|that|them|they|faster|slower|bigger|smaller|also|now|instead|add|remove|change|more|less|bounce|velocity|friction|drift|gravity|speed|fix|but|still|again|update|tweak|adjust|too)\b/.test(t);
    }

    /** Collect a node's streamed text, optionally surfacing it to the UI as
     *  prose (text_delta) and/or throughput (model_token). */
    private async runNode(opts: {
        system: string;
        user: string;
        temperature?: number;
        maxTokens?: number;
        signal: AbortSignal;
        streamToBubble?: boolean;
    }): Promise<string> {
        const streamOpts: StreamOptions = {
            messages: [
                { role: 'system', content: opts.system },
                { role: 'user', content: opts.user },
            ],
            temperature: opts.temperature,
            maxTokens: opts.maxTokens,
            signal: opts.signal,
        };
        let text = '';
        for await (const ev of this.provider.stream(streamOpts)) {
            if (ev.kind === 'text') {
                text += ev.delta;
                if (opts.streamToBubble) {
                    this.emit({ kind: 'model_token', delta: ev.delta });
                    this.emit({ kind: 'text_delta', delta: ev.delta });
                }
            } else if (ev.kind === 'done') {
                break;
            }
        }
        return text;
    }

    // ── Nodes ────────────────────────────────────────────────────────────────

    /** Decision node: classify the request into an intent. Enum grammar +
     *  greedy on GhostBot; tolerant parse + heuristic fallback elsewhere. */
    private async classifyIntent(userText: string, signal: AbortSignal): Promise<AgentIntent> {
        const heuristic: AgentIntent =
            /\b(error|crash|broken|bug|fix|not work|doesn'?t work)\b/i.test(userText) ? 'debug'
            : /\b(change|edit|update|modify|refactor|rename|add to|remove from)\b/i.test(userText) ? 'edit_code'
            : /\b(write|make|create|build|demo|example|game|sprite|program|snippet|generate|show me code)\b/i.test(userText) ? 'write_code'
            : /\b(what|why|how|explain|does|can i|is there)\b/i.test(userText) ? 'explain'
            : 'chat';

        if (signal.aborted) return heuristic;
        const hasPriorCode = this.lastEmittedCode() !== null;
        const sys =
            'Classify the user request as exactly ONE of: '
            + INTENTS.join(', ') + '. Reply with ONLY that one word, nothing else.'
            + (hasPriorCode
                ? ' NOTE: there is existing code from earlier in this conversation. A request to change/improve/extend it (e.g. "make it faster", "add X", "now make it bounce") is edit_code, NOT write_code.'
                : '');
        let out = '';
        try {
            out = await this.runNode({
                system: sys, user: userText,
                temperature: 0, maxTokens: 8, signal,
            });
        } catch { return heuristic; }
        const found = INTENTS.find(i => out.toLowerCase().includes(i));
        return found ?? heuristic;
    }

    /** Research node: name the capabilities the code needs, then pull docs for
     *  each (visible). Deterministic retrieval — no grammar. */
    private async research(userText: string, signal: AbortSignal): Promise<SearchHit[]> {
        if (!this.retriever || signal.aborted) return [];
        let capText = '';
        try {
            capText = await this.runNode({
                system: 'List the distinct capabilities this Fade program needs for the SIMPLEST version '
                    + 'of the request, each a short search phrase (3-6 words), one per line, max 4. Do not '
                    + 'invent extra features (animation, effects) the user did not ask for. No code, no commentary.',
                user: userText, temperature: 0, maxTokens: 160, signal,
            });
        } catch { return []; }
        const caps = capText.split('\n')
            .map(l => l.replace(/^[-*•\d.)\s]+/, '').trim())
            .filter(l => l.length >= 3 && l.length <= 60 && !l.startsWith('```'))
            .slice(0, 4);
        if (caps.length === 0) return [];

        this.emit({ kind: 'plan_emitted', plan: {
            goal: 'Research the commands needed, then write the code',
            steps: caps.map(c => ({ tool: 'search_docs', description: c })),
        } });

        const hits: SearchHit[] = [];
        const seen = new Set<string>();
        for (const cap of caps) {
            if (signal.aborted) break;
            try {
                const found = await this.retriever.search(cap, 3, { projectType: this.getProjectType?.() });
                for (const h of found) {
                    const key = `${h.chunk.source}#${h.chunk.heading}`;
                    if (!seen.has(key)) { seen.add(key); hits.push(h); }
                }
            } catch { /* one failed lookup is fine */ }
        }
        return hits;
    }

    /** Curation node: read the retrieved doc sections, judge them against the
     *  user's request, KEEP only the ones relevant to the SIMPLEST solution, and
     *  fetch MORE if something essential is missing. Fixes the "asked for a simple
     *  sprite demo, RAG returned animated-sprite docs, model over-built" failure
     *  by filtering for relevance + simplicity before code generation. */
    private async curateDocs(userText: string, hits: SearchHit[], signal: AbortSignal): Promise<SearchHit[]> {
        if (hits.length <= 1 || signal.aborted) return hits;
        const list = hits.map((h, i) =>
            `${i + 1}. ${h.chunk.heading} — ${h.chunk.text.slice(0, 110).replace(/\s+/g, ' ').trim()}`).join('\n');
        let out = '';
        try {
            out = await this.runNode({
                system: 'You are selecting documentation for a coding task. Keep ONLY the sections directly '
                    + 'relevant to the SIMPLEST solution of the request — ignore advanced or tangential topics '
                    + '(e.g. animation when only a static sprite was asked for). Reply with "KEEP: <numbers>" '
                    + '(comma-separated). If essential info is missing, add a line "MORE: <short search query>". '
                    + 'Nothing else.',
                user: `Request: ${userText}\n\nSections:\n${list}`,
                temperature: 0, maxTokens: 120, signal,
            });
        } catch { return hits.slice(0, 3); }

        const keepIdx = (/KEEP:\s*([0-9,\s]+)/i.exec(out)?.[1] ?? '')
            .split(/[,\s]+/).map(n => parseInt(n, 10) - 1).filter(i => i >= 0 && i < hits.length);
        const seen = new Set<string>();
        const selected: SearchHit[] = [];
        const add = (h: SearchHit) => {
            const k = `${h.chunk.source}#${h.chunk.heading}`;
            if (!seen.has(k)) { seen.add(k); selected.push(h); }
        };
        (keepIdx.length ? keepIdx.map(i => hits[i]) : hits.slice(0, 3)).forEach(add);

        const more = /MORE:\s*(.+)/i.exec(out)?.[1]?.trim();
        if (more && this.retriever && !signal.aborted) {
            try {
                for (const h of await this.retriever.search(more, 3, { projectType: this.getProjectType?.() })) add(h);
            } catch { /* ignore */ }
        }
        this.emit({
            kind: 'reasoning',
            title: `Kept ${selected.length} most-relevant doc section(s)` + (more ? ` + looked up "${more.slice(0, 40)}"` : ''),
        });
        return selected;
    }

    /** Asset-resolution loop (monogame). Given the code intent, work out what
     *  asset the program should use so the generated code NEVER references a
     *  file that isn't in the project:
     *    1. list the project's existing asset files → if any, use them;
     *    2. else search the Catalog (single assets only — packs can't be
     *       imported programmatically) and, IF the user confirms, import one;
     *    3. re-run the check (the imported file is now "available");
     *    4. if still nothing, fall back to the built-in 1×1 pixel texture (id 0)
     *       + a size-sprite instruction.
     *  Returns the asset context note for the coder. '' when assets aren't
     *  relevant (request has no asset noun, or not a monogame project). */
    private async resolveAssets(userText: string, signal: AbortSignal): Promise<string> {
        if (signal.aborted) return '';
        // Asset loaders + the pixel fallback are monogame-runtime concepts.
        if (this.getProjectType?.() !== 'monogame') return '';
        if (!ASSET_REQUEST.test(userText)) return '';

        // What KIND of asset does this request need? A sprite needs an IMAGE, a
        // font needs a FONT, a sound needs AUDIO — so we only consider files /
        // catalog entries of the matching category (the bug: a font got pulled
        // for a sprite request).
        const category = neededAssetCategory(userText);
        const extRe = EXT_BY_CATEGORY[category];

        let importedPaths: string[] = [];
        let suggestedPacks: string[] = [];
        for (let attempt = 0; attempt < 2; attempt++) {
            let files: string[] = [];
            try { files = (await this.toolContext?.workspace.list()) ?? []; } catch { /* no workspace */ }
            const present = [...new Set([...files.filter(f => extRe.test(f)), ...importedPaths])];
            if (present.length > 0) {
                const bare = [...new Set(present.map(bareAssetName))];
                this.emit({
                    kind: 'reasoning',
                    title: `Using ${bare.length} ${category} asset(s) already in the project`,
                    detail: bare.join(', '),
                });
                return `${CATEGORY_LABEL[category]} asset(s) available in this project — load these and `
                    + `reference them by their BARE name (no extension, no folder): `
                    + `${bare.map(n => `"${n}"`).join(', ')}. Do NOT reference any other asset file; only these exist.`;
            }
            // First pass with nothing present → try to pull one of the right
            // category from the catalog, then loop so it's "present".
            if (attempt === 0) {
                const res = await this.tryCatalogImport(userText, category, signal);
                importedPaths = res.paths;
                suggestedPacks = res.packs;
                if (importedPaths.length > 0) continue;
            }
            break;
        }

        // Nothing importable — but the catalog may have relevant PACKS (which
        // can't be auto-imported). Surface them so the catalog isn't a dead end.
        if (suggestedPacks.length > 0) {
            this.emit({
                kind: 'reasoning',
                title: `Catalog has ${suggestedPacks.length} ${category} pack(s) — import from the Catalog tab to use real art`,
                detail: suggestedPacks.join(', '),
            });
        }

        if (category === 'image') {
            this.emit({ kind: 'reasoning', title: 'No image asset available — using the built-in pixel texture (id 0)' });
            return PIXEL_FALLBACK_NOTE
                + (suggestedPacks.length
                    ? ` (FYI: the Catalog has packs the user could import for real art: ${suggestedPacks.join(', ')} — but do NOT reference them in code; they aren't imported.)`
                    : '');
        }
        // Audio/font have no built-in fallback — just forbid referencing a
        // missing file so the program still runs.
        this.emit({ kind: 'reasoning', title: `No ${category} asset available` });
        return `There is NO ${category} asset in this project and none was imported. Do NOT reference any `
            + `${category} file (it does not exist and would break the program). Write the demo WITHOUT a `
            + `${category} asset.`;
    }

    /** Catalog discovery for the needed CATEGORY. Returns:
     *   - `paths`: workspace path(s) of a SINGLE asset imported on confirm
     *     (empty when none matched / declined / no confirm hook);
     *   - `packs`: names of relevant PACKS found. Packs hold most of the real
     *     art but CAN'T be imported programmatically (the id-6 error) — they're
     *     surfaced as a suggestion so the user can import via the Catalog tab.
     *  Two searches: a category-scoped one for importable single assets, and a
     *  broad one (no kind/category filter, since packs are zips) for packs. */
    private async tryCatalogImport(userText: string, category: AssetCategory, signal: AbortSignal):
        Promise<{ paths: string[]; packs: string[] }> {
        if (!this.tools || !this.toolContext?.catalog || signal.aborted) return { paths: [], packs: [] };

        // 1. Importable single assets of the right category.
        const assetRes = await this.runTool('search_catalog',
            { query: userText, kind: 'asset', category }, signal);
        const assets = extractCatalogMatches(assetRes?.result)
            .filter(m => m.kind !== 'pack' && mimeMatchesCategory(m.mime, category));

        // 2. Relevant packs (broad search — packs are application/zip, so the
        //    category mime filter would wrongly exclude them).
        const packRes = await this.runTool('search_catalog', { query: userText }, signal);
        const packs = extractCatalogMatches(packRes?.result)
            .filter(m => m.kind === 'pack' && packMatchesCategory(m, category));
        const packNames = [...new Set(packs.map(p => p.name))].slice(0, 5);

        if (assets.length === 0) {
            this.emit({
                kind: 'reasoning',
                title: packNames.length
                    ? `No importable single ${category} asset (real art is in packs)`
                    : `No importable catalog ${category} asset matched`,
            });
            return { paths: [], packs: packNames };
        }

        this.emit({
            kind: 'reasoning',
            title: `Found ${assets.length} importable ${category} asset(s)`,
            detail: assets.slice(0, 6).map(m => `#${m.id} ${m.name} (${m.mime})`).join('\n'),
        });

        const best = assets[0];
        if (!this.confirmCatalogImport) return { paths: [], packs: packNames };  // no approval → don't write

        let approved = false;
        try { approved = await this.confirmCatalogImport(best); } catch { approved = false; }
        if (!approved) {
            this.emit({ kind: 'reasoning', title: `Skipped importing "${best.name}" (not confirmed)` });
            return { paths: [], packs: packNames };
        }

        const imported = await this.runTool('import_catalog_asset', { id: best.id }, signal);
        const paths = extractImportedPaths(imported?.result);
        this.emit({
            kind: 'reasoning',
            title: paths.length
                ? `Imported "${best.name}" → ${paths.join(', ')}`
                : `Import of "${best.name}" returned no path`,
        });
        return { paths, packs: packNames };
    }

    /** Command-resolution node: decide WHICH commands the program will use and
     *  inject their EXACT signatures + parameter docs. The bare name catalog
     *  tells the model what exists but not how to CALL it — this closes that
     *  gap (the #1 source of bad codegen: guessed arg counts / missing parens).
     *
     *  One cheap model call names the minimal command set; we then validate each
     *  name against the authoritative doc list (dropping hallucinations) and
     *  emit a trimmed markdown reference for the survivors. Returns '' when no
     *  command-doc source is wired (tests) or nothing resolves. */
    private async resolveCommandDocs(userText: string, signal: AbortSignal): Promise<string> {
        const docs = await this.commandDocs();
        if (docs.length === 0 || signal.aborted) return '';
        const byName = new Map(docs.map(d => [d.name.toLowerCase(), d]));
        const names = docs.map(d => d.name);

        let out = '';
        try {
            out = await this.runNode({
                system: 'You are about to write a Fade BASIC program. From the command list, name ONLY the '
                    + 'commands you will actually use — the minimal set for the SIMPLEST solution. One exact '
                    + 'command name per line (copy them verbatim from the list), at most 12, nothing else. '
                    + 'No code, no commentary.',
                user: `Request: ${userText}\n\nCommands: ${names.join(', ')}`,
                temperature: 0, maxTokens: 200, signal,
            });
        } catch { return ''; }

        const picked: CommandDocLite[] = [];
        const seen = new Set<string>();
        for (const rawLine of out.split('\n')) {
            const t = rawLine.replace(/^[-*•\d.)\s]+/, '').replace(/[`,]/g, '').trim().toLowerCase();
            if (!t) continue;
            const d = byName.get(t);
            if (d && !seen.has(t)) { seen.add(t); picked.push(d); }
        }
        if (picked.length === 0) return '';
        this.emit({
            kind: 'reasoning',
            title: `Looked up exact signatures for ${picked.length} command(s)`,
            detail: picked.map(p => p.name).join(', '),
            // Render each command as a clickable chip → opens its help-doc entry.
            links: picked.map(p => ({ label: p.name, symbol: p.name })),
        });
        // Full per-command docs, but bound the TOTAL so a big resolved set
        // doesn't blow the local model's context / latency. Stop adding docs
        // once we pass the budget (the first, most-relevant commands win).
        const TOTAL_BUDGET = 8000;
        const blocks: string[] = [];
        let used = 0;
        for (const d of picked) {
            const block = formatCommandDoc(d);
            if (used > 0 && used + block.length > TOTAL_BUDGET) break;
            blocks.push(block);
            used += block.length + 2;
        }
        return blocks.join('\n\n');
    }

    /** Design node — decide the DATA MODEL / which language features fit the
     *  task BEFORE writing code (the "do I need an array or a UDT here?" step).
     *  Nudges the model to reach for arrays (many similar things), UDTs (one
     *  entity with several fields), and functions (repeated logic) instead of
     *  defaulting to a pile of loose variables — while still biasing to the
     *  simplest design that fits. Returns a short plan to inject into the coder,
     *  or '' on failure/abort. */
    private async planDataModel(userText: string, signal: AbortSignal, baseCode?: string): Promise<string> {
        if (signal.aborted) return '';
        let out = '';
        try {
            out = await this.runNode({
                system: 'You are choosing the DATA MODEL for a small Fade BASIC program before it is written. '
                    + 'In 1-3 SHORT lines, decide which language features fit — and explicitly answer "array or UDT?":\n'
                    + '- Many SIMILAR things (enemies, bullets, particles, tiles, balls)? → an ARRAY: `dim things(n)`, processed with a `FOR` loop.\n'
                    + '- One entity with SEVERAL attributes (x, y, velocity, health)? → a USER-DEFINED TYPE: `type Name … endtype` (often an array of it for many).\n'
                    + '- Repeated logic? → a FUNCTION.\n'
                    + 'Bias to the SIMPLEST design that fits: if a few plain variables are enough, say exactly that '
                    + '("plain variables; no array or UDT needed"). Do NOT over-engineer, and do NOT write code — output ONLY the brief plan.',
                user: baseCode
                    ? `Request (modifying existing code): ${userText}\n\nExisting code:\n${baseCode.slice(0, 1500)}`
                    : `Request: ${userText}`,
                temperature: 0.2, maxTokens: 200, signal,
            });
        } catch { return ''; }
        out = out.trim();
        if (!out) return '';
        this.emit({ kind: 'reasoning', title: 'Chose a data model (array / UDT / functions?)', detail: out.slice(0, 400) });
        return 'PLANNED APPROACH — follow this data model (use the structures named here):\n' + out;
    }

    /** Build the resident rules block (FADE_RULES + monogame + command catalog). */
    private async rulesBlock(): Promise<string> {
        const parts = [FADE_RULES];
        if (this.getProjectType?.() === 'monogame') parts.push(MONOGAME_RULES);
        const names = await this.commandNames();
        if (names.length > 0) {
            parts.push(
                `These are ALL ${names.length} commands that exist in this project (authoritative): `
                + names.join(', ') + '. Use ONLY these.',
            );
        }
        return parts.join('\n\n');
    }

    /** Code-emit node: stream PURE Fade under the `language` grammar (no prose,
     *  no fences) given a fully-built system prompt + user instruction. Surfaced
     *  to the bubble wrapped in a ```fade fence. */
    private async emitCodeRaw(system: string, user: string, signal: AbortSignal, maxTokens = 4096): Promise<string> {
        this.emit({ kind: 'text_delta', delta: '```fade\n' });
        let raw = '';
        let finishReason: string | undefined;
        // We inject our OWN ```fade fence around the stream. If the model also
        // emits a fence (despite being told not to), the two collide → a broken
        // block + a stray empty block in the bubble. So stream line-buffered and
        // DROP any fence line the model produces; only our injected pair renders.
        let pending = '';
        const pump = (final: boolean) => {
            let nl: number;
            while ((nl = pending.indexOf('\n')) >= 0) {
                const line = pending.slice(0, nl + 1);
                pending = pending.slice(nl + 1);
                if (!/^\s*```/.test(line)) this.emit({ kind: 'text_delta', delta: line });
            }
            if (final && pending.length) {
                if (!/^\s*```/.test(pending)) this.emit({ kind: 'text_delta', delta: pending });
                pending = '';
            }
        };
        for await (const ev of this.provider.stream({
            messages: [{ role: 'system', content: system }, { role: 'user', content: user }],
            // Correctness comes from the focused context + verifyAndFix below.
            temperature: 0.3,
            maxTokens,
            signal,
        })) {
            if (ev.kind === 'text') {
                raw += ev.delta;
                this.emit({ kind: 'model_token', delta: ev.delta });
                pending += ev.delta;
                pump(false);
            } else if (ev.kind === 'done') {
                finishReason = ev.finishReason;
                break;
            }
        }
        pump(true);
        // If the model hit the token ceiling the code is CUT OFF mid-program —
        // the reviewer would then (correctly) flag the broken tail, and every
        // re-emit truncates again → an unwinnable loop. Flag it so verifyAndFix
        // surfaces it instead of spinning.
        this.lastEmitTruncated = finishReason === 'length';
        // Strip any fence from the RETURNED code too (history / verify).
        const code = stripFences(raw);
        this.emit({ kind: 'text_delta', delta: '\n```\n' });
        return code;
    }

    /** write_code: emit fresh code for the request. `docs` = RAG doc sections;
     *  `cmdDocs` = exact signatures/params for the commands the program will use. */
    private async emitCode(userText: string, docs: string, cmdDocs: string, signal: AbortSignal): Promise<string> {
        const sysParts = [
            'You write Fade BASIC. Output ONLY valid Fade source code for the request — '
            + 'no prose, no explanation, no markdown fences. Write the SIMPLEST program that '
            + 'satisfies the request: do NOT add features, animation, extra input handling, or '
            + 'effects the user did not explicitly ask for. Prefer fewer lines and basic commands.',
            await this.rulesBlock(),
        ];
        if (cmdDocs) {
            sysParts.push(
                'EXACT command reference for the commands you will use — call each EXACTLY as its '
                + 'signature shows (right number/type of arguments; value-returning commands need '
                + 'parentheses). Do NOT invent arguments:\n\n' + cmdDocs,
            );
        }
        // `docs` carries the planned data model + asset note + RAG sections.
        if (docs) sysParts.push('Context for writing the code:\n' + docs);
        return this.emitCodeRaw(sysParts.join('\n\n'), userText, signal);
    }

    /** Post-emit correctness check (the backstop that replaces decode-time
     *  grammar). Uses ONLY accurate signals:
     *   - deterministic structural/vocab detectors that need no signature info
     *     (cross-language syntax like `wend`/`end function`, invented commands,
     *     command-used-as-a-variable);
     *   - the LSP, which is AUTHORITATIVE about real command signatures/return
     *     types — so e.g. wrong args to `texture` are caught correctly. We do NOT
     *     guess "returns a value" from a name alone (that misfired on `texture`).
     */
    private async verifyCode(code: string): Promise<string[]> {
        const names = await this.commandNames();
        const valueCmds = await this.valueCommands();
        const notes = new Set<string>([
            ...detectFadeAntiPatterns(code),
            ...detectUnknownCommands(code, names),
            ...detectCommandAsVariable(code, names),
            // Assigning to a command / its result — `sprite x(1) = 100`.
            ...detectAssignToCommandCall(code, names),
            // Value-returning commands used without parens (e.g. `mouse x` →
            // `mouse x()`). Accurate (uses real return types) + multi-word aware.
            ...detectMissingValueCallParens(code, valueCmds),
        ]);
        // MonoGame asset loaders take a content path with NO file extension
        // (`texture 1, "ship"`, not `"ship.png"`). Only relevant for monogame.
        if (this.getProjectType?.() === 'monogame') {
            for (const n of detectAssetExtension(code)) notes.add(n);
        }
        if (this.toolContext?.lintFadeSnippet) {
            try {
                for (const d of await this.toolContext.lintFadeSnippet(code)) {
                    if (d.severity === 'error') notes.add(`Line ${d.line}: ${d.message}`);
                }
            } catch { /* LSP unavailable → detectors only */ }
        }
        return [...notes];
    }

    /** Verify the emitted code and, while issues remain, re-emit with the exact
     *  problems fed back — RE-VERIFYING each fix (a bounded self-heal loop, not a
     *  single shot). A small model often re-introduces the same mistake on the
     *  first rewrite, so we keep going up to MAX_FIX_PASSES and stop early once
     *  clean. Streams each fix into the bubble (clearing the previous attempt). */
    private async verifyAndFix(userText: string, docs: string, cmdDocs: string, code: string, signal: AbortSignal, maxPasses = 3): Promise<string> {
        const MAX_FIX_PASSES = maxPasses;
        let current = code;
        for (let pass = 1; pass <= MAX_FIX_PASSES; pass++) {
            if (signal.aborted) return current;
            // If the emit was cut off at the token ceiling, the code is
            // incomplete — re-emitting just truncates again. Surface it and stop
            // rather than spinning the loop on the broken tail.
            if (this.lastEmitTruncated) {
                this.emit({
                    kind: 'reasoning',
                    title: 'Output was cut off at the token limit — the program may be incomplete',
                    detail: 'Try asking for a smaller change, or split it into steps.',
                });
                return current;
            }
            const issues = await this.verifyCode(current);
            if (issues.length === 0) {
                if (pass > 1) this.emit({ kind: 'reasoning', title: 'Code review passed ✓' });
                return current;
            }
            this.emit({
                kind: 'reasoning',
                title: `Reviewing the code — found ${issues.length} issue(s), fixing (pass ${pass}/${MAX_FIX_PASSES})`,
                detail: issues.join('\n'),
            });
            this.emit({ kind: 'revising' });  // clear the bubble for the rewrite
            const sysParts = [
                'You write Fade BASIC. Output ONLY the corrected Fade source — no prose, no fences.',
                await this.rulesBlock(),
            ];
            if (cmdDocs) sysParts.push('EXACT command reference (call each exactly as shown):\n\n' + cmdDocs);
            if (docs) sysParts.push('Context for writing the code:\n' + docs);
            sysParts.push(
                'Your previous code had these problems — fix ALL of them and do NOT re-introduce them:\n'
                + issues.map(i => `- ${i}`).join('\n')
                + '\n\nPrevious code:\n' + current,
            );
            current = await this.emitCodeRaw(sysParts.join('\n\n'), userText, signal);
        }
        // Still not clean after the budget — surface that honestly.
        const remaining = await this.verifyCode(current);
        if (remaining.length > 0) {
            this.emit({
                kind: 'reasoning',
                title: `Still ${remaining.length} issue(s) after ${MAX_FIX_PASSES} fix passes`,
                detail: remaining.join('\n'),
            });
        }
        return current;
    }

    /** Run a tool with full visibility (tool_call_start/result events), used by
     *  the edit/debug branches. Returns the ToolResult (or null if no registry). */
    private async runTool(name: string, args: Record<string, unknown>, signal: AbortSignal):
        Promise<{ ok: boolean; result: unknown } | null> {
        if (!this.tools || !this.toolContext || signal.aborted) return null;
        const id = `gloop-${name}-${++this.toolSeq}`;
        this.emit({ kind: 'tool_call_start', id, name, args });
        let ok = false; let result: unknown;
        try {
            const r = await this.tools.run(name, args, this.toolContext);
            ok = r.ok; result = r.result;
        } catch (e) {
            result = { error: (e as Error).message };
        }
        this.emit({ kind: 'tool_call_result', id, name, ok, result });
        return { ok, result };
    }

    /** Choose which file the edit/debug branch targets: a filename the user
     *  named (if it's a real source), else the sole source, else the first. */
    private async pickTargetFile(userText: string): Promise<string | null> {
        if (!this.toolContext) return null;
        let files: string[] = [];
        try { files = await this.toolContext.workspace.list(); } catch { return null; }
        const sources = files.filter(f => /\.(fbasic|fade)$/i.test(f));
        if (sources.length === 0) return null;
        const named = sources.find(f => userText.toLowerCase().includes(f.toLowerCase()));
        return named ?? sources[0];
    }

    /** edit_code / debug branch — modify EXISTING code rather than start over.
     *  The base to modify is, in priority:
     *    1. the target project file's content (if non-trivial) → applied back via
     *       apply_edit (LSP review + diff approval happen inside the tool);
     *    2. else the last code block the assistant emitted this conversation
     *       (the "I'm iterating on what you just showed me" case) → shown only.
     *  Conversation context + the resolved command signatures are fed in so the
     *  follow-up ("make it faster", "bounce off walls") has something to build
     *  on. Falls back to a fresh write only when there's NO base at all. */
    private async editOrDebug(intent: 'edit_code' | 'debug', userText: string, signal: AbortSignal): Promise<string> {
        const editReq = intent === 'debug' ? `Fix: ${userText}` : userText;

        // 1. Try the project file.
        const path = await this.pickTargetFile(userText);
        let fileContent = '';
        if (path) {
            const readRes = await this.runTool('read_file', { path }, signal);
            fileContent = (readRes?.ok && readRes.result && typeof readRes.result === 'object'
                && 'content' in (readRes.result as object))
                ? String((readRes.result as { content: unknown }).content ?? '') : '';
        }
        // 2. The base: a real file wins; else the last code we emitted.
        const fileIsBase = fileContent.trim().length > 0;
        const base = fileIsBase ? fileContent : (this.lastEmittedCode() ?? '');

        if (!base) {
            // Nothing to modify anywhere → fresh write (with research + assets).
            const assetNote = await this.resolveAssets(userText, signal);
            const design = this.needsDesign(userText) ? await this.planDataModel(userText, signal) : '';
            const hits = await this.curateDocs(userText, await this.research(userText, signal), signal);
            const docs = [design, assetNote, hits.length > 0 ? formatHits(hits) : ''].filter(Boolean).join('\n\n');
            const cmdDocs = await this.resolveCommandDocs(userText, signal);
            let code = await this.emitCode(userText, docs, cmdDocs, signal);
            code = await this.verifyAndFix(userText, docs, cmdDocs, code, signal);
            return '```fade\n' + code + '\n```';
        }

        const steps: { tool: string; description: string }[] = [];
        if (fileIsBase && path) steps.push({ tool: 'read_file', description: path });
        if (intent === 'debug' && fileIsBase && path) steps.push({ tool: 'get_diagnostics', description: path });
        steps.push({ tool: 'resolve_commands', description: 'signatures for the change' });
        if (fileIsBase && path) steps.push({ tool: 'apply_edit', description: path });
        this.emit({ kind: 'plan_emitted', plan: {
            goal: fileIsBase && path
                ? (intent === 'debug' ? `Read ${path}, fix it, apply` : `Modify ${path}, apply`)
                : 'Modify the previous code',
            steps,
        } });

        let diagText = '';
        if (intent === 'debug' && fileIsBase && path) {
            const d = await this.runTool('get_diagnostics', { path }, signal);
            if (d?.ok) diagText = '\n\nCurrent diagnostics:\n' + JSON.stringify(d.result);
        }

        // Design + targeted command discovery for the requested CHANGE (e.g.
        // "10 bouncing balls" → array of a Ball UDT; screen width()/height()),
        // then build the edit prompt.
        const design = this.needsDesign(userText) ? await this.planDataModel(userText, signal, base) : '';
        const cmdDocs = await this.resolveCommandDocs(userText, signal);
        const sysParts = [
            fileIsBase && path
                ? `You are MODIFYING the Fade program in "${path}". Output the COMPLETE updated program — `
                  + 'ONLY code, no prose, no fences. Keep everything that should stay; change only what the '
                  + 'request asks. Build on the program below; do NOT start over.'
                : 'You are MODIFYING an existing Fade program (shown below). Output the COMPLETE updated '
                  + 'program — ONLY code, no prose, no fences. Build on it; do NOT start over.',
            await this.rulesBlock(),
        ];
        if (cmdDocs) sysParts.push('EXACT command reference (call each exactly as shown):\n\n' + cmdDocs);
        if (design) sysParts.push(design);
        const ctx = this.historyContext();
        if (ctx) sysParts.push('Recent conversation:\n' + ctx);
        sysParts.push('Current program to modify:\n' + base + diagText);

        let newContent = await this.emitCodeRaw(sysParts.join('\n\n'), editReq, signal);
        // Edits cap the self-heal at 2 passes (latency on local models).
        newContent = await this.verifyAndFix(editReq, design, cmdDocs, newContent, signal, 2);

        // Apply to the file only when the file WAS the base; otherwise show only.
        if (fileIsBase && path) {
            const lineCount = fileContent.length === 0 ? 1 : fileContent.split('\n').length;
            const applied = await this.runTool('apply_edit',
                { path, startLine: 1, endLine: lineCount, newText: newContent }, signal);
            const ok = applied?.ok === true;
            return (ok ? `Updated \`${path}\`:` : `Proposed change to \`${path}\` (not applied):`)
                + '\n\n```fade\n' + newContent + '\n```';
        }
        return '```fade\n' + newContent + '\n```';
    }

    /** Two-hop RAG retrieval for a question: search the docs for the request,
     *  then BRANCH — follow up on the headings of the top hits to pull related
     *  sections — and return the merged docs. Emits a visible plan + the hits. */
    private async gatherDocs(query: string, signal: AbortSignal): Promise<string> {
        if (!this.retriever || signal.aborted) return '';
        const seen = new Set<string>();
        const hits: SearchHit[] = [];
        const add = (hs: SearchHit[]) => {
            for (const h of hs) {
                const key = `${h.chunk.source}#${h.chunk.heading}`;
                if (!seen.has(key)) { seen.add(key); hits.push(h); }
            }
        };
        const pt = this.getProjectType?.();
        try { add(await this.retriever.search(query, 5, { projectType: pt })); } catch { /* ignore */ }

        // Branch: follow the top hits' headings to find related sections.
        const followups = hits.slice(0, 2).map(h => h.chunk.heading).filter(Boolean);
        this.emit({ kind: 'plan_emitted', plan: {
            goal: 'Look up the docs for this, then read related sections',
            steps: [
                { tool: 'search_docs', description: query },
                ...followups.map(f => ({ tool: 'search_docs', description: `related: ${f}` })),
            ],
        } });
        for (const f of followups) {
            if (signal.aborted) break;
            try { add(await this.retriever.search(f, 2, { projectType: pt })); } catch { /* ignore */ }
        }

        if (hits.length > 0) this.emit({ kind: 'docs_retrieved', query, hits });
        this.emit({ kind: 'reasoning', title: `Read ${hits.length} doc section(s)` });
        return hits.length > 0 ? formatHits(hits) : '';
    }

    /** explain node: gather docs FIRST (two-hop RAG), then answer FROM them —
     *  so the model can't claim a documented feature doesn't exist. No grammar. */
    private async answerQuestion(userText: string, signal: AbortSignal): Promise<string> {
        const docs = await this.gatherDocs(userText, signal);
        const sysParts = [this.systemPrompt, FADE_RULES];
        if (docs) {
            sysParts.push(
                'Relevant Fade documentation — answer FROM these. If the docs describe a '
                + 'feature, it EXISTS; do not claim otherwise. Cite the real command/keyword names.\n\n' + docs,
            );
        }
        return this.runNode({
            system: sysParts.join('\n\n'),
            user: userText,
            temperature: 0.4,
            maxTokens: 1024,
            signal,
            streamToBubble: true,
        });
    }

    // ── Orchestrator ──────────────────────────────────────────────────────────
    async send(userText: string): Promise<void> {
        this.abortController = new AbortController();
        const signal = this.abortController.signal;
        await this.provider.ensureReady();
        this.history.push({ role: 'user', content: userText });

        try {
            this.emit({ kind: 'reasoning', title: 'Classifying the request…' });
            let intent = await this.classifyIntent(userText, signal);
            // A follow-up that tweaks prior code is an EDIT, not a fresh write —
            // route it to the iterate path so it has the previous code to build
            // on (the #1 reason "make it faster / bounce" used to get stuck).
            if (this.lastEmittedCode() && this.isModification(userText)
                && (intent === 'write_code' || intent === 'chat')) {
                intent = 'edit_code';
            }
            this.emit({ kind: 'reasoning', title: `Approach — ${intent}` });

            let answer = '';
            if (intent === 'write_code') {
                // Assets first: figure out what the code may reference (existing
                // file → catalog import → built-in pixel) so it never points at
                // an asset that isn't in the project.
                const assetNote = await this.resolveAssets(userText, signal);
                // Design step (array? UDT? functions?) — only when the request
                // plausibly needs structure; trivial demos skip it for latency.
                const design = this.needsDesign(userText) ? await this.planDataModel(userText, signal) : '';
                const hits = await this.research(userText, signal);
                const selected = await this.curateDocs(userText, hits, signal);
                if (selected.length > 0) this.emit({ kind: 'docs_retrieved', query: userText, hits: selected });
                const docs = [design, assetNote, selected.length > 0 ? formatHits(selected) : '']
                    .filter(Boolean).join('\n\n');
                // Resolve the exact commands + signatures the program will use.
                const cmdDocs = await this.resolveCommandDocs(userText, signal);
                let code = await this.emitCode(userText, docs, cmdDocs, signal);
                code = await this.verifyAndFix(userText, docs, cmdDocs, code, signal);
                answer = '```fade\n' + code + '\n```';
            } else if (intent === 'edit_code' || intent === 'debug') {
                // editOrDebug handles a missing tool registry itself (it shows
                // the modified code instead of applying it).
                answer = await this.editOrDebug(intent, userText, signal);
            } else if (intent === 'explain') {
                answer = await this.answerQuestion(userText, signal);
            } else {
                // chat / greeting / meta: plain prose, no grammar, no docs.
                answer = await this.runNode({
                    system: this.systemPrompt + '\n\n' + FADE_RULES,
                    user: userText,
                    temperature: 0.6,
                    maxTokens: 1024,
                    signal,
                    streamToBubble: true,
                });
            }

            this.history.push({ role: 'assistant', content: answer });
            this.emit({ kind: 'turn_complete', finishReason: 'stop' });
        } catch (e) {
            this.emit({ kind: 'error', message: (e as Error).message ?? String(e) });
            this.emit({ kind: 'turn_complete', finishReason: 'error' });
        } finally {
            this.abortController = null;
        }
    }
}

// A request that plausibly needs an art/audio/font asset → trigger asset
// resolution. Kept broad on the noun side, but only fires for write_code.
const ASSET_REQUEST = /\b(sprite|sprites|image|images|picture|texture|tile|tiles|icon|art|graphic|graphics|sound|sounds|sfx|audio|music|song|font|fonts|background|bg)\b/i;

// Requests that plausibly need a data STRUCTURE (array/UDT) → worth the design
// step. Deliberately excludes a bare "sprite" (a single-actor demo shouldn't
// pay for a planning call); fires on counts, plurals of game entities, system
// words, and a few structured-game names. See needsDesign().
const DESIGN_CUES = /\b(\d+\s+\w+s|many|several|multiple|bunch|lots of|list|array|grid|board|inventory|waves?|spawns?|enem(y|ies)|bullets?|projectiles?|particles?|asteroids?|items?|coins?|stars?|entit(y|ies)|objects?|players?|rows?|columns?|leaderboard|snake|tetris|breakout|invaders|maze|dungeon|rpg|platformer|roguelike|tilemap)\b/i;

export type AssetCategory = 'image' | 'audio' | 'font';

// File extensions per asset category — a sprite wants an IMAGE, a font wants a
// FONT, a sound wants AUDIO. Keeping these separate is what stops a font being
// used to satisfy a sprite request.
const EXT_BY_CATEGORY: Record<AssetCategory, RegExp> = {
    image: /\.(png|jpe?g|bmp|gif|tga|dds)$/i,
    audio: /\.(wav|ogg|mp3|m4a|aiff?)$/i,
    font: /\.(ttf|otf|fnt|spritefont)$/i,
};

const CATEGORY_LABEL: Record<AssetCategory, string> = {
    image: 'Image', audio: 'Audio', font: 'Font',
};

/** Which asset category a request needs. A sprite/image/tile → image; a
 *  sound/sfx/music → audio; a font/typeface → font. Image wins when several
 *  are present (the sprite is the thing that needs a file); defaults to image
 *  since this only runs when ASSET_REQUEST already matched. */
function neededAssetCategory(text: string): AssetCategory {
    const t = text.toLowerCase();
    if (/\b(sprite|sprites|image|images|picture|texture|tile|tiles|icon|art|graphic|graphics|background|bg)\b/.test(t)) return 'image';
    if (/\b(sound|sounds|sfx|audio|music|song|songs)\b/.test(t)) return 'audio';
    if (/\b(font|fonts|typeface)\b/.test(t)) return 'font';
    return 'image';
}

/** Does a catalog entry's MIME type belong to the needed category? Used to
 *  drop, e.g., a font entry that a text search surfaced for a "sprite" query. */
function mimeMatchesCategory(mime: string, category: AssetCategory): boolean {
    const m = (mime || '').toLowerCase();
    if (category === 'image') return m.startsWith('image/');
    if (category === 'audio') return m.startsWith('audio/');
    return m.startsWith('font/') || m.includes('font') || /(ttf|otf|woff)/.test(m);
}

// Tag keywords that mark a PACK as containing a given category of content.
// Packs are zips (mime `application/zip`), so mime can't tell us what's inside
// — we lean on the curated tags instead. Used only to SUGGEST packs (they can't
// be auto-imported), never to import.
const PACK_TAGS: Record<AssetCategory, RegExp> = {
    image: /^(sprite|sprites|tile|tiles|tileset|pixel|art|image|images|graphic|graphics|background|platformer|shmup|dungeon|card|cards|texture|textures)$/i,
    audio: /^(audio|sfx|sound|sounds|music|song|songs|chiptune|soundtrack)$/i,
    font: /^(font|fonts|typography|typeface|typefaces)$/i,
};

/** Whether a pack's tags suggest it contains assets of the needed category. */
function packMatchesCategory(entry: CatalogCandidate, category: AssetCategory): boolean {
    return entry.tags.some(t => PACK_TAGS[category].test(t.trim()));
}

/** Bare content name for an asset path — no folder, no extension
 *  (`catalog-imports/ship.png` → `ship`), matching what the loaders want. */
function bareAssetName(path: string): string {
    return path.replace(/^.*\//, '').replace(/\.[^.]+$/, '');
}

// Instruction for the coder when no real image asset is available: use the
// runtime's built-in 1×1 white-pixel texture (id 0) and size it up.
const PIXEL_FALLBACK_NOTE =
    'There is NO image asset in this project, and none was imported. Do NOT reference any image '
    + 'file (e.g. "player.png" / "ship") — it does not exist and would break the program. Instead use '
    + 'the BUILT-IN 1×1 white-pixel texture: it is texture id 0 and needs NO `texture` load. Create the '
    + 'sprite with texture id 0 — `sprite 1, x, y, 0` — and because the pixel is 1×1 (invisible at '
    + 'default size) you MUST scale it up with `size sprite 1, 50, 50` (choose a sensible size). That '
    + 'draws a solid square placeholder. Use this for any simple/placeholder graphic.';

/** Pull the catalog matches out of a search_catalog tool result (shape:
 *  `{ matches: [...] }`), normalising to CatalogCandidate. Tolerant of the
 *  no-direct-match fallback (which still returns `matches`). */
function extractCatalogMatches(result: unknown): CatalogCandidate[] {
    const raw = (result && typeof result === 'object' && 'matches' in result)
        ? (result as { matches: unknown }).matches : null;
    if (!Array.isArray(raw)) return [];
    const out: CatalogCandidate[] = [];
    for (const e of raw) {
        if (!e || typeof e !== 'object') continue;
        const o = e as Record<string, unknown>;
        if (typeof o.id !== 'number' || typeof o.name !== 'string') continue;
        out.push({
            id: o.id, name: o.name,
            kind: String(o.kind ?? 'asset'),
            mime: String(o.mime ?? ''),
            tags: Array.isArray(o.tags) ? o.tags.filter((t): t is string => typeof t === 'string') : [],
            bytes: typeof o.bytes === 'number' ? o.bytes : 0,
            license: String(o.license ?? ''),
        });
    }
    return out;
}

/** Pull the written workspace paths out of an import_catalog_asset result
 *  (shape: `{ name, paths: [...], imported }`). */
function extractImportedPaths(result: unknown): string[] {
    const raw = (result && typeof result === 'object' && 'paths' in result)
        ? (result as { paths: unknown }).paths : null;
    return Array.isArray(raw) ? raw.filter((p): p is string => typeof p === 'string') : [];
}

/** Render one command's FULL authoritative docs for the coder context — the
 *  complete LSP markdown (signature header + summary + params + examples). We
 *  keep examples now: seeing a real call in context is one of the strongest
 *  signals for getting the syntax right. A high per-command cap is kept only as
 *  a runaway guard for a pathologically long doc; normal docs pass through whole. */
function formatCommandDoc(d: CommandDocLite, maxChars = 4000): string {
    let md = (d.markdown ?? '').trim();
    if (md.length > maxChars) md = md.slice(0, maxChars).trim() + '\n…(doc truncated)';
    // BuildCommandMarkdown already opens with a `### name` header; only add one
    // when the markdown is missing/empty so we don't double it up.
    if (!md) return `### ${d.name}`;
    return /^#{1,6}\s/.test(md) ? md : `### ${d.name}\n${md}`;
}

/** Strip a single leading/trailing ``` fence if the model added one despite
 *  being told not to (happens on unconstrained providers). */
function stripFences(s: string): string {
    const m = /^\s*```[^\n]*\n([\s\S]*?)\n?```\s*$/.exec(s);
    return m ? m[1] : s.trim();
}

/** The LAST fenced code block in a message (``` or ```fade …). Used to recover
 *  the previous program from the assistant's prior turn so a follow-up edit has
 *  a base to modify. Returns null when there's no fenced block. */
function extractLastFenced(s: string): string | null {
    const re = /```[^\n]*\n([\s\S]*?)```/g;
    let m: RegExpExecArray | null;
    let last: string | null = null;
    while ((m = re.exec(s)) !== null) last = m[1].replace(/\n?$/, '');
    return last && last.trim() ? last.trim() : null;
}
