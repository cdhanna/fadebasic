// Context-window management. Three strategies, applied in order, each
// gated by "still over budget?":
//
//   1. Elision     — replace old <tool_result> bodies with a stub so the
//                    call-record stays but the payload doesn't burn tokens
//   2. Summary     — fold old turns into a single [SUMMARY] system message
//                    (uses the active ChatProvider for a one-shot generation)
//   3. Hard drop   — remove the oldest user/assistant pair entirely
//
// Eviction MUTATES `history` in place. Once a tool result body is dropped or
// a turn is summarized, it's gone — saved chats reflect the evicted state.
// In exchange we get a tight implementation that doesn't have to re-evict
// every turn from scratch.
//
// All eviction work funnels through evictToBudget(); the agent calls that
// once per iteration before sending to the model.

import { getLogger } from '../log-bus';
import type { ChatProvider, Msg } from './providers/types';

const log = getLogger('ai/context');

/** Marker prefix on summary system messages. Lets the orchestrator find
 *  the existing summary (if any) on the next eviction pass and fold it
 *  into a fresh summary. */
export const SUMMARY_PREFIX = '[SUMMARY]\n';

/** Matches the start of a <tool_result> user message. We use this as the
 *  marker for "this is an evictable tool result, not user prose." */
const TOOL_RESULT_RE = /^<tool_result name="([^"]+)">/;
const ELIDED_RE = /^<tool_result name="([^"]+)">\.\.\.elided\b/;

export interface EvictionOptions {
    /** Provider — used for tokenization (cheap) and summarization (one
     *  extra generation call when Strategy 2 fires). */
    provider: ChatProvider;
    /** Fraction of maxContext at which to start evicting. Default 0.7. */
    evictAt?: number;
    /** Target fraction to evict down to. Default 0.5. Leaves headroom so
     *  the next turn doesn't immediately re-evict. */
    evictTo?: number;
    /** Don't touch the last N messages — they're current context the model
     *  needs intact. Default 4 (roughly the last two turn pairs). */
    keepRecentMessages?: number;
    /** Disable Strategy 2 (summarization). Useful for tests or when the
     *  user wants strictly lossless behavior — eviction falls through to
     *  hard-drop instead. Default true. */
    enableSummarization?: boolean;
    /** Max tokens the summary is allowed to use. Default 400. */
    summaryMaxTokens?: number;
}

export interface EvictionResult {
    /** Tokens evicted across all strategies. */
    saved: number;
    elided: number;        // # of tool results elided
    summarized: number;    // # of messages folded into a summary
    dropped: number;       // # of messages hard-dropped
    /** Stops as soon as we're under target, even if we could do more. */
    underTarget: boolean;
}

/** Listener for fine-grained eviction phase events — surfaced to the UI. */
export type EvictionListener = (ev: EvictionEvent) => void;

export type EvictionEvent =
    | { kind: 'started'; tokens: number; max: number; ratio: number }
    | { kind: 'elided'; count: number; savedTokens: number }
    | { kind: 'summarized'; messagesReplaced: number; summary: string }
    | { kind: 'dropped'; count: number }
    | { kind: 'done'; tokens: number; max: number; ratio: number };

const DEFAULTS = {
    evictAt: 0.7,
    evictTo: 0.5,
    keepRecentMessages: 4,
    enableSummarization: true,
    summaryMaxTokens: 400,
};

export class ContextEvictor {
    private readonly opts: Required<EvictionOptions>;
    private listeners = new Set<EvictionListener>();

    constructor(opts: EvictionOptions) {
        this.opts = {
            provider: opts.provider,
            evictAt: opts.evictAt ?? DEFAULTS.evictAt,
            evictTo: opts.evictTo ?? DEFAULTS.evictTo,
            keepRecentMessages: opts.keepRecentMessages ?? DEFAULTS.keepRecentMessages,
            enableSummarization: opts.enableSummarization ?? DEFAULTS.enableSummarization,
            summaryMaxTokens: opts.summaryMaxTokens ?? DEFAULTS.summaryMaxTokens,
        };
    }

