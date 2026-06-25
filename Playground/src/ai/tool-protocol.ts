// In-prompt protocol for structured model output. The model emits tagged
// blocks (`<plan>`, `<tool_call>`) inline with regular text; we parse them
// out of the stream and surface them as typed StreamEvents to the agent.
// Tool results are fed back as <tool_result> blocks in subsequent user
// messages.
//
// Read-only tools use compact single-line JSON inside <tool_call>. Write
// tools (apply_edit, create_file) may use an attribute form so multiline
// content doesn't need JSON escaping:
//
//   <tool_call name="apply_edit" path="main.fbasic" start="5" end="5">
//   print "hello"
//   </tool_call>

import type { Msg, StreamEvent, Tool } from './providers/types';

// ─── Block types ────────────────────────────────────────────────────────────

export interface AgentPlan {
    goal: string;
    steps: Array<{ tool?: string; description: string }>;
}

export interface ParsedToolCall {
    id: string;
    name: string;
    args: unknown;
    raw: string;
}

/** Stream events emitted by the protocol parser. */
export type ProtocolEvent =
    | StreamEvent
    | { kind: 'plan'; plan: AgentPlan; raw: string }
    | { kind: 'tool_parse_error'; error: string; raw: string };

// ─── Generic tagged-block parser ────────────────────────────────────────────

type TagName = 'plan' | 'tool_call';

interface OpenTagMatch {
    tag: TagName;
    start: number;
    bodyStart: number;
    attrs?: Record<string, string>;
}

/** Stateful streaming parser. */
export class BlockStreamParser {
    private buffer = '';
    private inTag: TagName | null = null;
    /** Attributes from the opening <tool_call …> tag, if any. */
    private inTagAttrs: Record<string, string> | null = null;
    private callCounter = 0;

    feed(delta: string): ProtocolEvent[] {
        this.buffer += delta;
        const out: ProtocolEvent[] = [];

        while (true) {
            if (this.inTag === null) {
                const match = this.findOpenTag(this.buffer);
                if (!match) {
                    const safe = Math.max(0, this.buffer.length - longestTagLength());
                    if (safe > 0) {
                        out.push({ kind: 'text', delta: this.buffer.slice(0, safe) });
                        this.buffer = this.buffer.slice(safe);
                    }
                    break;
                }
                if (match.start > 0) {
                    out.push({ kind: 'text', delta: this.buffer.slice(0, match.start) });
                }
                this.buffer = this.buffer.slice(match.bodyStart);
                this.inTag = match.tag;
                this.inTagAttrs = match.tag === 'tool_call' ? (match.attrs ?? null) : null;
            } else {
                const close = `</${this.inTag}>`;
                const closeIdx = this.buffer.indexOf(close);
                if (closeIdx < 0) break;

                const body = this.buffer.slice(0, closeIdx);
                const wasTag = this.inTag;
                const wasAttrs = this.inTagAttrs;
                this.buffer = this.buffer.slice(closeIdx + close.length);
                this.inTag = null;
                this.inTagAttrs = null;

                const ev = this.parseBody(wasTag, body, wasAttrs);
                if (ev) out.push(ev);
            }
        }

        return out;
    }

    end(): ProtocolEvent[] {
        const out: ProtocolEvent[] = [];
        if (this.inTag !== null) {
            // Smaller models frequently emit a complete, valid <tool_call>
            // body but stop before the closing </tool_call> (they hit an
            // end-of-turn token right after the JSON). Rather than discard a
            // perfectly good call as "[unclosed …]" text, try to parse the
            // buffered body — if it's a usable tool_call/plan, salvage it.
            const salvaged = this.parseBody(this.inTag, this.buffer, this.inTagAttrs);
            const wasTag = this.inTag;
            this.buffer = '';
            this.inTag = null;
            this.inTagAttrs = null;
            if (salvaged && (salvaged.kind === 'tool_call' || salvaged.kind === 'plan')) {
                out.push(salvaged);
            } else if (salvaged && salvaged.kind === 'tool_parse_error') {
                // Genuinely malformed — surface as a parse error so the agent
                // can nudge a retry, not as confusing inline text.
                out.push(salvaged);
            } else {
                out.push({ kind: 'text', delta: `\n[unclosed <${wasTag}>]\n` });
            }
        } else if (this.buffer.length > 0) {
            out.push({ kind: 'text', delta: this.buffer });
            this.buffer = '';
        }
        return out;
    }

