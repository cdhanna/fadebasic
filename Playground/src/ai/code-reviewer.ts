import type { ChatProvider } from './providers/types';
import type { EditReviewRequest, EditReviewResult } from './edit-review-types';
import { formatDiagnosticFeedback } from './lsp-diagnostic-format';
import { withTimeout } from './with-timeout';
import { detectFadeAntiPatterns } from './fade-antipatterns';
import { detectMissingCallParens } from './command-phrases';
import { detectUnknownCommands, detectCommandAsVariable } from './fade-command-check';

export type { EditReviewRequest, EditReviewResult } from './edit-review-types';

const MAX_SNIPPET_CHARS = 6000;
const LSP_REVIEW_TIMEOUT_MS = 8_000;
const LLM_REVIEW_TIMEOUT_MS = 25_000;

export type EditReviewPhase = 'lsp' | 'llm';

export interface ReviewProposedEditOptions {
    signal?: AbortSignal;
    /** Second LLM pass — off by default; LSP is authoritative for syntax. */
    llmReview?: boolean;
    onPhase?: (phase: EditReviewPhase) => void;
}

function truncate(s: string, max: number): string {
    if (s.length <= max) return s;
    return s.slice(0, max) + `\n… [${s.length - max} more chars truncated]`;
}

function buildReviewPrompt(req: EditReviewRequest, lspNotes: string): string {
    const parts = [
        'You are a Fade Basic code reviewer. A coding agent proposed an edit.',
        'Check: syntax fit, line-ending consistency, scope (only the intended change),',
        'obvious logic errors, and Fade idioms.',
        '',
        `File: ${req.path}`,
    ];
    if (lspNotes) {
        parts.push('', '--- LSP pre-check (authoritative) ---', lspNotes);
    }
    parts.push(
        '',
        '--- current file ---',
        truncate(req.oldContent, MAX_SNIPPET_CHARS),
        '',
        '--- proposed file ---',
        truncate(req.newContent, MAX_SNIPPET_CHARS),
        '',
        'Reply with EXACTLY one line:',
        'APPROVE',
        'or',
        'ISSUES: <concise feedback the author agent should act on>',
    );
    return parts.join('\n');
}

/** Parse the reviewer model's one-line verdict. */
export function parseReviewVerdict(text: string): EditReviewResult {
    const line = text.trim().split('\n').map(s => s.trim()).find(Boolean) ?? '';
    const upper = line.toUpperCase();
    if (upper === 'APPROVE' || upper.startsWith('APPROVE ')) {
        return { approved: true, feedback: '' };
    }
    const issuesMatch = line.match(/^issues:\s*(.+)$/i);
    if (issuesMatch) {
        return { approved: false, feedback: issuesMatch[1].trim() };
    }
    if (/reject|problem|error|fix|incorrect|wrong/i.test(line)) {
        return { approved: false, feedback: line };
    }
    return { approved: true, feedback: '' };
}

async function runLlmReview(
    provider: ChatProvider,
    req: EditReviewRequest,
    lspNotes: string,
    signal?: AbortSignal,
): Promise<EditReviewResult> {
    const prompt = buildReviewPrompt(req, lspNotes);
    let text = '';

    const streamPromise = (async () => {
        for await (const ev of provider.stream({
            messages: [
                {
                    role: 'system',
                    content: 'You review Fade Basic edits. One line only: APPROVE or ISSUES: …',
                },
                { role: 'user', content: prompt },
            ],
            maxTokens: 256,
            temperature: 0.1,
            signal,
        })) {
            if (ev.kind === 'text') text += ev.delta;
            if (ev.kind === 'done') break;
        }
        return parseReviewVerdict(text);
    })();

    return withTimeout(streamPromise, LLM_REVIEW_TIMEOUT_MS, 'AI code review');
}

/** Run LSP validation (and optional LLM reviewer) on a proposed edit. */
export async function reviewProposedEdit(
    provider: ChatProvider,
    req: EditReviewRequest,
    opts: ReviewProposedEditOptions = {},
): Promise<EditReviewResult> {
    const { signal, llmReview = false, onPhase } = opts;

    if (signal?.aborted) {
        return { approved: false, feedback: 'Review cancelled.' };
    }

    // Deterministic Fade pre-check — runs before the LSP so the model gets a
    // crisp, Fade-specific reason ("`keydown(...)` is not a command — did you
    // mean `key down`?", "call it with parentheses") instead of an opaque LSP
    // code it tends to loop on. Only for Fade source, and only on text-pattern
    // mistakes that don't need whole-project symbol resolution (the LSP remains
    // authoritative for everything else).
    if (/\.(fbasic|fade)$/i.test(req.path)) {
        const cmds = req.commandNames ?? [];
        const issues = [
            ...detectFadeAntiPatterns(req.newContent),
            ...detectMissingCallParens(req.newContent, cmds),
            ...detectUnknownCommands(req.newContent, cmds),
            ...detectCommandAsVariable(req.newContent, cmds),
        ];
        if (issues.length > 0) {
            return {
                approved: false,
                feedback: 'Fade syntax problems:\n' + issues.map(s => `  - ${s}`).join('\n'),
            };
        }
    }

    onPhase?.('lsp');
    if (req.validateContent) {
        try {
            const entries = await withTimeout(
                req.validateContent(req.path, req.newContent),
                LSP_REVIEW_TIMEOUT_MS,
                'LSP syntax check',
            );
            const lspNotes = formatDiagnosticFeedback(entries);
            if (lspNotes) {
                return { approved: false, feedback: lspNotes };
            }
        } catch (e) {
            const msg = (e as Error).message ?? String(e);
            return { approved: false, feedback: `LSP validation failed: ${msg}` };
        }
    }

    if (!llmReview) {
        return { approved: true, feedback: '' };
    }

    onPhase?.('llm');
    try {
        return await runLlmReview(provider, req, '', signal);
    } catch (e) {
        const msg = (e as Error).message ?? String(e);
        return { approved: false, feedback: msg };
    }
}
