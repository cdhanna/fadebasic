// The agent loop. Talks to a ChatProvider through the unified
// ProtocolEvent stream, executes tool calls via the registry, and emits
// typed AgentEvents to the UI/tests.
//
// Three-phase contract baked into the protocol:
//   1. Plan   — model emits a single <plan>{goal, steps}</plan> block.
//   2. Execute — model emits <tool_call> blocks; we run them, feed results
//                back as <tool_result> user messages, loop.
//   3. Summarize — final plain-text response with no <tool_call>.
//
// We don't enforce the phases as separate provider calls; they're a single
// streaming conversation and the model decides when each phase begins. The
// engine just surfaces the structure to the UI via AgentEvents.

import { getLogger } from '../log-bus';
import type {
    ChatProvider,
    FinishReason,
    Msg,
    StreamOptions,
} from './providers/types';
import {
    BlockStreamParser,
    type AgentPlan,
    type ProtocolEvent,
    getFewShotTurns,
    renderToolProtocolPrompt,
    renderToolCallRetryPrompt,
    renderToolResult,
} from './tool-protocol';
import {
    ACCESS_DENIAL_NUDGE,
    TOOLS_CAPABILITY_BLOCK,
    buildToolRouteHint,
    looksLikeAccessDenial,
} from './tool-awareness';
import { PLAN_SUGGESTION_BLOCK, shouldSuggestPlan } from './plan-suggest';
import {
    WORKSPACE_PREFETCHED_BLOCK,
    WORKSPACE_TOOLS_BLOCK,
    shouldRequireWorkspaceTools,
} from './workspace-tool-hint';
import { filterWorkspacePaths } from './tools/list-files';
import { shouldAutoRetrieveDocs, shouldPrefetchDocs } from './rag/auto-retrieve';
import {
    EDIT_VIA_TOOLS_BLOCK,
    EDIT_VIA_TOOLS_NUDGE,
    looksLikePastedCodeEdit,
    userRequestedCodeChange,
} from './edit-via-tools';
import { Retriever, formatHits, getRetriever } from './rag/retrieval';
import type { SearchHit } from './rag/types';
import { ContextEvictor, type EvictionResult } from './context';
import type { ToolContext, ToolRegistry } from './tools';

const log = {
    agent: getLogger('ai/agent'),
    tool: getLogger('ai/tool'),
    context: getLogger('ai/context'),
    rag: getLogger('ai/rag'),
};

export interface AgentOptions {
    provider: ChatProvider;
    tools: ToolRegistry;
    toolContext: ToolContext;
    /** Hard ceiling on tool-call iterations within a single user turn. */
    maxIterations?: number;
    /** Optional system prompt prepended to every conversation. */
    systemPrompt?: string;
    /** When estimated context usage exceeds this fraction of maxContext,
     *  emit a `budget_warning` event. Default 0.7. */
    budgetWarnAt?: number;
    /** Retrieval used to auto-inject relevant docs on the first user turn.
     *  Pass null to disable auto-retrieval entirely. Defaults to the
     *  module singleton (which loads /docs-index.json). */
    retriever?: Retriever | null;
    /** How many chunks to inject on auto-retrieval. Default 3. */
    autoRetrievalK?: number;
    /** Returns the active project's `type` ('web', 'monogame', …) used to
     *  gate type-scoped chunks (see docs-sources.mjs `projectTypes`). Called
     *  on every retrieval, so changes mid-session are reflected. */
    getProjectType?: () => string | undefined;
    /** Context-eviction config. Pass null to disable eviction entirely
     *  (history grows unbounded until the provider rejects). Defaults
     *  load a ContextEvictor with sensible thresholds. */
    evictor?: ContextEvictor | null;
    /** Loaded Fade command names — injected so the model does not hallucinate APIs. */
    getCommandNames?: () => Promise<string[]>;
}

export type AgentEvent =
    | { kind: 'text_delta'; delta: string }
    | { kind: 'iteration_start'; iteration: number }
    | { kind: 'plan_emitted'; plan: AgentPlan }
    | { kind: 'tool_call_start'; id: string; name: string; args: unknown }
    | { kind: 'tool_call_result'; id: string; name: string; ok: boolean; result: unknown }
    | { kind: 'docs_retrieved'; query: string; hits: SearchHit[] }
    | { kind: 'post_edit_diagnostics'; path: string; errors: number; warnings: number; clean: boolean }
    | { kind: 'budget_warning'; tokens: number; max: number; ratio: number }
    | { kind: 'eviction'; result: EvictionResult; tokensBefore: number; tokensAfter: number; max: number }
    | { kind: 'turn_complete'; finishReason: FinishReason }
    | { kind: 'error'; message: string };

