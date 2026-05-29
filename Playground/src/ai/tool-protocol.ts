// In-prompt protocol for structured model output. The model emits tagged
// blocks (`<plan>`, `<tool_call>`) inline with regular text; we parse them
// out of the stream and surface them as typed StreamEvents to the agent.
// Tool results are fed back as <tool_result> blocks in subsequent user
// messages.
//
// Used by every ChatProvider that doesn't have a native tools API
// (currently: all of them). Works on Qwen, Phi, Llama and most
// instruction-tuned models — they're trained on similar structured-
// invocation patterns and a few-shot example seals the deal.

import type { Msg, StreamEvent, Tool } from './providers/types';

// ─── Block types ────────────────────────────────────────────────────────────

export interface AgentPlan {
    /** One-line goal summary. */
    goal: string;
    /** Ordered list of steps the model intends to take. */
    steps: Array<{ tool?: string; description: string }>;
}

export interface ParsedToolCall {
    id: string;
    name: string;
    args: unknown;
    /** Raw JSON text between the tags — preserved for error reporting. */
    raw: string;
}

/** Stream events emitted by the protocol parser. Extends the provider-level
 *  StreamEvent with `plan` since plans are an in-prompt construct. */
export type ProtocolEvent =
    | StreamEvent
    | { kind: 'plan'; plan: AgentPlan; raw: string };

// ─── Generic tagged-block parser ────────────────────────────────────────────

const TAG_HANDLERS = {
    plan: 'plan' as const,
    tool_call: 'tool_call' as const,
};

type TagName = keyof typeof TAG_HANDLERS;

const ALL_TAGS: TagName[] = ['plan', 'tool_call'];

interface OpenTagMatch {
    tag: TagName;
    /** Index into the buffer where the tag starts (the `<`). */
    start: number;
    /** Index into the buffer just past the `>`. */
    bodyStart: number;
}

/** Stateful streaming parser. Feed it deltas as the model produces them; it
 *  yields ProtocolEvents — `text` for content outside blocks, `plan` for
 *  closed `<plan>` blocks, `tool_call` for closed `<tool_call>` blocks. */
export class BlockStreamParser {
    private buffer = '';
    private inTag: TagName | null = null;
    private callCounter = 0;

    feed(delta: string): ProtocolEvent[] {
        this.buffer += delta;
        const out: ProtocolEvent[] = [];

        while (true) {
            if (this.inTag === null) {
                const match = this.findOpenTag(this.buffer);
                if (!match) {
                    // No open tag found. Flush most of the buffer as text,
                    // but hold back enough to detect a tag that's still
                    // being streamed.
                    const safe = Math.max(0, this.buffer.length - longestTagLength());
                    if (safe > 0) {
                        out.push({ kind: 'text', delta: this.buffer.slice(0, safe) });
                        this.buffer = this.buffer.slice(safe);
                    }
                    break;
                }
                // Flush text before the tag.
                if (match.start > 0) {
                    out.push({ kind: 'text', delta: this.buffer.slice(0, match.start) });
                }
                this.buffer = this.buffer.slice(match.bodyStart);
                this.inTag = match.tag;
            } else {
                const close = `</${this.inTag}>`;
                const closeIdx = this.buffer.indexOf(close);
                if (closeIdx < 0) break;            // wait for more

                const body = this.buffer.slice(0, closeIdx);
                const wasTag = this.inTag;
                this.buffer = this.buffer.slice(closeIdx + close.length);
                this.inTag = null;

                const ev = this.parseBody(wasTag, body);
                if (ev) out.push(ev);
            }
        }

        return out;
    }

    /** Flush any remaining buffered content once the model is done. */
    end(): ProtocolEvent[] {
        const out: ProtocolEvent[] = [];
        if (this.inTag !== null) {
            out.push({ kind: 'text', delta: `\n[unclosed <${this.inTag}>: ${this.buffer}]\n` });
            this.buffer = '';
            this.inTag = null;
        } else if (this.buffer.length > 0) {
            out.push({ kind: 'text', delta: this.buffer });
            this.buffer = '';
        }
        return out;
    }

    private findOpenTag(text: string): OpenTagMatch | null {
        let best: OpenTagMatch | null = null;
        for (const tag of ALL_TAGS) {
            const open = `<${tag}>`;
            const idx = text.indexOf(open);
            if (idx < 0) continue;
            if (!best || idx < best.start) {
                best = { tag, start: idx, bodyStart: idx + open.length };
            }
        }
        return best;
    }

