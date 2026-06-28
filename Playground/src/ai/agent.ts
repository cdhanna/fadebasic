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
    CODE_RESEARCH_PROTOCOL,
    looksLikePastedCodeEdit,
    userRequestedCodeChange,
    userWantsCode,
} from './edit-via-tools';
import { Retriever, formatHits, getRetriever } from './rag/retrieval';
import type { SearchHit } from './rag/types';
import { ContextEvictor, type EvictionResult } from './context';
import type { ToolContext, ToolRegistry } from './tools';
import { checkAssetRefs, extractCodeBlocks, type AssetRef } from './asset-refs';
import { FADE_RULES } from './fade-rules';
import { MONOGAME_RULES } from './monogame-rules';
import { extractCommandPhrases, detectMissingCallParens } from './command-phrases';
import { detectFadeAntiPatterns } from './fade-antipatterns';
import { detectUnknownCommands, detectCommandAsVariable } from './fade-command-check';

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

/** A clickable chip in a reasoning row's detail — e.g. a command name that
 *  opens its help-doc entry when clicked. */
export interface ReasoningLink { label: string; symbol?: string }

export type AgentEvent =
    | { kind: 'text_delta'; delta: string }
    /** Every raw model token (including tool-call syntax), for live
     *  throughput metrics — distinct from text_delta, which is prose only. */
    | { kind: 'model_token'; delta: string }
    | { kind: 'iteration_start'; iteration: number }
    /** An internal agent step (request classification, self-review, …) — shown
     *  to the user as a collapsible "thinking" row so the loop is legible.
     *  `links` renders the detail as clickable chips (e.g. command names that
     *  open the help docs) instead of plain text. */
    | { kind: 'reasoning'; title: string; detail?: string; links?: ReasoningLink[] }
    /** The repair sub-agent is starting a fresh rewrite pass — the UI should
     *  clear the current answer bubble so the new attempt streams in cleanly. */
    | { kind: 'revising' }
    /** The answer was rewritten by the isolated repair sub-agent — the UI
     *  should replace the streamed bubble with this corrected text. */
    | { kind: 'answer_revised'; text: string }
    | { kind: 'plan_emitted'; plan: AgentPlan }
    | { kind: 'tool_call_start'; id: string; name: string; args: unknown }
    | { kind: 'tool_call_result'; id: string; name: string; ok: boolean; result: unknown }
    | { kind: 'docs_retrieved'; query: string; hits: SearchHit[] }
    | { kind: 'post_edit_diagnostics'; path: string; errors: number; warnings: number; clean: boolean }
    | { kind: 'budget_warning'; tokens: number; max: number; ratio: number }
    | { kind: 'eviction'; result: EvictionResult; tokensBefore: number; tokensAfter: number; max: number }
    | { kind: 'asset_report'; present: AssetRef[]; missing: AssetRef[] }
    | { kind: 'code_lint'; issues: Array<{ line: number; message: string; code?: string }> }
    | { kind: 'suggestion'; suggestions: TurnSuggestion[] }
    | { kind: 'turn_complete'; finishReason: FinishReason }
    | { kind: 'error'; message: string };

/** An end-of-turn "next step" the assistant offers. The UI renders these as
 *  clickable chips; clicking sends `prompt` as the next user message. */
export interface TurnSuggestion {
    title: string;
    prompt: string;
}

/** Decision-tree intents for a code request, chosen by the router. */
export type CodeIntent = 'write_code' | 'edit_code' | 'debug';

/** The router's plan: which branch, which files to read, which commands to
 *  research — drives the visible preparation before the model writes code. */