export type AgentListener = (ev: AgentEvent) => void;

const DEFAULT_SYSTEM_PROMPT =
    'You are a coding assistant working inside a Fade Playground editor. ' +
    'Be direct and pragmatic — no filler, no over-explanation.\n\n' +
    'Workspace inspection is cheap. When the user asks about THEIR project, ' +
    'THEIR code, THEIR files, THEIR errors — or anything that depends on what ' +
    "is actually in the workspace — your first action must be a tool call to " +
    'read the relevant file(s). Do not invent file contents or fabricate ' +
    'project structure. Conceptual questions about Fade itself can be answered ' +
    'directly from the docs block below.\n\n' +
    'SELF-HEALING: After every successful apply_edit or create_file, the runtime '
    + 'automatically checks the LSP and feeds back a <post_edit_diagnostics> '
    + 'block. If it reports errors, you MUST issue another tool call to fix them '
    + '(read the file at the reported line, then apply_edit). Only finish with '
    + 'plain text once diagnostics are clean OR the remaining issues are clearly '
    + 'pre-existing / unrelated to your edit. Do not call get_diagnostics '
    + 'yourself right after an edit — the runtime already does that for you.\n\n'
    + 'When you are done, reply with plain text and no <tool_call> block.';

const MAX_ITERATIONS_DEFAULT = 8;
/** How many times to nudge the model after a malformed tool_call parse. */
const MAX_TOOL_PARSE_RETRIES = 2;
/** How long to wait after a write tool before reading diagnostics back from
 *  the LSP. Most LSPs publish updated markers within a few hundred ms of a
 *  file change; this gives them time without making the user feel a delay. */
const POST_EDIT_DIAGNOSTICS_DELAY_MS = 400;
const DEFAULT_BUDGET_WARN = 0.7;

export class Agent {
    private readonly provider: ChatProvider;
    private readonly tools: ToolRegistry;
    private readonly toolContext: ToolContext;
    private readonly maxIterations: number;
    private readonly systemPrompt: string;
    private readonly budgetWarnAt: number;
    private readonly retriever: Retriever | null;
    private readonly autoRetrievalK: number;
    private readonly getProjectType: (() => string | undefined) | null;
    private readonly evictor: ContextEvictor | null;
    private readonly getCommandNames: (() => Promise<string[]>) | null;
    private listeners = new Set<AgentListener>();
    /** Persistent conversation history. Survives across sends. */
    private history: Msg[] = [];
    private abortController: AbortController | null = null;

    constructor(opts: AgentOptions) {
        this.provider = opts.provider;
        this.tools = opts.tools;
        this.toolContext = opts.toolContext;
        this.maxIterations = opts.maxIterations ?? MAX_ITERATIONS_DEFAULT;
        this.systemPrompt = opts.systemPrompt ?? DEFAULT_SYSTEM_PROMPT;
        this.budgetWarnAt = opts.budgetWarnAt ?? DEFAULT_BUDGET_WARN;
        // null disables; undefined falls back to the module singleton.
        this.retriever = opts.retriever === null
            ? null
            : (opts.retriever ?? getRetriever());
        this.autoRetrievalK = opts.autoRetrievalK ?? 3;
        this.getProjectType = opts.getProjectType ?? null;
        // null disables; undefined creates a default evictor bound to this provider.
        this.evictor = opts.evictor === null
            ? null
            : (opts.evictor ?? new ContextEvictor({ provider: opts.provider }));
        this.getCommandNames = opts.getCommandNames ?? null;
    }

    on(listener: AgentListener): () => void {
        this.listeners.add(listener);
        return () => { this.listeners.delete(listener); };
    }

    private emit(ev: AgentEvent): void {
        for (const l of this.listeners) {
            try { l(ev); } catch (e) { console.error('[agent] listener threw', e); }
        }
    }

    abort(): void {
        this.abortController?.abort();
    }

    getAbortSignal(): AbortSignal | undefined {
        return this.abortController?.signal;
    }

    getHistory(): readonly Msg[] {
        return this.history;
    }

    setHistory(msgs: Msg[]): void {
        this.history = msgs.slice();
    }

    clearHistory(): void {
        this.history = [];
    }

