/** True when the user is asking the agent to change code (not just explain). */
export function userRequestedCodeChange(query: string): boolean {
    const q = query.toLowerCase();
    if (/\b(apply_edit|create_file)\b/.test(q)) return false;
    return /\b(fix|change|update|edit|modify|add|remove|delete|write|create|replace|refactor|implement|patch)\b/.test(q)
        && /\b(code|file|function|line|\.fbasic|main|project|return|variable)\b/.test(q);
}

/** True when the model pasted a multi-line code fence instead of a write tool. */
export function looksLikePastedCodeEdit(text: string): boolean {
    const fences = text.match(/```[\w]*\n[\s\S]*?```/g);
    if (!fences) return false;
    return fences.some(f => f.split('\n').length >= 4);
}

export const EDIT_VIA_TOOLS_BLOCK =
    'CODE CHANGES: Never paste full file contents or large ``` code blocks in chat. '
    + 'Use apply_edit or create_file so the user gets a diff to approve. '
    + 'After read_file, emit <tool_call name="apply_edit" …> with the new lines.';

export const EDIT_VIA_TOOLS_NUDGE =
    'You pasted code in markdown instead of using a write tool. '
    + 'Emit apply_edit (or create_file) now — attribute form with raw body — '
    + 'so the user sees a diff. Do not repeat the code block in plain text.';