    private findOpenTag(text: string): OpenTagMatch | null {
        let best: OpenTagMatch | null = null;

        const planIdx = text.indexOf('<plan>');
        if (planIdx >= 0) {
            best = { tag: 'plan', start: planIdx, bodyStart: planIdx + '<plan>'.length };
        }

        const tcIdx = text.indexOf('<tool_call');
        if (tcIdx >= 0) {
            const gt = text.indexOf('>', tcIdx);
            if (gt >= 0) {
                const tagText = text.slice(tcIdx, gt + 1);
                if (/^<tool_call(?:\s+[^>]*)?>$/.test(tagText)) {
                    const match: OpenTagMatch = {
                        tag: 'tool_call',
                        start: tcIdx,
                        bodyStart: gt + 1,
                        attrs: parseTagAttributes(tagText),
                    };
                    if (!best || tcIdx < best.start) best = match;
                }
            }
        }

        return best;
    }

    private parseBody(
        tag: TagName,
        body: string,
        attrs: Record<string, string> | null,
    ): ProtocolEvent | null {
        if (tag === 'tool_call') {
            const parsed = parseToolCallBody(body, ++this.callCounter, attrs ?? undefined);
            if (!parsed.ok) {
                return { kind: 'tool_parse_error', error: parsed.error, raw: body };
            }
            return {
                kind: 'tool_call',
                id: parsed.value.id,
                name: parsed.value.name,
                args: parsed.value.args,
            };
        }

        const trimmed = body.trim();
        if (!trimmed) {
            return { kind: 'text', delta: `\n[empty <${tag}> block]\n` };
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
    // Hold back enough to detect a long attributed <tool_call …> still streaming.
    return 127;
}

type ParseResult<T> = { ok: true; value: T } | { ok: false; error: string };

/** Parse name="…" path="…" attributes from an opening tag. */
export function parseTagAttributes(tagText: string): Record<string, string> {
    const attrs: Record<string, string> = {};
    const re = /(\w+)=(?:"([^"]*)"|'([^']*)')/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(tagText)) !== null) {
        attrs[m[1]] = m[2] ?? m[3] ?? '';
    }
    return attrs;
}

/** Best-effort JSON parse for sloppy model output. Exported for tests. */
export function repairAndParseJson(text: string): ParseResult<unknown> {
    let s = text.trim();
    s = s.replace(/^```(?:json)?\s*\n?/i, '').replace(/\n?```\s*$/i, '').trim();

    const start = s.indexOf('{');
    if (start >= 0) {
        const extracted = extractBalancedJson(s, start);
        if (extracted) s = extracted;
    }

    s = s.replace(/,\s*([}\]])/g, '$1');

    try {
        return { ok: true, value: JSON.parse(s) };
    } catch (e) {
        return { ok: false, error: `not valid JSON: ${(e as Error).message}` };
    }
}

function extractBalancedJson(text: string, start: number): string | null {
    let depth = 0;
    let inString = false;
    let escape = false;
    for (let i = start; i < text.length; i++) {
        const ch = text[i];
        if (inString) {
            if (escape) { escape = false; continue; }
            if (ch === '\\') { escape = true; continue; }
            if (ch === '"') inString = false;
            continue;
        }
        if (ch === '"') { inString = true; continue; }
        if (ch === '{') depth++;
        else if (ch === '}') {
            depth--;
            if (depth === 0) return text.slice(start, i + 1);
        }
    }
    return null;
}

