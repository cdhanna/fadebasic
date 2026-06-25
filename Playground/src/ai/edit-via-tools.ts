/** True when the user is asking the agent to change code (not just explain). */
export function userRequestedCodeChange(query: string): boolean {
    const q = query.toLowerCase();
    if (/\b(apply_edit|create_file)\b/.test(q)) return false;
    return /\b(fix|change|update|edit|modify|add|remove|delete|write|create|replace|refactor|implement|patch)\b/.test(q)
        && /\b(code|file|function|line|\.fbasic|main|project|return|variable)\b/.test(q);
}

/** True when the user wants code WRITTEN or shown (broader than a file edit —
 *  also catches "write a sprite demo", "show me a loop", "make a game"). Used
 *  to attach the research-first protocol before the model writes a snippet. */
export function userWantsCode(query: string): boolean {
    const q = query.toLowerCase();
    if (userRequestedCodeChange(q)) return true;
    return /\b(write|make|build|create|show|give|generate|code|program|demo|example|snippet|script|sample)\b/.test(q)
        && /\b(code|program|demo|example|snippet|script|game|loop|function|sprite|fade|basic|sample|move|draw|render|key|input|sound|animation)\b/.test(q);
}

/** Research-first discipline for writing snippets: confirm every command is
 *  real (via search_docs) BEFORE writing, and research any new one mid-task.
 *  This is the reliable, prompt-level version of "look up the commands first";
 *  the self-heal loop + detectors enforce it after the fact. */
export const CODE_RESEARCH_PROTOCOL =
    'WRITING FADE CODE — research the commands FIRST, then write:\n'
    + '1. List the capabilities the code needs (e.g. "draw a sprite", "read the arrow keys", "clear the screen").\n'
    + '2. For EACH capability, call search_docs to find the REAL command and read its exact signature/arguments. '
    + 'Confirm the command appears in the authoritative command list.\n'
    + '3. Only write the code once every command you will use is confirmed to exist and you know its arguments — use those exact commands.\n'
    + 'HARD RULE: if, while writing, you realize you need a command you did NOT research in step 2, STOP and call '
    + 'search_docs for it before using it. Never guess a command name or its arguments, and never invent one '
    + '(there is no `cls`, `delay`, `wend`, `draw sprite`, etc. unless the list says so).';

/** True when the model pasted a multi-line code fence instead of a write tool. */
export function looksLikePastedCodeEdit(text: string): boolean {
    const fences = text.match(/```[\w]*\n[\s\S]*?```/g);
    if (!fences) return false;
    return fences.some(f => f.split('\n').length >= 4);
}

export const EDIT_VIA_TOOLS_BLOCK =
    'CODE CHANGES: Never paste full file contents or large ``` code blocks in chat. '
    + 'Use apply_edit or create_file so the user gets a diff to approve. '
    + 'After read_file, emit an apply_edit call with the new lines.\n'
    + 'PUT RUNNABLE CODE WHERE IT RUNS: only code in a file listed in the project '
    + "sources (fade.json `sources`, e.g. main.fbasic) actually executes. Strongly "
    + 'prefer apply_edit on an existing source file. Only create_file when a genuinely '
    + 'new file is needed — it is auto-added to sources, but an existing source file is better.';

export const EDIT_VIA_TOOLS_NUDGE =
    'You pasted code in markdown instead of using a write tool. '
    + 'Emit apply_edit (or create_file) now — attribute form with raw body — '
    + 'so the user sees a diff. Do not repeat the code block in plain text.';