    on(listener: EvictionListener): () => void {
        this.listeners.add(listener);
        return () => { this.listeners.delete(listener); };
    }

    private emit(ev: EvictionEvent): void {
        for (const l of this.listeners) {
            try { l(ev); } catch (e) { console.error('[context] listener threw', e); }
        }
    }

    /** Total token cost of system + history under the current provider. */
    countTokens(systemMsg: Msg, history: Msg[]): number {
        let total = this.opts.provider.countTokens(systemMsg.content);
        for (const m of history) total += this.opts.provider.countTokens(m.content);
        return total;
    }

    /** Run the eviction ladder until under the evictTo target (or out of
     *  things to evict). Mutates `history` in place. Returns a summary of
     *  what was done. */
    async evictToBudget(systemMsg: Msg, history: Msg[]): Promise<EvictionResult> {
        const max = this.opts.provider.capabilities.maxContext;
        const result: EvictionResult = { saved: 0, elided: 0, summarized: 0, dropped: 0, underTarget: true };
        if (!max) return result;

        const before = this.countTokens(systemMsg, history);
        if (before / max < this.opts.evictAt) return result;

        const target = max * this.opts.evictTo;
        this.emit({ kind: 'started', tokens: before, max, ratio: before / max });
        log.warn(`eviction triggered: ${before}/${max} (${((before / max) * 100).toFixed(0)}%) → target ${Math.round(target)}`);

        // Strategy 1: elide old tool-result bodies.
        let elidedCount = 0;
        let savedByElision = 0;
        while (this.countTokens(systemMsg, history) > target) {
            const saved = this.elideOldestToolResult(history);
            if (saved === 0) break;
            elidedCount++;
            savedByElision += saved;
        }
        if (elidedCount > 0) {
            log.info(`elided ${elidedCount} tool result(s), saved ~${savedByElision} tokens`);
            this.emit({ kind: 'elided', count: elidedCount, savedTokens: savedByElision });
            result.elided = elidedCount;
        }

        // Strategy 2: summarize old turns.
        if (this.opts.enableSummarization && this.countTokens(systemMsg, history) > target) {
            const sumResult = await this.summarizeOldHistory(history);
            if (sumResult) {
                log.info(`summarized ${sumResult.messagesReplaced} message(s) into ${sumResult.summary.length} chars`);
                this.emit({
                    kind: 'summarized',
                    messagesReplaced: sumResult.messagesReplaced,
                    summary: sumResult.summary,
                });
                result.summarized = sumResult.messagesReplaced;
            }
        }

        // Strategy 3: hard drop oldest pair(s).
        let droppedCount = 0;
        while (this.countTokens(systemMsg, history) > target) {
            const dropped = this.dropOldestPair(history);
            if (dropped === 0) break;
            droppedCount += dropped;
        }
        if (droppedCount > 0) {
            log.info(`hard-dropped ${droppedCount} message(s)`);
            this.emit({ kind: 'dropped', count: droppedCount });
            result.dropped = droppedCount;
        }

        const after = this.countTokens(systemMsg, history);
        result.saved = before - after;
        result.underTarget = after <= target;
        this.emit({ kind: 'done', tokens: after, max, ratio: after / max });
        log.info(`eviction complete: ${before} → ${after} tokens (saved ${result.saved})`);

        return result;
    }

    // ─── Strategy 1: elision ────────────────────────────────────────────────

    /** Find the oldest <tool_result> message outside the keep-recent window
     *  and replace its body with a stub. Returns tokens saved (0 if nothing
     *  to elide). */
    private elideOldestToolResult(history: Msg[]): number {
        const keepFromIdx = Math.max(0, history.length - this.opts.keepRecentMessages);
        for (let i = 0; i < keepFromIdx; i++) {
            const m = history[i];
            if (m.role !== 'user') continue;
            if (ELIDED_RE.test(m.content)) continue;   // already elided
            const match = TOOL_RESULT_RE.exec(m.content);
            if (!match) continue;

            const toolName = match[1];
            const originalTokens = this.opts.provider.countTokens(m.content);
            const elided = `<tool_result name="${toolName}">...elided (${m.content.length} chars)...</tool_result>`;
            const newTokens = this.opts.provider.countTokens(elided);
            if (newTokens >= originalTokens) continue;  // no point eliding empty body

            history[i] = { ...m, content: elided };
            return originalTokens - newTokens;
        }
        return 0;
    }