export function parseToolCallBody(
    body: string,
    counter: number,
    attrs?: Record<string, string>,
): ParseResult<ParsedToolCall> {
    if (attrs?.name) {
        return parseAttributedToolCall(attrs, body, counter);
    }

    const trimmed = body.trim();
    if (!trimmed) {
        return { ok: false, error: 'empty tool_call body (use JSON or name="…" attributes)' };
    }

    const json = repairAndParseJson(trimmed);
    if (!json.ok) return json as ParseResult<ParsedToolCall>;

    const parsed = json.value;
    if (!parsed || typeof parsed !== 'object') {
        return { ok: false, error: 'expected JSON object' };
    }
    const obj = parsed as Record<string, unknown>;
    if (typeof obj.name !== 'string' || !obj.name) {
        return { ok: false, error: 'missing "name" string' };
    }
    const args = obj.args ?? obj.arguments ?? {};
    const id = typeof obj.id === 'string' && obj.id ? obj.id : `call_${counter}_${Date.now().toString(36)}`;
    return { ok: true, value: { id, name: obj.name, args, raw: trimmed } };
}

function parseAttributedToolCall(
    attrs: Record<string, string>,
    body: string,
    counter: number,
): ParseResult<ParsedToolCall> {
    const name = attrs.name;
    if (!name) return { ok: false, error: 'missing name attribute' };

    const id = attrs.id || `call_${counter}_${Date.now().toString(36)}`;

    switch (name) {
        case 'apply_edit': {
            const path = attrs.path;
            const startLine = parseInt(attrs.start ?? attrs.startLine ?? '', 10);
            const endLine = parseInt(attrs.end ?? attrs.endLine ?? '', 10);
            if (!path) return { ok: false, error: 'apply_edit requires path="…" attribute' };
            if (!Number.isFinite(startLine) || !Number.isFinite(endLine)) {
                return { ok: false, error: 'apply_edit requires start="N" end="N" attributes' };
            }
            return {
                ok: true,
                value: { id, name, args: { path, startLine, endLine, newText: body }, raw: body },
            };
        }
        case 'create_file': {
            const path = attrs.path;
            if (!path) return { ok: false, error: 'create_file requires path="…" attribute' };
            return { ok: true, value: { id, name, args: { path, content: body }, raw: body } };
        }
        case 'read_file': {
            const path = attrs.path;
            if (!path) return { ok: false, error: 'read_file requires path="…" attribute' };
            return { ok: true, value: { id, name, args: { path }, raw: body } };
        }
        case 'list_files':
            return { ok: true, value: { id, name, args: {}, raw: body } };
        case 'search_docs': {
            const query = attrs.query;
            if (!query) return { ok: false, error: 'search_docs requires query="…" attribute' };
            return { ok: true, value: { id, name, args: { query }, raw: body } };
        }
        case 'get_diagnostics': {
            const args: Record<string, unknown> = {};
            if (attrs.path) args.path = attrs.path;
            return { ok: true, value: { id, name, args, raw: body } };
        }
        default:
            return { ok: false, error: `unknown tool: ${name}` };
    }
}

function parsePlanBody(body: string): ParseResult<AgentPlan> {
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

function prosePlan(body: string): AgentPlan {
    const collapsed = body.trim().replace(/\s+/g, ' ');
    return { goal: collapsed.slice(0, 200), steps: [] };
}

export const ToolCallStreamParser = BlockStreamParser;

// ─── Prompt rendering ───────────────────────────────────────────────────────

export function getFewShotTurns(): Msg[] {
    return [
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
            content: '<tool_result name="read_file">{"path":"examples/sample.fbasic","content":"function area(w, h)\\n  return w * h\\nend function"}</tool_result>',
        },
        {
            role: 'assistant',
            content: 'One function: `area(w, h)` returns w × h.',
        },
        {
            role: 'user',
            content: 'what is in this project?',
        },
        {
            role: 'assistant',
            content: '<tool_call>{"name":"list_files","args":{}}</tool_call>',
        },
        {
            role: 'user',
            content: '<tool_result name="list_files">{"files":["main.fbasic","fade.json"]}</tool_result>',
        },
        {
            role: 'assistant',
            content: 'Two files: `main.fbasic` and `fade.json`.',
        },
        {
            role: 'user',
            content: 'what files exist in examples/?',
        },
        {
            role: 'assistant',
            content: '<tool_call>{"name":"list_files","args":{}}</tool_call>',
        },
        {
            role: 'user',
            content: '<tool_result name="list_files">{"files":["examples/sample.fbasic"]}</tool_result>',
        },
        {
            role: 'assistant',
            content: 'Just `examples/sample.fbasic`.',
        },
        {
            role: 'user',
            content: 'in examples/sample.fbasic, change the return to multiply by 2',
        },
        {
            role: 'assistant',
            // JSON form — same shape as every other tool. ALL args go inside
            // "args". (The attribute form is also accepted for big multi-line
            // edits, but JSON is shown here so the model has a complete
            // template and never emits a name-only call.)
            content: '<tool_call>{"name":"apply_edit","args":{"path":"examples/sample.fbasic","startLine":2,"endLine":2,"newText":"  return w * h * 2"}}</tool_call>',
        },
        {
            role: 'user',
            content: '<tool_result name="apply_edit">{"path":"examples/sample.fbasic","linesReplaced":1}</tool_result>',
        },
        {
            role: 'assistant',
            content: 'Updated line 2 to `return w * h * 2`.',
        },
    ];
}