    /** Send a user message and run the agent loop to completion. */
    async send(userText: string): Promise<void> {
        this.abortController = new AbortController();
        const signal = this.abortController.signal;
        this.toolContext.abortSignal = signal;

        await this.provider.ensureReady();

        this.history.push({ role: 'user', content: userText });
        log.agent.info(`turn started — provider=${this.provider.id} history=${this.history.length}`);

        // Inventory questions ("what is in this project?") — run list_files
        // before the model's first pass. Small models skip tool calls even
        // with strong prompts; prefetching guarantees a visible tool row +
        // fresh JSON in history.
        const listFilesPrefetched = shouldRequireWorkspaceTools(userText)
            ? await this.prefetchListFiles(signal)
            : false;

        const docsPrefetched = shouldPrefetchDocs(userText)
            ? await this.prefetchSearchDocs(userText, signal)
            : false;

        // Auto-retrieve docs relevant to the user's message.
        const docsBlock = await this.runAutoRetrieval(userText);
        const commandCatalogBlock = await this.buildCommandCatalogBlock();

        // Snapshot the workspace state (project name, files, open file,
        // diagnostic count). Goes LAST in the system message so it's the
        // most salient context the model sees before deciding what to do.
        const workspaceBlock = await this.buildWorkspaceContext();

        const tools = this.tools.describe();
        const toolPrompt = renderToolProtocolPrompt(tools);

        // Ordering inside the system message — models pay disproportionate
        // attention to the LAST section before the user's turn. Put the
        // section that should drive behavior (workspace state + its
        // closing directive) at the end. Reference docs come before it
        // so they describe the language without being the model's first
        // instinct for "what to answer from".
        const sections = [this.systemPrompt, toolPrompt, TOOLS_CAPABILITY_BLOCK];
        const toolRoute = buildToolRouteHint(userText);
        if (toolRoute) sections.push(toolRoute);
        if (shouldRequireWorkspaceTools(userText)) {
            sections.push(listFilesPrefetched ? WORKSPACE_PREFETCHED_BLOCK : WORKSPACE_TOOLS_BLOCK);
        }
        if (shouldSuggestPlan(userText)) sections.push(PLAN_SUGGESTION_BLOCK);
        if (userRequestedCodeChange(userText)) sections.push(EDIT_VIA_TOOLS_BLOCK);
        if (commandCatalogBlock) sections.push(commandCatalogBlock);
        if (docsBlock) sections.push(docsBlock);
        if (docsPrefetched) {
            sections.push('Docs were prefetched via search_docs — use those results for command syntax.');
        }
        if (workspaceBlock) sections.push(workspaceBlock);
        const systemMsg: Msg = {
            role: 'system',
            content: sections.join('\n\n'),
        };

        // Few-shot examples as real conversation turns — much stronger
        // pattern signal for small models than the same examples baked
        // into the system prompt. ~400 tokens overhead per send, never
        // saved to history.
        const fewShot = getFewShotTurns();

        let iteration = 0;
        let lastFinishReason: FinishReason = 'stop';
        // One-shot nudge budget: if the model emits only a plan and stops,
        // we feed it a synthetic prompt to continue. Capped so a broken
        // model can't loop forever.
        let planContinuationUsed = false;
        let accessDenialNudgeUsed = false;
        let editViaToolsNudgeUsed = false;
        let toolParseRetries = 0;

        try {
            while (iteration < this.maxIterations) {
                iteration++;
                this.emit({ kind: 'iteration_start', iteration });

                await this.runEviction(systemMsg);

                const messages = [systemMsg, ...fewShot, ...this.history];
                this.checkBudget(messages);

                // Dump the assembled prompt at debug. Pairs with the
                // "raw model output" debug line in runStream — together
                // they explain why a turn went the way it did.
                if (iteration === 1) {
                    log.agent.debug(`prompt (system, ${this.provider.countTokens(systemMsg.content)} tokens): ${truncateForLog(systemMsg.content, 2000)}`);
                }

                const streamOpts: StreamOptions = { messages, signal };
                const { text, toolCalls, finishReason, planEmitted, toolParseError } = await this.runStream(streamOpts);
                lastFinishReason = finishReason;

                // Record what the assistant said. Tool calls re-rendered so
                // the next iteration's prompt mirrors the model's output.
                this.history.push({
                    role: 'assistant',
                    content: renderAssistantMessage(text, toolCalls),
                });

                if (finishReason === 'aborted' || finishReason === 'error') {
                    break;
                }

                if (toolCalls.length > 0) {
                    toolParseRetries = 0;
                    await this.executeToolCalls(toolCalls);
                    continue;
                }

                // Malformed tool_call JSON/tags — nudge the model to retry.
                if (toolParseError && toolParseRetries < MAX_TOOL_PARSE_RETRIES) {
                    toolParseRetries++;
                    log.agent.warn(`tool_call parse failed — retry ${toolParseRetries}/${MAX_TOOL_PARSE_RETRIES}: ${toolParseError}`);
                    this.history.push({
                        role: 'user',
                        content: renderToolCallRetryPrompt(toolParseError),
                    });
                    continue;
                }

                if (toolParseError) {
                    log.agent.warn(`tool_call parse failed after ${MAX_TOOL_PARSE_RETRIES} retries: ${toolParseError}`);
                    this.emit({
                        kind: 'error',
                        message: `Could not parse tool call: ${toolParseError}`,
                    });
                    break;
                }

                // No tool calls — usually we'd break and let the model's
                // text be the answer. Two failure modes to catch:
                //   (a) plan emitted but no follow-up → nudge once
                //   (b) totally empty turn → tell the user
                if (text.length === 0 && planEmitted && !planContinuationUsed) {
                    planContinuationUsed = true;
                    log.agent.warn('plan-only response — feeding continuation nudge');
                    this.history.push({
                        role: 'user',
                        content: 'Continue. Emit either a <tool_call> to inspect a file, '
                            + 'or your final plain-text answer. Do not stop after only a plan.',
                    });
                    continue;
                }

                if (text.length === 0 && !planEmitted) {
                    log.agent.warn('empty turn — no text, no plan, no tool calls');
                    this.emit({
                        kind: 'error',
                        message: 'Model returned no output. Try rephrasing, or check /context.',
                    });
                }

                // Model claimed it can't see source without calling tools.
                if (text.length > 0
                    && toolCalls.length === 0
                    && !accessDenialNudgeUsed
                    && looksLikeAccessDenial(text)
                    && buildToolRouteHint(userText)) {
                    accessDenialNudgeUsed = true;
                    log.agent.warn('access-denial hallucination — nudging to use tools');
                    this.history.push({ role: 'user', content: ACCESS_DENIAL_NUDGE });
                    continue;
                }

                // Model pasted code in markdown instead of apply_edit / create_file.
                if (text.length > 0
                    && toolCalls.length === 0
                    && !editViaToolsNudgeUsed
                    && userRequestedCodeChange(userText)
                    && looksLikePastedCodeEdit(text)) {
                    editViaToolsNudgeUsed = true;
                    log.agent.warn('markdown code edit — nudging to use write tools');
                    this.history.push({ role: 'user', content: EDIT_VIA_TOOLS_NUDGE });
                    continue;
                }

                break;
            }

            if (iteration >= this.maxIterations) {
                log.agent.warn(`hit max iterations (${this.maxIterations})`);
                this.emit({
                    kind: 'error',
                    message: `Stopped after ${this.maxIterations} tool iterations.`,
                });
            }

            this.emit({ kind: 'turn_complete', finishReason: lastFinishReason });
            log.agent.info(`turn complete reason=${lastFinishReason}`);
        } catch (e) {
            const msg = (e as Error).message ?? String(e);
            log.agent.error(`turn failed: ${msg}`);
            this.emit({ kind: 'error', message: msg });
            this.emit({ kind: 'turn_complete', finishReason: 'error' });
        } finally {
            this.toolContext.abortSignal = undefined;
            this.abortController = null;
        }
    }