export interface CodeRoutePlan {
    intent: CodeIntent;
    files: string[];
    capabilities: string[];
}

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
/** Max repair attempts inside the isolated code-fixer sub-agent. */
const MAX_REPAIR_PASSES = 3;
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
    /** Lazily-fetched Fade command names, used to detect when the model
     *  speculates about a real command instead of looking it up. */
    private cachedCommandNames: string[] | null = null;
    /** Monotonic id source for router-driven prefetch tool calls. */
    private routeSeq = 0;
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

        // "Fix the code you showed" — the user is referring to a snippet from a
        // PRIOR reply that was never written to a file. Pin that exact code into
        // context so the agent edits it inline instead of (wrongly) reading
        // main.fbasic. Skips the file-reading router for this turn.
        const priorCode = this.referencesPriorOutput(userText) ? this.lastAssistantCodeBlock() : null;

        // For code requests, run the AI router: it classifies the intent
        // (write / edit / debug) and deterministically drives the right visible
        // tools — read the target file(s), fetch diagnostics, research each
        // command via search_docs — before any code is written. Otherwise fall
        // back to the single-shot doc prefetch for API questions.
        let docsPrefetched = false;
        if (priorCode) {
            this.emit({
                kind: 'reasoning',
                title: 'You mean the code from my previous reply (not a project file)',
                detail: priorCode,
            });
            this.history.push({
                role: 'user',
                content:
                    'CONTEXT: when I say "the code", "that", "it", or "the snippet", I mean THIS code from '
                    + 'your previous reply. It was only SHOWN in chat — it is NOT saved in main.fbasic or any '
                    + 'project file, so do NOT read a file for it. Work from this exact code:\n\n```\n'
                    + priorCode + '\n```',
            });
        } else if (userWantsCode(userText)) {
            docsPrefetched = await this.routeCodeRequest(userText, signal);
        }
        if (!docsPrefetched && shouldPrefetchDocs(userText)) {
            docsPrefetched = await this.prefetchSearchDocs(userText, signal);
        }

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
        // Research-first discipline whenever the user wants code written/shown.
        if (userWantsCode(userText)) sections.push(CODE_RESEARCH_PROTOCOL);
        sections.push(FADE_RULES);
        // Retained-mode / sync-driven runtime rules — only for MonoGame games,
        // where the model otherwise invents "clear the screen" / "wait" steps.
        if (this.getProjectType?.() === 'monogame') sections.push(MONOGAME_RULES);
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
        // Self-heal: when the final answer SHOWS invalid Fade code, repair it in
        // an ISOLATED sub-agent (its own context: the draft + the errors + the
        // rules) and splice the corrected answer back, leaving this conversation
        // clean. Run at most once per turn.
        let snippetHealUsed = false;
        // For the end-of-turn analysis (asset check + next-step suggestion).
        let finalText = '';
        const toolsUsed = new Set<string>();
        if (docsPrefetched) toolsUsed.add('search_docs');
        if (listFilesPrefetched) toolsUsed.add('list_files');

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
                    for (const c of toolCalls) toolsUsed.add(c.name);
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

                // Self-heal: the answer is about to become final, but if it
                // SHOWS invalid Fade (wrong syntax, missing parens, or commands
                // that don't exist — none of which went through code review,
                // since it's chat text not a write tool), feed the exact errors
                // back and let the model correct before the user ever sees it.
                // Bounded so a model that can't fix it still terminates.
                if (text.length > 0 && !snippetHealUsed) {
                    const lint = await this.lintAnswerSnippets(text);
                    if (lint.issues.length > 0 || lint.notes.length > 0) {
                        snippetHealUsed = true;
                        const n = lint.issues.length + lint.notes.length;
                        const detail = [
                            ...lint.notes,
                            ...lint.issues.map(d => `L${d.line}: ${d.message}${d.code ? ` (${d.code})` : ''}`),
                        ].join('\n');
                        log.agent.warn(`invalid Fade in answer — repairing in sub-agent (${n} problem(s))`);
                        this.emit({
                            kind: 'reasoning',
                            title: `Found ${n} problem(s) in my code — fixing in an isolated pass`,
                            detail,
                        });
                        const fixed = await this.repairAnswer(text, signal);
                        if (fixed && fixed.trim() && fixed !== text) {
                            this.emit({ kind: 'reasoning', title: 'Code repaired ✓' });
                            this.replaceLastAssistantMessage(fixed);
                            this.emit({ kind: 'answer_revised', text: fixed });
                            finalText = fixed;
                            break;
                        }
                        this.emit({
                            kind: 'reasoning',
                            title: 'Could not fully fix the code — flagging the remaining errors',
                        });
                        // Restore the original answer in the bubble (the live
                        // repair stream left the last failed attempt there) so it
                        // matches history and the passive lint notice below.
                        this.emit({ kind: 'answer_revised', text });
                    }
                }

                finalText = text;
                break;
            }

            if (iteration >= this.maxIterations) {
                log.agent.warn(`hit max iterations (${this.maxIterations})`);
                this.emit({
                    kind: 'error',
                    message: `Stopped after ${this.maxIterations} tool iterations.`,
                });
            }

            // End-of-turn analysis: check the answer's code for asset usage,
            // and offer a concrete next step. Best-effort — never blocks or
            // fails the turn.
            if (lastFinishReason === 'stop' && finalText) {
                try { await this.analyzeTurn(finalText, toolsUsed); }
                catch (e) { log.agent.warn(`turn analysis failed: ${(e as Error).message}`); }
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

    /** Lint the Fade code blocks SHOWN in an answer (chat text, not write
     *  tools — so they never hit code review). Combines the command-list-aware
     *  deterministic detectors (missing parens, invented commands, cross-language
     *  syntax — each with a crisp fix) with the LSP, which is authoritative for
     *  statement-form unknown commands like `cls`, `delay`, `draw sprite`.
     *  Returns the rule violations (`notes`) and compile errors (`issues`);
     *  both empty means the code is clean. */
    private async lintAnswerSnippets(text: string): Promise<{
        issues: Array<{ line: number; message: string; code?: string }>;
        notes: string[];
    }> {
        const issues: Array<{ line: number; message: string; code?: string }> = [];
        const notes = new Set<string>();
        const cmdNames = await this.commandNames();

        for (const block of extractCodeBlocks(text)) {
            const nonEmpty = block.split('\n').filter(l => l.trim()).length;
            if (nonEmpty === 0) continue;
            for (const n of detectFadeAntiPatterns(block)) notes.add(n);
            for (const n of detectMissingCallParens(block, cmdNames)) notes.add(n);
            for (const n of detectUnknownCommands(block, cmdNames)) notes.add(n);
            for (const n of detectCommandAsVariable(block, cmdNames)) notes.add(n);
            if (this.toolContext.lintFadeSnippet) {
                try {
                    const diags = await this.toolContext.lintFadeSnippet(block);
                    for (const d of diags) {
                        if (d.severity !== 'error') continue;
                        const unknownSymbol = d.code === '0200'
                            || /unknown symbol|invalid reference/i.test(d.message);
                        // A single illustrative line referencing a symbol defined
                        // elsewhere isn't really an error — skip that one case.
                        if (nonEmpty <= 1 && unknownSymbol) continue;
                        issues.push({ line: d.line, message: d.message, code: d.code });
                        if (issues.length >= 8) break;
                    }
                } catch { /* LSP unavailable — fall back to the detectors */ }
            }
            if (issues.length >= 8) break;
        }
        return { issues, notes: [...notes] };
    }

    /** True when the user's message points back at code from a PRIOR reply
     *  ("the code you showed", "fix that snippet", "the previous example") rather
     *  than at a project file. */
    private referencesPriorOutput(userText: string): boolean {
        const q = userText.toLowerCase();
        if (/\byou (just )?(wrote|showed|gave|made|posted|generated|provided|shared)\b/.test(q)) return true;
        return /\b(that|this|the|your|previous|earlier|last|above)\b[^.?!]*\b(code|snippet|example|program|script|version|function)\b/.test(q);
    }

    /** The first fenced code block from the most recent assistant reply, or null
     *  if the last assistant turn showed no code. Used to resolve "the code"
     *  references to chat output instead of a file. */
    private lastAssistantCodeBlock(): string | null {
        for (let i = this.history.length - 1; i >= 0; i--) {
            const m = this.history[i];
            if (m.role !== 'assistant') continue;
            const blocks = extractCodeBlocks(m.content);
            if (blocks.length > 0) return blocks[0];
            // Only consult the single most recent assistant turn.
            return null;
        }
        return null;
    }

    /** Replace the most recent assistant message in history (used to splice a
     *  sub-agent-repaired answer back in, so the conversation records the fixed
     *  version, not the broken draft). */
    private replaceLastAssistantMessage(content: string): void {
        for (let i = this.history.length - 1; i >= 0; i--) {
            if (this.history[i].role === 'assistant') {
                this.history[i] = { role: 'assistant', content };
                return;
            }
        }
    }

    /** Isolated code-fixer SUB-AGENT. Runs in its own throwaway context (the
     *  draft answer + its errors + the full rules + command list) and loops
     *  until the code is valid or the budget is spent — none of this churn
     *  touches the main conversation. Returns the corrected answer, or null if
     *  it couldn't make it valid. The caller splices the result back and resumes
     *  the main flow with a clean history. */
    private async repairAnswer(answer: string, signal: AbortSignal): Promise<string | null> {
        const sysParts = [
            'You are a Fade BASIC code FIXER. You are given a draft answer whose code is INVALID. '
            + 'Rewrite the WHOLE answer so the code compiles and follows every rule below — change only '
            + 'what is needed, keep the prose and intent. Output ONLY the corrected answer (prose + '
            + 'fenced code), no preamble, no explanation of the fixes.',
            FADE_RULES,
        ];
        if (this.getProjectType?.() === 'monogame') sysParts.push(MONOGAME_RULES);
        const catalog = await this.buildCommandCatalogBlock();
        if (catalog) sysParts.push(catalog);
        const system = { role: 'system' as const, content: sysParts.join('\n\n') };

        let draft = answer;
        for (let attempt = 1; attempt <= MAX_REPAIR_PASSES; attempt++) {
            if (signal.aborted) return null;
            const lint = await this.lintAnswerSnippets(draft);
            if (lint.issues.length === 0 && lint.notes.length === 0) return draft;

            const errs = [
                ...lint.notes,
                ...lint.issues.map(d => `L${d.line}: ${d.message}${d.code ? ` (${d.code})` : ''}`),
            ];
            this.emit({
                kind: 'reasoning',
                title: `Repairing the code (isolated pass ${attempt})`,
                detail: errs.join('\n'),
            });

            const user = {
                role: 'user' as const,
                content:
                    'This draft has invalid Fade code. Problems:\n'
                    + errs.map(e => `- ${e}`).join('\n')
                    + '\n\n--- draft answer ---\n' + draft
                    + '\n--- end draft ---\n\nRewrite the full answer with the code fixed. '
                    + 'For any command you are unsure of, use one from the command list. Output only the corrected answer.',
            };

            // Stream the rewrite live into the answer bubble so the user watches
            // the fix happen (clear the prior text first), and tick the gen bar.
            this.emit({ kind: 'revising' });
            let out = '';
            try {
                for await (const ev of this.provider.stream({
                    messages: [system, user],
                    maxTokens: 1536,
                    temperature: 0.1,
                    signal,
                })) {
                    if (ev.kind === 'text') {
                        out += ev.delta;
                        this.emit({ kind: 'model_token', delta: ev.delta });
                        this.emit({ kind: 'text_delta', delta: ev.delta });
                    }
                    if (ev.kind === 'done') break;
                }
            } catch (e) {
                log.agent.debug(`repair sub-agent failed: ${(e as Error).message}`);
                return null;
            }
            if (!out.trim()) return null;
            draft = out;
        }

        // Final check after the last rewrite.
        const finalLint = await this.lintAnswerSnippets(draft);
        return (finalLint.issues.length === 0 && finalLint.notes.length === 0) ? draft : null;
    }

    /** Post-turn analysis: surface asset-usage problems in the answer and
     *  offer a concrete next step. Emits `asset_report` and/or `suggestion`.
     *  Purely additive — failures are swallowed by the caller. */
    private async analyzeTurn(finalText: string, toolsUsed: Set<string>): Promise<void> {
        const suggestions: TurnSuggestion[] = [];

        // 1. Asset usage: does the answer's code reference assets the project
        //    doesn't have? Call it out, and offer to find them in the Catalog.
        try {
            const files = await this.toolContext.workspace.list();
            const { present, missing } = checkAssetRefs(finalText, files);
            if (present.length || missing.length) {
                this.emit({ kind: 'asset_report', present, missing });
            }
            if (missing.length > 0 && this.toolContext.catalog) {
                const first = missing[0];
                suggestions.push({
                    title: `Find “${first.name}” in the Catalog`,
                    prompt: `The snippet uses the ${first.category} asset "${first.name}", which isn't `
                        + `in my project. Search the catalog for a matching ${first.category} and show me `
                        + `options to import.`,
                });
            }
        } catch { /* workspace unavailable — skip the asset check */ }

        // 1b. Lint Fade code the answer SHOWS (not applied via a write tool,
        //     so it never hit code review). We lint essentially every block —
        //     the only carve-out is "unknown symbol" on a SINGLE-line fragment,
        //     which is almost always an illustrative line referencing a symbol
        //     defined elsewhere (false positive). Everything else — syntax
        //     errors like `elseif`, bad overloads, multi-line blocks — is
        //     flagged regardless of length.
        // Self-heal (above) already bounced invalid snippets back to the model.
        // If errors REMAIN after that budget, surface them passively so the
        // user isn't misled into thinking the shown code compiles.
        try {
            const { issues } = await this.lintAnswerSnippets(finalText);
            if (issues.length > 0) {
                this.emit({ kind: 'code_lint', issues });
                suggestions.push({
                    title: 'Fix the snippet errors',
                    prompt: 'The code you showed has Fade compile errors. Fix them — '
                        + 'call search_docs for any command you are unsure about — and show the corrected code.',
                });
            }
        } catch { /* LSP unavailable — skip */ }

        // 2. Speculation: did the answer guess about a real Fade command
        //    instead of reading the docs? Offer to look it up.
        if (!toolsUsed.has('search_docs')) {
            const cmd = await this.detectSpeculatedCommand(finalText);
            if (cmd) {
                suggestions.push({
                    title: `Look up the \`${cmd}\` command`,
                    prompt: `Search the docs for the "${cmd}" command and explain what it actually does `
                        + `and how to use it.`,
                });
            }
        }

        // 3. Always keep the conversation moving: add context-derived
        //    next-steps so there's never a dead end. Specific suggestions
        //    (above) take priority; these fill in to 3 total.
        const editApplied = toolsUsed.has('apply_edit') || toolsUsed.has('create_file');
        const hasCode = /```/.test(finalText) || /\b(texture|sprite|print|function|for|if)\b/.test(finalText);
        const forward: TurnSuggestion[] = [];
        if (editApplied) {
            forward.push({ title: 'Run it to test', prompt: 'How do I run the project to test this change?' });
            forward.push({ title: 'What should I add next?', prompt: 'What is a good next feature or improvement to add?' });
        } else if (hasCode) {
            forward.push({ title: 'Add this to my project', prompt: 'Add that code to my project in the right place.' });
            forward.push({ title: 'Explain how it works', prompt: 'Walk me through how that code works, step by step.' });
        } else {
            forward.push({ title: 'Show a working example', prompt: 'Show me a complete working example I can run.' });
            forward.push({ title: 'How do I use this?', prompt: 'How would I use that in my current project?' });
        }
        forward.push({ title: 'What should I do next?', prompt: 'Given all that, what is the single best next step?' });

        // Merge, dedupe by title, cap at 3.
        const seen = new Set(suggestions.map(s => s.title));
        for (const f of forward) {
            if (suggestions.length >= 3) break;
            if (seen.has(f.title)) continue;
            seen.add(f.title);
            suggestions.push(f);
        }

        if (suggestions.length > 0) {
            this.emit({ kind: 'suggestion', suggestions });
        }
    }

    /** Cached Fade command names (or [] if unavailable). */
    private async commandNames(): Promise<string[]> {
        if (!this.getCommandNames) return [];
        if (!this.cachedCommandNames) {
            try { this.cachedCommandNames = await this.getCommandNames(); }
            catch { this.cachedCommandNames = []; }
        }
        return this.cachedCommandNames ?? [];
    }

    /** If the answer hedges ("likely", "probably", "is a command that…")
     *  about a token that is actually a known Fade command, return that
     *  command name so we can offer a docs lookup. Null otherwise. */
    private async detectSpeculatedCommand(text: string): Promise<string | null> {
        const names = await this.commandNames();
        if (names.length === 0) return null;

        const lower = text.toLowerCase();
        const HEDGE = /(likely|probably|presumably|might\s+be|may\s+be|i\s+think|i\s+believe|appears?\s+to\s+be|seems?\s+to\s+be|not\s+(?:entirely\s+)?sure|i'?m\s+not\s+sure|is\s+(?:likely\s+)?a\s+command|guess)/;
        if (!HEDGE.test(lower)) return null;

        for (const name of names) {
            const n = name.toLowerCase();
            if (n.length < 3) continue; // skip 1-2 char names → too many false hits
            const idx = lower.indexOf(n);
            if (idx < 0) continue;
            const boundaryBefore = idx === 0 || /[^a-z0-9_]/.test(lower[idx - 1]);
            const boundaryAfter = idx + n.length >= lower.length || /[^a-z0-9_]/.test(lower[idx + n.length]);
            if (!boundaryBefore || !boundaryAfter) continue;
            // Require the hedge to sit near the command mention, not anywhere.
            const window = lower.slice(Math.max(0, idx - 60), Math.min(lower.length, idx + n.length + 60));
            if (HEDGE.test(window)) return name;
        }
        return null;
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
            if (names.length === 0) {
                // The model has NO authoritative command list — invented
                // commands (`cls`, `delay`, `draw sprite`) are then very likely.
                // Surface it loudly: usually means the LSP worker isn't ready or
                // the project's commandDlls didn't load.
                log.context.warn(
                    'command catalog is EMPTY (getCommandNames returned 0) — the model has no '
                    + 'authoritative command list, so it will invent commands. Check the LSP worker / '
                    + 'project commandDlls.',
                );
                return null;
            }
            // List the FULL command set — local models have the context room,
            // and a complete authoritative list is the single best defence
            // against invented command names (e.g. guessing `KeyDown` when the
            // real input command is something else). Names only (cheap); the
            // model calls search_docs for signatures/usage of a specific one.
            return (
                `These are ALL ${names.length} commands that exist in this Fade project. `
                + 'This list is authoritative and complete:\n'
                + names.join(', ') + '\n\n'
                + 'HARD RULES:\n'
                + '- Use ONLY names from this list. A name not in the list DOES NOT EXIST '
                + '(there is no `cls`, `delay`, `draw sprite`, `wend`, etc. unless listed).\n'
                + '- Before using ANY command you are not 100% certain of, call search_docs for it FIRST '
                + 'to confirm it exists and get its exact signature — do not guess arguments.\n'
                + '- A command that returns a value is called with parentheses (`rightKey()`).\n'
                + '- If what you want is not in the list, pick the closest listed command or search_docs; never invent one.'
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

    /** Run a tool BEFORE the model turn with full visibility — emit the
     *  tool_call start/result events (so the UI shows a row) and push the call +
     *  result into history so the model sees it. The driven actions of the
     *  router's decision branches. */
    private async runVisibleTool(name: string, args: Record<string, unknown>, signal: AbortSignal): Promise<boolean> {
        if (signal.aborted) return false;
        const id = `route-${name}-${++this.routeSeq}`;
        this.emit({ kind: 'tool_call_start', id, name, args });
        log.tool.info(`route ${name} ${JSON.stringify(args).slice(0, 80)}`);
        let ok = false;
        let result: unknown;
        try {
            const r = await this.tools.run(name, args, this.toolContext);
            ok = r.ok;
            result = r.result;
        } catch (e) {
            result = { error: (e as Error).message };
        }
        this.emit({ kind: 'tool_call_result', id, name, ok, result });
        this.history.push({ role: 'assistant', content: `<tool_call>${JSON.stringify({ name, args })}</tool_call>` });
        this.history.push({ role: 'user', content: renderToolResult(name, result) });
        if (name === 'search_docs' && ok && result && typeof result === 'object' && 'hits' in (result as object)) {
            const hits = (result as { hits?: SearchHit[] }).hits;
            if (hits?.length) this.emit({ kind: 'docs_retrieved', query: String(args.query ?? ''), hits });
        }
        return ok;
    }

    /** Decision node: classify a code request into an intent and a plan of what
     *  to gather first. One model call, tolerantly parsed (small models format
     *  loosely). If the model just lists phrases (no labels), they're treated as
     *  capabilities and the intent falls back to a heuristic — so a bare list
     *  still drives a research pass. */
    private async classifyCodeRoute(userText: string, signal: AbortSignal): Promise<CodeRoutePlan> {
        const heuristicIntent: CodeIntent = /\b(error|crash|broken|doesn'?t work|not working|bug|fix the|wrong|fails?)\b/i.test(userText)
            ? 'debug'
            : userRequestedCodeChange(userText) ? 'edit_code' : 'write_code';
        const fallback: CodeRoutePlan = { intent: heuristicIntent, files: [], capabilities: [] };
        if (signal.aborted) return fallback;

        let knownFiles: string[] = [];
        try { knownFiles = await this.toolContext.workspace.list(); }
        catch { /* no workspace listing — the model just won't be grounded on filenames */ }

        const sys =
            'You are the ROUTER for a Fade BASIC coding assistant. Plan what to gather BEFORE writing code. '
            + 'Reply in EXACTLY this format, nothing else:\n'
            + 'INTENT: write_code | edit_code | debug\n'
            + 'FILES: comma-separated existing filenames to read first, or none\n'
            + 'CAPABILITIES: semicolon-separated short phrases, one per command/action the code needs '
            + '(e.g. read the arrow keys; draw a sprite), or none\n\n'
            + 'write_code = new code/demo (CAPABILITIES set, FILES usually none). '
            + 'edit_code = change existing code (FILES = the file to read; CAPABILITIES for new behavior). '
            + 'debug = something errors (FILES = the relevant file).'
            + (knownFiles.length ? `\n\nProject files: ${knownFiles.join(', ')}` : '');

        let text = '';
        try {
            for await (const ev of this.provider.stream({
                messages: [{ role: 'system', content: sys }, { role: 'user', content: userText }],
                maxTokens: 200,
                temperature: 0,
                signal,
            })) {
                if (ev.kind === 'text') text += ev.delta;
                if (ev.kind === 'done') break;
            }
        } catch (e) {
            log.agent.debug(`route classification failed: ${(e as Error).message}`);
            return fallback;
        }

        const grab = (label: string): string | null => {
            const m = new RegExp(`^\\s*${label}\\s*:\\s*(.+)$`, 'im').exec(text);
            return m ? m[1].trim() : null;
        };
        const clean = (s: string) => s.replace(/^[-*•\d.)\s]+/, '').trim();
        const splitList = (s: string | null, sep: RegExp) =>
            (s && !/^none$/i.test(s.trim()))
                ? s.split(sep).map(clean).filter(x => x.length >= 2 && !/[{}<>]/.test(x))
                : [];

        const intentRaw = (grab('INTENT') ?? '').toLowerCase();
        const intent: CodeIntent =
            intentRaw.includes('debug') ? 'debug'
            : intentRaw.includes('edit') ? 'edit_code'
            : intentRaw.includes('write') ? 'write_code'
            : heuristicIntent;

        const filesLine = grab('FILES');
        const capsLine = grab('CAPABILITIES');
        let files = splitList(filesLine, /[,\n]/).filter(f => knownFiles.length === 0 || knownFiles.includes(f));
        let capabilities = splitList(capsLine, /[;\n]/);

        // No labels at all → treat the whole reply as a bare capability list
        // (keeps the simple "one phrase per line" behavior working).
        if (filesLine === null && capsLine === null && grab('INTENT') === null) {
            capabilities = text.split('\n').map(clean)
                .filter(l => l.length >= 3 && l.length <= 60 && !l.startsWith('```') && !/[{}<>]/.test(l));
        }

        return {
            intent,
            files: [...new Set(files)].slice(0, 3),
            capabilities: [...new Set(capabilities)].slice(0, 4),
        };
    }

    /** Outer decision-tree branch for code requests: classify the intent, show
     *  a visible plan, then drive the branch's actions — read the target file(s)
     *  for edits/debug, fetch diagnostics for debug, and research each command
     *  via search_docs — all BEFORE the model writes a line. Returns true if it
     *  prepared anything (so the caller skips the lighter single-shot prefetch). */
    private async routeCodeRequest(userText: string, signal: AbortSignal): Promise<boolean> {
        if (!this.retriever || signal.aborted) return false;
        this.emit({ kind: 'reasoning', title: 'Classifying the request…' });
        const route = await this.classifyCodeRoute(userText, signal);
        if (route.files.length === 0 && route.capabilities.length === 0) {
            this.emit({ kind: 'reasoning', title: 'No research needed — answering directly' });
            return false;
        }

        const intentLabel = route.intent === 'debug' ? 'Debug & fix'
            : route.intent === 'edit_code' ? 'Edit existing code' : 'Write new code';
        const decisionDetail = [
            `Intent: ${route.intent}`,
            route.files.length ? `Files to read: ${route.files.join(', ')}` : null,
            route.capabilities.length ? `Commands to research: ${route.capabilities.join(', ')}` : null,
        ].filter(Boolean).join('\n');
        this.emit({ kind: 'reasoning', title: `Approach — ${intentLabel}`, detail: decisionDetail });

        const goal =
            route.intent === 'debug' ? 'Read the code + diagnostics, then fix the problem'
            : route.intent === 'edit_code' ? 'Read the target file and research commands, then edit'
            : 'Research the commands needed, then write the code';
        const steps: AgentPlan['steps'] = [
            ...route.files.map(f => ({ tool: 'read_file', description: f })),
            ...(route.intent === 'debug' ? [{ tool: 'get_diagnostics', description: 'current errors' }] : []),
            ...route.capabilities.map(c => ({ tool: 'search_docs', description: c })),
        ];
        log.agent.info(`route=${route.intent} files=[${route.files.join(',')}] caps=[${route.capabilities.join(' | ')}]`);
        this.emit({ kind: 'plan_emitted', plan: { goal, steps } });

        for (const f of route.files) {
            if (signal.aborted) break;
            await this.runVisibleTool('read_file', { path: f }, signal);
        }
        if (route.intent === 'debug' && !signal.aborted) {
            await this.runVisibleTool('get_diagnostics', {}, signal);
        }
        for (const cap of route.capabilities) {
            if (signal.aborted) break;
            await this.prefetchSearchDocs(cap, signal);
        }

        const tail =
            route.intent === 'debug'
                ? 'The file and its diagnostics are above. Fix the specific errors with apply_edit. '
                : route.intent === 'edit_code'
                    ? 'The file is above. Make the change with apply_edit on it. '
                    : '';
        // Re-inject the FULL rule set RIGHT BEFORE the model writes code — it's
        // the most salient position in the prompt, and the model otherwise
        // misreads the rules on the first attempt despite them being in the
        // (now distant) system prompt.
        const ruleParts = [FADE_RULES];
        if (this.getProjectType?.() === 'monogame') ruleParts.push(MONOGAME_RULES);
        this.history.push({
            role: 'user',
            content:
                tail
                + 'Before writing, RE-READ these rules and follow EVERY one:\n\n'
                + ruleParts.join('\n\n')
                + '\n\nUse ONLY commands confirmed in the docs above or the authoritative command list. '
                + 'If you need another command you have not researched, call search_docs for it FIRST — '
                + 'never guess a command name or its arguments.',
        });
        return true;
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

        // When an edit was rejected (compile errors), look up the real docs
        // for the commands the model used near the failure and feed them back
        // — the LSP error alone ("token not in parens") rarely tells the model
        // the actual signature, so it keeps guessing. This injects the truth.
        for (const w of writes) {
            const r = results[w.idx];
            if (r && !r.ok) await this.injectDocsForRejectedEdit(w.call, r.result);
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

    /** After an edit is rejected by code review, pull the real docs for the
     *  commands the model used (it usually guessed them) and push them into
     *  history so the next attempt has correct signatures. No-op when there's
     *  no retriever, no code, or nothing command-like to look up. */
    private async injectDocsForRejectedEdit(
        call: { name: string; args: unknown },
        result: unknown,
    ): Promise<void> {
        if (!this.retriever) return;
        const res = result as Record<string, unknown> | null;
        // Only for compile/review rejections (have `review` text), not e.g.
        // "user rejected" or "file not found".
        if (!res || typeof res.review !== 'string') return;
        const args = (call.args ?? {}) as Record<string, unknown>;
        const code = typeof args.newText === 'string' ? args.newText
            : typeof args.content === 'string' ? args.content : '';
        if (!code) return;

        const parts: string[] = [];

        // 1. Translate known "wrote it like another language" mistakes in the
        //    rejected code into specific Fade corrections, right next to the
        //    error (the LSP codes alone don't say "endfunction is one word",
        //    or that a bare value-returning command needs parens). We read the
        //    actual code + command list, ignoring the misleading error code.
        const cmdNames = await this.commandNames();
        const antiPatterns = [
            ...detectFadeAntiPatterns(code),
            ...detectMissingCallParens(code, cmdNames),
            ...detectUnknownCommands(code, cmdNames),
            ...detectCommandAsVariable(code, cmdNames),
        ];
        if (antiPatterns.length > 0) {
            parts.push('Fix these Fade syntax mistakes in your edit:\n'
                + antiPatterns.map(s => `- ${s}`).join('\n'));
        }

        // 2. Look up the real docs for the commands the model used (it usually
        //    guessed them) so the next attempt has correct signatures.
        const phrases = extractCommandPhrases(code).slice(0, 4);
        const hits: SearchHit[] = [];
        if (phrases.length > 0) {
            const seen = new Set<string>();
            for (const phrase of phrases) {
                try {
                    const found = await this.retriever.search(phrase, 2, { projectType: this.getProjectType?.() });
                    for (const h of found) {
                        const key = `${h.chunk.source}#${h.chunk.heading}`;
                        if (!seen.has(key)) { seen.add(key); hits.push(h); }
                    }
                } catch { /* ignore a single failed lookup */ }
            }
            if (hits.length > 0) {
                this.emit({ kind: 'docs_retrieved', query: phrases.join(', '), hits });
                parts.push(
                    'Docs for the commands in your rejected edit — use these EXACT commands and '
                    + 'signatures; if a command you used is not here, it may not exist:\n' + formatHits(hits),
                );
            }
        }

        if (parts.length === 0) return;
        this.history.push({ role: 'user', content: parts.join('\n\n') });
        log.rag.info(`rejected-edit feedback: ${antiPatterns.length} anti-pattern(s), ${phrases.length} command lookup(s)`);
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
            // Throughput tick for the UI — counts every token, including the
            // ones that get parsed into tool calls (so the rate doesn't look
            // frozen during a tool-heavy turn).
            if (ev.kind === 'text') this.emit({ kind: 'model_token', delta: ev.delta });
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