    private parseBody(tag: TagName, body: string): ProtocolEvent | null {
        const trimmed = body.trim();
        if (!trimmed) {
            return { kind: 'text', delta: `\n[empty <${tag}> block]\n` };
        }

        if (tag === 'tool_call') {
            const parsed = parseToolCallBody(trimmed, ++this.callCounter);
            if (!parsed.ok) {
                return { kind: 'text', delta: `\n[invalid tool call: ${parsed.error}]\n` };
            }
            return {
                kind: 'tool_call',
                id: parsed.value.id,
                name: parsed.value.name,
                args: parsed.value.args,
            };
        }

        if (tag === 'plan') {
            const parsed = parsePlanBody(trimmed);
            if (!parsed.ok) {
                return { kind: 'text', delta: `\n[invalid plan: ${parsed.error}]\n` };
            }
            return { kind: 'plan', plan: parsed.value, raw: trimmed };
        }

        return null;
    }
}

function longestTagLength(): number {
    let max = 0;
    for (const tag of ALL_TAGS) {
        const open = `<${tag}>`.length;
        const close = `</${tag}>`.length;
        if (open > max) max = open;
        if (close > max) max = close;
    }
    return max - 1; // we always keep at most (len-1) so a full open tag will be detected on the next feed
}

type ParseResult<T> = { ok: true; value: T } | { ok: false; error: string };

function parseToolCallBody(body: string, counter: number): ParseResult<ParsedToolCall> {
    let parsed: unknown;
    try {
        parsed = JSON.parse(body);
    } catch (e) {
        return { ok: false, error: `not valid JSON: ${(e as Error).message}` };
    }
    if (!parsed || typeof parsed !== 'object') {
        return { ok: false, error: 'expected JSON object' };
    }
    const obj = parsed as Record<string, unknown>;
    if (typeof obj.name !== 'string' || !obj.name) {
        return { ok: false, error: 'missing "name" string' };
    }
    const args = obj.args ?? obj.arguments ?? {};
    const id = typeof obj.id === 'string' && obj.id ? obj.id : `call_${counter}_${Date.now().toString(36)}`;
    return { ok: true, value: { id, name: obj.name, args, raw: body } };
}

function parsePlanBody(body: string): ParseResult<AgentPlan> {
    // We prefer JSON {goal, steps}, but models often emit prose plans
    // (especially Claude — "I'll read the file, then edit it..."). Treat
    // those as plans with a goal-only summary rather than surfacing a
    // user-visible "[invalid plan]" error: plans are advisory scratchpad,
    // not load-bearing.
    let parsed: unknown = null;
    try {
        parsed = JSON.parse(body);
    } catch {
        return { ok: true, value: prosePlan(body) };
    }
    if (!parsed || typeof parsed !== 'object') {
        return { ok: true, value: prosePlan(body) };
    }
    const obj = parsed as Record<string, unknown>;
    const goal = typeof obj.goal === 'string' && obj.goal ? obj.goal : body.trim().slice(0, 200);
    const rawSteps = Array.isArray(obj.steps) ? obj.steps : [];
    const steps: AgentPlan['steps'] = [];
    for (const s of rawSteps) {
        if (typeof s === 'string') {
            steps.push({ description: s });
        } else if (s && typeof s === 'object') {
            const stepObj = s as Record<string, unknown>;
            const description = typeof stepObj.description === 'string'
                ? stepObj.description
                : (typeof stepObj.why === 'string' ? stepObj.why : '');
            const tool = typeof stepObj.tool === 'string' ? stepObj.tool : undefined;
            steps.push({ description, tool });
        }
    }
    return { ok: true, value: { goal, steps } };
}

/** Treat a non-JSON <plan> body as a single-goal plan. Truncate aggressively
 *  so a runaway prose plan doesn't flood the UI. */
function prosePlan(body: string): AgentPlan {
    const collapsed = body.trim().replace(/\s+/g, ' ');
    return { goal: collapsed.slice(0, 200), steps: [] };
}

// ─── Backwards compatibility ────────────────────────────────────────────────
//
// Old name kept as an alias because the old tests still use it. The
// behavior is unchanged except that it now also recognizes <plan> blocks.

export const ToolCallStreamParser = BlockStreamParser;

// ─── Prompt rendering ───────────────────────────────────────────────────────