    /** Snapshot what the user is looking at right now. Cheap (~50 tokens
     *  typical) — file lists are tiny, diagnostics are just a count, and
     *  the editor adapter returns string/number directly. Failures degrade
     *  silently: a missing diagnostics adapter just drops that line. */
    private async buildWorkspaceContext(): Promise<string | null> {
        const ws = this.toolContext.workspace;
        if (!ws) return null;

        const lines: string[] = ['Workspace state:'];
        let captured = false;

        // Project name + file list. List is the load-bearing line — it
        // tells the model what files exist before it needs to ask.
        try {
            const project = ws.currentProject();
            const files = filterWorkspacePaths(await ws.list());
            lines.push(`Project: ${project}`);
            lines.push(formatWorkspaceFileList(files));
            captured = true;
        } catch (e) {
            log.context.debug(`workspace list failed: ${(e as Error).message}`);
        }

        // Editor focus — currently focused file + cursor line. Lets the
        // user ask things like "fix this" without naming the file.
        const editor = this.toolContext.editor;
        if (editor) {
            const active = editor.activeFile();
            if (active) {
                const line = editor.cursorLine();
                lines.push(line != null
                    ? `Open file: ${active} (cursor at line ${line + 1})`
                    : `Open file: ${active}`);
                captured = true;
            }
        }

        // Diagnostics summary. Just counts at this level — the model can
        // call get_diagnostics for the details.
        const diags = this.toolContext.diagnostics;
        if (diags) {
            try {
                const all = await diags.getAll();
                const errors = all.filter(d => d.severity === 'error').length;
                const warnings = all.filter(d => d.severity === 'warning').length;
                if (errors + warnings === 0 && all.length === 0) {
                    lines.push('Diagnostics: clean');
                } else if (errors + warnings === 0) {
                    lines.push(`Diagnostics: ${all.length} info/hint`);
                } else {
                    lines.push(`Diagnostics: ${errors} error(s), ${warnings} warning(s)`);
                }
                captured = true;
            } catch (e) {
                log.context.debug(`diagnostics getAll failed: ${(e as Error).message}`);
            }
        }

        if (!captured) return null;

        // Trailing directive — small models tend to answer from the docs
        // block alone when the question could be either workspace-specific
        // or conceptual. This sentence tells them which lane to pick.
        lines.push('');
        lines.push(
            'If the user references "the main file", "my code", "the project", or any '
            + 'file in the list above, call read_file on that path BEFORE answering. '
            + 'For a full/authoritative listing, call list_files. '
            + 'The docs block describes Fade in general — it does NOT describe THIS project.',
        );

        log.context.debug(`workspace context: ${lines.length - 1} fields captured`);
        return lines.join('\n');
    }

