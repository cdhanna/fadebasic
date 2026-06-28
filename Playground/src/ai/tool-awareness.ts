import { needsCommandDocs } from './rag/auto-retrieve';

/** Block appended to the system prompt so small models don't forget tools. */
export const TOOLS_CAPABILITY_BLOCK =
    'You HAVE workspace tools via <tool_call> — you are NOT a chat-only assistant:\n'
    + '- list_files / read_file — full access to project source code on disk\n'
    + '- apply_edit / create_file — propose code changes (user approves the diff)\n'
    + '- get_diagnostics — LSP errors/warnings for any file\n'
    + '- search_docs — Fade language documentation (REQUIRED before using unfamiliar commands)\n'
    + '- search_catalog / browse_catalog / import_catalog_asset — find & add free sprites, sounds '
    + 'and fonts. If search_catalog finds nothing, call browse_catalog to list real entries and '
    + 'pick the closest yourself (ask before importing)\n'
    + 'Never tell the user you "cannot access" or "don\'t have" their source code. '
    + 'Call read_file (or list_files first) and answer from the result.\n'
    + 'Never invent Fade command names — call search_docs when unsure.';

/** Rule-based first-tool hints — no extra model call. */
export function buildToolRouteHint(query: string): string | null {
    const q = query.toLowerCase();
    const tools = new Set<string>();

    if (/\b(project|workspace|repo|files?)\b/.test(q)
        && /\b(what|list|show|describe|in |contain|include)\b/.test(q)) {
        tools.add('list_files');
    }
    if (/\b(read|open|show|fix|edit|change|update|refactor|error|bug|function|line)\b/.test(q)
        || /\.fbasic\b/.test(q)
        || /\b(main|source|code|file)\b/.test(q)) {
        tools.add('read_file');
    }
    if (/\b(error|warning|diagnostic|compile|syntax)\b/.test(q)) {
        tools.add('get_diagnostics');
    }
    if (needsCommandDocs(q)
        || (/\b(how do|how to|what is|syntax|command|fade|fbasic)\b/.test(q)
            && !/\b(my |this |the )?(project|file|code)\b/.test(q))) {
        tools.add('search_docs');
    }
    if (/\b(fix|edit|change|add|implement)\b/.test(q)
        && /\b(command|sprite|draw|shader|input)\b/.test(q)) {
        tools.add('search_docs');
    }

    if (tools.size === 0) return null;
    return 'Tool route: call '
        + [...tools].join(' → ')
        + ' (in that order when multiple apply) before giving a final answer.';
}

/** True when the model apologized for lacking access without using tools. */
export function looksLikeAccessDenial(text: string): boolean {
    const t = text.toLowerCase();
    return /\b(don'?t|do not|cannot|can'?t|unable to)\b/.test(t)
        && /\b(access|see|view|read|source code|your code|the code|files?)\b/.test(t);
}

export const ACCESS_DENIAL_NUDGE =
    'You still have read_file / list_files. Do NOT claim you lack access — '
    + 'call the appropriate tool now, then answer from the tool_result.';