    // ─── Strategy 2: summarization ──────────────────────────────────────────

    /** Fold all-but-the-last-N messages into a single [SUMMARY] system msg.
     *  If a previous [SUMMARY] exists, its content is included as input so
     *  context accumulates across multiple eviction passes.
     *  Returns null if there's nothing summarizable. */
    private async summarizeOldHistory(history: Msg[]): Promise<{
        messagesReplaced: number;
        summary: string;
    } | null> {
        const keepFromIdx = Math.max(0, history.length - this.opts.keepRecentMessages);
        // Need at least 2 messages older than the keep window to bother.
        if (keepFromIdx < 2) return null;

        const oldSlice = history.slice(0, keepFromIdx);
        // If everything in the old slice is a summary plus very little, skip.
        const nonSummaryCount = oldSlice.filter(m => !(m.role === 'system' && m.content.startsWith(SUMMARY_PREFIX))).length;
        if (nonSummaryCount === 0) return null;

        const summary = await this.generateSummary(oldSlice);
        if (!summary) return null;

        const summaryMsg: Msg = {
            role: 'system',
            content: SUMMARY_PREFIX + summary,
        };
        history.splice(0, keepFromIdx, summaryMsg);
        return { messagesReplaced: oldSlice.length, summary };
    }

    /** One-shot summary generation. We bypass the agent's tool/plan
     *  protocol — pure text in, pure text out. */
    private async generateSummary(messages: Msg[]): Promise<string | null> {
        const transcript = messages.map(formatForSummary).join('\n\n');
        const prompt: Msg[] = [
            {
                role: 'system',
                content: 'You compress conversations into terse summaries. Keep only what later turns need: what was decided, what files were touched, what is still open. Bullets are fine. Under 200 words. No <tool_call> or <plan> blocks — plain text only.',
            },
            {
                role: 'user',
                content: `Summarize this conversation so far:\n\n${transcript}\n\nSummary:`,
            },
        ];

        let text = '';
        try {
            for await (const ev of this.opts.provider.stream({
                messages: prompt,
                maxTokens: this.opts.summaryMaxTokens,
                temperature: 0.2,
            })) {
                if (ev.kind === 'text') text += ev.delta;
            }
        } catch (e) {
            log.warn(`summary generation failed: ${(e as Error).message}`);
            return null;
        }
        const trimmed = text.trim();
        return trimmed.length > 0 ? trimmed : null;
    }

    // ─── Strategy 3: hard drop ──────────────────────────────────────────────

    /** Drop the oldest message (and its paired follow-up if present).
     *  Returns the number of messages removed. Refuses to drop into the
     *  keep-recent window. */
    private dropOldestPair(history: Msg[]): number {
        if (history.length <= this.opts.keepRecentMessages) return 0;
        // Always preserve any leading [SUMMARY] — it represents lots of
        // already-compressed history and is cheap to keep.
        let dropIdx = 0;
        if (history[0]?.role === 'system' && history[0].content.startsWith(SUMMARY_PREFIX)) {
            dropIdx = 1;
        }
        if (history.length - 1 <= this.opts.keepRecentMessages) return 0;
        if (dropIdx >= history.length - this.opts.keepRecentMessages) return 0;

        const first = history.splice(dropIdx, 1)[0];
        let dropped = 1;
        // If the dropped message was a user message and the next one is
        // assistant, drop that too — they're a turn pair.
        if (first.role === 'user'
            && history[dropIdx]?.role === 'assistant'
            && history.length - 1 > this.opts.keepRecentMessages) {
            history.splice(dropIdx, 1);
            dropped++;
        }
        return dropped;
    }
}

function formatForSummary(m: Msg): string {
    const tag = m.role === 'system' ? 'SYSTEM'
        : m.role === 'user' ? 'USER'
        : m.role === 'assistant' ? 'ASSISTANT'
        : 'TOOL';
    return `${tag}: ${m.content}`;
}