export const PROTOCOL_INSTRUCTIONS = `
You communicate using <tool_call> blocks to inspect or modify the workspace.

**Read-only tools** — compact single-line JSON inside the tags:
  <tool_call>{"name":"read_file","args":{"path":"main.fbasic"}}</tool_call>
  <tool_call>{"name":"list_files","args":{}}</tool_call>

**Write tools** — same JSON form. EVERY argument goes inside "args"; escape
newlines in code as \\n. Never emit a tool_call with just a name and no args.
  <tool_call>{"name":"apply_edit","args":{"path":"main.fbasic","startLine":5,"endLine":5,"newText":"print \\"hello\\""}}</tool_call>
  <tool_call>{"name":"create_file","args":{"path":"new.fbasic","content":"print \\"new\\""}}</tool_call>

For a LARGE multi-line edit you may instead use the attribute form (avoids
escaping every newline):
  <tool_call name="apply_edit" path="main.fbasic" start="5" end="5">
  print "hello"
  </tool_call>

After \`</tool_call>\`, STOP. The runtime executes the tool and returns
\`<tool_result name="…">…</tool_result>\` in the next user message. Never
emit \`<tool_result>\` yourself.

For multi-step tasks (several files/edits, or multiple distinct goals),
emit a short \`<plan>\` **before** the first tool_call. Single quick
questions can skip the plan. Always follow a plan with a tool_call or a
plain-text answer — never stop after only a plan.

When done, reply with plain text and no <tool_call>.

NEVER paste full file contents or large \`\`\` code blocks when changing code.
Use apply_edit / create_file so the user gets an approvable diff.
`.trim();

export function renderToolProtocolPrompt(tools: Tool[]): string {
    const toolLines: string[] = ['Available tools:'];
    for (const t of tools) {
        const schemaSummary = summarizeSchema(t.schema);
        toolLines.push(`- ${t.name}(${schemaSummary}) — ${t.description}`);
    }
    return `${PROTOCOL_INSTRUCTIONS}\n\n${toolLines.join('\n')}`;
}

function summarizeSchema(schema: Record<string, unknown>): string {
    const properties = schema.properties as Record<string, { type?: string }> | undefined;
    if (!properties) return '';
    const parts: string[] = [];
    for (const [k, v] of Object.entries(properties)) {
        parts.push(`${k}: ${v.type ?? 'any'}`);
    }
    return parts.join(', ');
}

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

/** User-message nudge when the model emitted a malformed tool_call. */
export function renderToolCallRetryPrompt(error: string): string {
    return (
        `Your tool_call was invalid: ${error}\n\n`
        + 'Retry now with a corrected tool_call. For read-only tools use single-line JSON:\n'
        + '  <tool_call>{"name":"read_file","args":{"path":"…"}}</tool_call>\n'
        + 'For edits use the attribute form with raw body:\n'
        + '  <tool_call name="apply_edit" path="…" start="N" end="N">\n'
        + '  …new lines…\n'
        + '  </tool_call>\n'
        + 'Do not apologize — emit the corrected tool_call immediately.'
    );
}