    /** Run list_files before the model turn for inventory questions. */
    private async prefetchListFiles(signal: AbortSignal): Promise<boolean> {
        if (signal.aborted) return false;
        const id = `auto-list-${Date.now()}`;
        this.emit({ kind: 'tool_call_start', id, name: 'list_files', args: {} });
        log.tool.info('prefetch list_files (workspace inventory question)');
        try {
            const r = await this.tools.run('list_files', {}, this.toolContext);
            this.emit({ kind: 'tool_call_result', id, name: 'list_files', ok: r.ok, result: r.result });
            this.history.push({
                role: 'assistant',
                content: '<tool_call>{"name":"list_files","args":{}}</tool_call>',
            });
            this.history.push({
                role: 'user',
                content: renderToolResult('list_files', r.ok ? r.result : r.result),
            });
            return r.ok;
        } catch (e) {
            const err = (e as Error).message;
            log.tool.warn(`prefetch list_files failed: ${err}`);
            const result = { error: err };
            this.emit({
                kind: 'tool_call_result',
                id,
                name: 'list_files',
                ok: false,
                result,
            });
            this.history.push({
                role: 'assistant',
                content: '<tool_call>{"name":"list_files","args":{}}</tool_call>',
            });
            this.history.push({
                role: 'user',
                content: renderToolResult('list_files', result),
            });
            return false;
        }
    }

    /** Inject loaded command names so the model does not invent APIs. */
    private async buildCommandCatalogBlock(): Promise<string | null> {
        if (!this.getCommandNames) return null;
        try {
            const names = await this.getCommandNames();
            if (names.length === 0) return null;
            const sample = names.slice(0, 80).join(', ');
            const more = names.length > 80 ? ` … +${names.length - 80} more` : '';
            return (
                `Loaded Fade commands (${names.length}): ${sample}${more}\n`
                + 'Do NOT invent command names. Call search_docs before using any command not listed.'
            );
        } catch (e) {
            log.context.debug(`command catalog failed: ${(e as Error).message}`);
            return null;
        }
    }

    /** Run search_docs before the model turn for command/API questions. */
    private async prefetchSearchDocs(query: string, signal: AbortSignal): Promise<boolean> {
        if (signal.aborted) return false;
        const id = `auto-docs-${Date.now()}`;
        const args = { query, k: this.autoRetrievalK };
        this.emit({ kind: 'tool_call_start', id, name: 'search_docs', args });
        log.tool.info(`prefetch search_docs: "${query.slice(0, 60)}"`);
        try {
            const r = await this.tools.run('search_docs', args, this.toolContext);
            this.emit({ kind: 'tool_call_result', id, name: 'search_docs', ok: r.ok, result: r.result });
            this.history.push({
                role: 'assistant',
                content: `<tool_call>{"name":"search_docs","args":${JSON.stringify(args)}}</tool_call>`,
            });
            this.history.push({
                role: 'user',
                content: renderToolResult('search_docs', r.ok ? r.result : r.result),
            });
            if (r.ok && r.result && typeof r.result === 'object' && 'hits' in (r.result as object)) {
                const hits = (r.result as { hits?: SearchHit[] }).hits;
                if (hits?.length) this.emit({ kind: 'docs_retrieved', query, hits });
            }
            return r.ok;
        } catch (e) {
            const err = (e as Error).message;
            log.tool.warn(`prefetch search_docs failed: ${err}`);
            const result = { error: err };
            this.emit({
                kind: 'tool_call_result',
                id,
                name: 'search_docs',
                ok: false,
                result,
            });
            this.history.push({
                role: 'assistant',
                content: `<tool_call>{"name":"search_docs","args":${JSON.stringify(args)}}</tool_call>`,
            });
            this.history.push({
                role: 'user',
                content: renderToolResult('search_docs', result),
            });
            return false;
        }
    }