/** Few-shot examples expressed as actual conversation turns.
 *
 *  Design constraints learned the hard way:
 *
 *   1. Examples must use prompts the user is UNLIKELY to type verbatim, or
 *      the model will treat the example's user turn as resolving the real
 *      one and just paraphrase the example's answer instead of running the
 *      tool. (We saw this with "explain the main file" — the model copied
 *      the example's greet() answer rather than calling read_file.)
 *
 *   2. The files referenced in examples must NOT exist in the user's actual
 *      workspace by default — otherwise the model can still confuse "I
 *      already read this" vs "I should read it." We use `examples/sample.fbasic`
 *      which won't collide with real project files.
 *
 *   3. Examples should still teach the right *pattern*: workspace question
 *      → emit <tool_call> first → then plain-text summary after the result.
 *      The patterns transfer even when the literal text doesn't.
 *
 *  Prepended on every send() but never persisted in the agent's history.
 *  Cost: ~300 tokens of prompt overhead per turn. */
export function getFewShotTurns(): Msg[] {
    return [
        // ── Conceptual question — answer from prior knowledge, no tool ───
        {
            role: 'user',
            content: 'In Fade, how do I write a FOR loop?',
        },
        {
            role: 'assistant',
            content: 'A FOR loop iterates a variable from a start to an end value:\n\n'
                + '  for i = 1 to 10\n    print i\n  next i\n\n'
                + 'The loop variable is exclusive — once `i` exceeds the upper bound the loop ends.',
        },

        // ── Workspace inspection — phrased so users won't reuse it ───────
        // The user asks about a specific file with content that won't get
        // reused in real scenarios. The model sees the pattern: "workspace
        // question → tool_call → wait → answer." Critical detail: the
        // assistant's tool_call message contains ONLY the tool_call, no
        // prose preamble. That's the pattern we want to transfer.
        {
            role: 'user',
            content: 'in examples/sample.fbasic, list every function that\'s defined',
        },
        {
            role: 'assistant',
            content: '<tool_call>{"name":"read_file","args":{"path":"examples/sample.fbasic"}}</tool_call>',
        },
        {
            role: 'user',
            content: '<tool_result name="read_file">{"path":"examples/sample.fbasic","content":"function area(w, h)\\n  return w * h\\nend function\\n\\nfunction perim(w, h)\\n  return 2 * (w + h)\\nend function"}</tool_result>',
        },
        {
            role: 'assistant',
            content: 'Two functions: `area(w, h)` returns w × h, `perim(w, h)` returns 2 × (w + h).',
        },
    ];
}

export const PROTOCOL_INSTRUCTIONS = `
You communicate using two structured block types alongside your normal
text. Every turn must end with a tool_call OR plain-text content — never
stop after only a plan.

**Tool calls.** When you need to inspect or modify the workspace, emit:

  <tool_call>
  {"name":"<tool>","args":{...}}
  </tool_call>

After emitting \`</tool_call>\`, STOP. The runtime will execute the tool and
return the result in the next user message as
\`<tool_result name="<tool>">...</tool_result>\`. Do NOT generate
\`<tool_result>\` blocks yourself — those are produced by the runtime, never
by you. Continuing past \`</tool_call>\` to "fill in" the result is wrong
and produces hallucinated content.

**Plan (optional).** For non-trivial multi-step tasks you MAY begin with a
single \`<plan>\` block — JSON \`{goal, steps}\`. Keep it short. A plan is
scratchpad thinking, NOT your answer; always follow it with a tool_call or
plain text.

**Final answer.** When done, write plain text with no <tool_call> block.
Be terse.

See the example exchanges that follow for the expected patterns.
`.trim();

/** Render the system prompt addendum that teaches the model the protocol.
 *  Includes the available tool list. */
export function renderToolProtocolPrompt(tools: Tool[]): string {
    const toolLines: string[] = ['Available tools:'];
    for (const t of tools) {
        const schemaSummary = summarizeSchema(t.schema);
        toolLines.push(`- ${t.name}(${schemaSummary}) — ${t.description}`);
    }
    return `${PROTOCOL_INSTRUCTIONS}\n\n${toolLines.join('\n')}`;
}

function summarizeSchema(schema: Record<string, unknown>): string {
    const properties = schema.properties as Record<string, { type?: string; description?: string }> | undefined;
    if (!properties) return '';
    const parts: string[] = [];
    for (const [k, v] of Object.entries(properties)) {
        parts.push(`${k}: ${v.type ?? 'any'}`);
    }
    return parts.join(', ');
}

/** Render a tool result for re-injection into the conversation. */
export function renderToolResult(toolName: string, result: unknown): string {
    let body: string;
    if (typeof result === 'string') {
        body = result;
    } else {
        try {
            body = JSON.stringify(result);
        } catch {
            body = String(result);
        }
    }
    return `<tool_result name="${toolName}">${body}</tool_result>`;
}