    /** Embed the user message, retrieve top-K chunks, format for the
     *  system prompt. Returns null when retrieval is disabled, the index
     *  is missing, or no chunks match. Failures are logged but never
     *  thrown — the agent always proceeds. */
    private async runAutoRetrieval(query: string): Promise<string | null> {
        if (!this.retriever) return null;
        if (!shouldAutoRetrieveDocs(query)) {
            log.rag.debug(`auto-retrieval skipped for workspace-style query: "${query.slice(0, 60)}"`);
            return null;
        }
        try {
            const hits = await this.retriever.search(query, this.autoRetrievalK, {
                projectType: this.getProjectType?.(),
            });
            if (hits.length === 0) return null;
            log.rag.info(`auto-retrieved ${hits.length} chunk(s) for "${query.slice(0, 60)}"`);
            this.emit({ kind: 'docs_retrieved', query, hits });
            return formatHits(hits);
        } catch (e) {
            log.rag.warn(`auto-retrieval failed: ${(e as Error).message}`);
            return null;
        }
    }

    /** Apply the eviction ladder before each iteration. Mutates history
     *  in place. Re-throws nothing — eviction failures degrade to a
     *  warning log so the agent still tries to send. */
    private async runEviction(systemMsg: Msg): Promise<void> {
        if (!this.evictor) return;
        const max = this.provider.capabilities.maxContext;
        if (!max) return;

        const tokensBefore = this.evictor.countTokens(systemMsg, this.history);
        try {
            const result = await this.evictor.evictToBudget(systemMsg, this.history);
            if (result.saved > 0) {
                const tokensAfter = this.evictor.countTokens(systemMsg, this.history);
                this.emit({ kind: 'eviction', result, tokensBefore, tokensAfter, max });
            }
        } catch (e) {
            log.context.warn(`eviction failed: ${(e as Error).message}`);
        }
    }

    /** Estimate token usage and warn the UI if we're approaching the
     *  provider's context limit. Cheap — we approximate via the provider's
     *  countTokens; if it's the real tokenizer the number is exact. */
    private checkBudget(messages: Msg[]): void {
        const max = this.provider.capabilities.maxContext;
        if (!max) return;
        let tokens = 0;
        for (const m of messages) tokens += this.provider.countTokens(m.content);
        const ratio = tokens / max;
        log.context.debug(`budget: ${tokens}/${max} tokens (${(ratio * 100).toFixed(1)}%)`);
        if (ratio >= this.budgetWarnAt) {
            log.context.warn(`context budget high: ${tokens}/${max}`);
            this.emit({ kind: 'budget_warning', tokens, max, ratio });
        }
    }

    private async executeToolCalls(
        calls: Array<{ id: string; name: string; args: unknown }>,
    ): Promise<void> {
        // Run read-only tools in parallel; everything else sequentially
        // (write tools need user confirmation, and parallelizing them would
        // make the UX weird). Order of results in history matches order of
        // emissions from the model.
        const reads: Array<{ idx: number; call: typeof calls[number] }> = [];
        const writes: Array<{ idx: number; call: typeof calls[number] }> = [];
        for (let i = 0; i < calls.length; i++) {
            const tool = this.tools.get(calls[i].name);
            if (tool?.readOnly) reads.push({ idx: i, call: calls[i] });
            else writes.push({ idx: i, call: calls[i] });
        }

        const results: Array<{ id: string; name: string; ok: boolean; result: unknown }> = new Array(calls.length);

        const runOne = async (idx: number, call: typeof calls[number]) => {
            this.emit({ kind: 'tool_call_start', id: call.id, name: call.name, args: call.args });
            log.tool.info(`call ${call.name} id=${call.id}`);
            const r = await this.tools.run(call.name, call.args, this.toolContext);
            log.tool.info(`result ${call.name} id=${call.id} ok=${r.ok}`);
            results[idx] = { id: call.id, name: call.name, ok: r.ok, result: r.result };
            this.emit({ kind: 'tool_call_result', ...results[idx] });
        };

        // Parallel reads.
        if (reads.length > 0) {
            await Promise.all(reads.map(r => runOne(r.idx, r.call)));
        }
        // Sequential writes.
        for (const w of writes) {
            await runOne(w.idx, w.call);
        }

        // Feed results back to the model in the order the model emitted them.
        for (const r of results) {
            this.history.push({
                role: 'user',
                content: renderToolResult(r.name, r.result),
            });
        }

        // Self-healing: for any successful write call with a `path` arg,
        // probe the LSP and inject a synthetic post-edit diagnostics block.
        // The model is told (in the system prompt) that this happens, so
        // it knows to react to errors here without manually calling
        // get_diagnostics — cuts the round-trip and pushes the model
        // toward fixing its own mistakes.
        const writtenPaths = collectWrittenPaths(writes, results);
        if (writtenPaths.size > 0) {
            await this.injectPostEditDiagnostics(writtenPaths);
        }
    }

    /** Wait for the LSP to settle, fetch diagnostics for each edited path,
     *  and push a synthetic user message into history so the model sees
     *  them on the next iteration. Failures degrade silently — a missing
     *  diagnostics adapter or a slow LSP just means no feedback block,
     *  which is no worse than the pre-self-healing behavior. */
    private async injectPostEditDiagnostics(paths: Set<string>): Promise<void> {
        const diags = this.toolContext.diagnostics;
        if (!diags) return;

        // Brief delay so the LSP has a chance to re-analyze the file.
        // Run it once total, not once per path — typical edits touch one
        // file and parallel writes are rare for this agent.
        await new Promise<void>(resolve => setTimeout(resolve, POST_EDIT_DIAGNOSTICS_DELAY_MS));

        for (const path of paths) {
            let entries;
            try {
                entries = await diags.forFile(path);
            } catch (e) {
                log.context.debug(`post-edit diagnostics for ${path} failed: ${(e as Error).message}`);
                continue;
            }

            const errors = entries.filter(d => d.severity === 'error').length;
            const warnings = entries.filter(d => d.severity === 'warning').length;
            const clean = errors === 0 && warnings === 0;

            this.emit({ kind: 'post_edit_diagnostics', path, errors, warnings, clean });
            this.history.push({
                role: 'user',
                content: renderPostEditDiagnostics(path, entries),
            });
        }
    }

    /** Run one provider stream, collecting text, plans, and tool calls.
     *  Also accumulates the raw pre-parser text and logs it at debug — when
     *  the agent looks like it "did nothing," that log line shows what the
     *  model actually emitted (including malformed tool calls that the
     *  parser would otherwise drop on the floor). */
    private async runStream(opts: StreamOptions): Promise<{
        text: string;
        toolCalls: Array<{ id: string; name: string; args: unknown }>;
        finishReason: FinishReason;
        planEmitted: boolean;
        toolParseError: string | null;
    }> {
        const text: string[] = [];
        const toolCalls: Array<{ id: string; name: string; args: unknown }> = [];
        const rawOutput: string[] = [];
        let finishReason: FinishReason = 'stop';
        const planMarker = { emitted: false };
        let toolParseError: string | null = null;

        const useNativeTools = this.provider.capabilities.supportsTools;
        const parser = useNativeTools ? null : new BlockStreamParser();

        for await (const ev of this.provider.stream(opts)) {
            if (ev.kind === 'done') { finishReason = ev.finishReason; continue; }
            if (useNativeTools) {
                this.handleEvent(ev, text, toolCalls, planMarker, err => { toolParseError = err; });
                continue;
            }
            if (ev.kind === 'text') {
                rawOutput.push(ev.delta);
                for (const out of parser!.feed(ev.delta)) {
                    this.handleEvent(out, text, toolCalls, planMarker, err => { toolParseError = err; });
                }
            } else if (ev.kind === 'tool_call') {
                this.handleEvent(ev, text, toolCalls, planMarker, err => { toolParseError = err; });
            }
        }
        if (parser) {
            for (const out of parser.end()) {
                this.handleEvent(out, text, toolCalls, planMarker, err => { toolParseError = err; });
            }
        }

        const rawJoined = rawOutput.join('');
        log.agent.debug(`raw model output (${rawJoined.length} chars): ${truncateForLog(rawJoined)}`);

        return {
            text: text.join(''),
            toolCalls,
            finishReason,
            planEmitted: planMarker.emitted,
            toolParseError,
        };
    }

    private handleEvent(
        ev: ProtocolEvent,
        text: string[],
        toolCalls: Array<{ id: string; name: string; args: unknown }>,
        planMarker: { emitted: boolean },
        onParseError: (msg: string) => void,
    ): void {
        if (ev.kind === 'text') {
            text.push(ev.delta);
            this.emit({ kind: 'text_delta', delta: ev.delta });
        } else if (ev.kind === 'tool_call') {
            toolCalls.push({ id: ev.id, name: ev.name, args: ev.args });
        } else if (ev.kind === 'tool_parse_error') {
            onParseError(ev.error);
            log.agent.debug(`tool_call parse error: ${ev.error} (raw ${truncateForLog(ev.raw, 200)})`);
        } else if (ev.kind === 'plan') {
            log.agent.info(`plan: ${ev.plan.goal} (${ev.plan.steps.length} steps)`);
            planMarker.emitted = true;
            this.emit({ kind: 'plan_emitted', plan: ev.plan });
        }
    }
}

function formatWorkspaceFileList(files: string[]): string {
    if (files.length === 0) return 'Files: (empty)';
    if (files.length <= 25) return `Files (${files.length}): ${files.join(', ')}`;
    return `Files (${files.length}, first 25): ${files.slice(0, 25).join(', ')}, …`;
}

function truncateForLog(s: string, max: number = 1500): string {
    if (s.length <= max) return s.replace(/\n/g, '\\n');
    return s.slice(0, max).replace(/\n/g, '\\n') + `…[+${s.length - max} chars]`;
}

/** Pick out the file paths that were successfully written this iteration.
 *  Walks the call/result pairs; only counts a path if the call had a
 *  `path` string in args AND the tool returned ok=true. Paths arrive in
 *  the order the tools ran (writes run sequentially), deduped via Set. */
function collectWrittenPaths(
    writes: Array<{ idx: number; call: { id: string; name: string; args: unknown } }>,
    results: Array<{ id: string; ok: boolean }>,
): Set<string> {
    const paths = new Set<string>();
    for (const w of writes) {
        const r = results[w.idx];
        if (!r || !r.ok) continue;
        const args = w.call.args;
        if (args && typeof args === 'object' && 'path' in args) {
            const p = (args as { path: unknown }).path;
            if (typeof p === 'string' && p.length > 0) paths.add(p);
        }
    }
    return paths;
}

/** Render the synthetic user message that feeds LSP diagnostics back to
 *  the model after a successful write. Kept compact: header line +
 *  bullet-per-finding, sorted error→warning→info→hint. Always emitted
 *  even when clean so the model knows verification happened and doesn't
 *  redundantly call get_diagnostics. */
function renderPostEditDiagnostics(
    path: string,
    entries: ReadonlyArray<{
        severity: string;
        line: number;
        column: number;
        message: string;
        code?: string;
    }>,
): string {
    if (entries.length === 0) {
        return `<post_edit_diagnostics path="${path}">clean — no errors or warnings.</post_edit_diagnostics>`;
    }
    const rank: Record<string, number> = { error: 0, warning: 1, info: 2, hint: 3 };
    const sorted = [...entries].sort((a, b) => (rank[a.severity] ?? 9) - (rank[b.severity] ?? 9) || a.line - b.line);
    // Cap the bullet list so a giant pre-existing pile of warnings can't
    // flood the prompt. The model can call get_diagnostics for more.
    const max = 20;
    const shown = sorted.slice(0, max);
    const extra = sorted.length - shown.length;
    const lines = shown.map(d => {
        const code = d.code ? ` [${d.code}]` : '';
        return `- ${d.severity} at ${d.line}:${d.column}${code} — ${d.message}`;
    });
    if (extra > 0) lines.push(`- …and ${extra} more`);
    const body = lines.join('\n');
    const guidance = sorted.some(d => d.severity === 'error')
        ? 'If any error above was caused by your edit, fix it with another tool call. '
            + 'If errors look pre-existing or unrelated, mention them in your final answer.'
        : 'No errors. Address warnings only if they look caused by your edit.';
    return `<post_edit_diagnostics path="${path}">\n${body}\n${guidance}\n</post_edit_diagnostics>`;
}

function renderAssistantMessage(
    text: string,
    toolCalls: Array<{ id: string; name: string; args: unknown }>,
): string {
    if (toolCalls.length === 0) return text;
    const calls = toolCalls
        .map(c => `<tool_call>\n${JSON.stringify({ id: c.id, name: c.name, args: c.args })}\n</tool_call>`)
        .join('\n');
    return text ? `${text}\n${calls}` : calls;
}
