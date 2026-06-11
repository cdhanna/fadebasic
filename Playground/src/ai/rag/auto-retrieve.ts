// Heuristic gate for automatic doc injection on the first agent turn.
// Workspace questions should use tools; conceptual Fade questions benefit
// from RAG. The model can always call search_docs explicitly.

/** Command/API lookups — worth fetching docs even during edit flows. */
export function needsCommandDocs(query: string): boolean {
    const q = query.toLowerCase().trim();
    if (q.length < 8) return false;
    if (/\b(how do i|how to|what is|what does|which command|what command)\b/.test(q)
        && /\b(command|fade|fbasic|api|syntax|function)\b/.test(q)) {
        return true;
    }
    if (/\b(draw|sprite|shader|texture|sound|input|keyboard|mouse|collision|vector)\b/.test(q)
        && /\b(how|what|use|call|command)\b/.test(q)) {
        return true;
    }
    if (/\b(unknown|undefined|not found|doesn'?t exist)\b/.test(q)
        && /\b(command|identifier|function)\b/.test(q)) {
        return true;
    }
    return false;
}

/** True when the user's message looks like a language/docs question rather
 *  than a workspace inspection task. */
export function shouldAutoRetrieveDocs(query: string): boolean {
    const q = query.toLowerCase().trim();
    if (q.length < 12) return false;

    // Command/API questions benefit from docs even in project context.
    if (needsCommandDocs(q)) return true;

    // Workspace / project tasks — tools + workspace context are enough.
    if (/\b(my |the |this )?(project|workspace|repo)\b/.test(q)) return false;
    if (/\bwhat files?\b/.test(q)) return false;
    if (/\b(list|read|show|open)\b/.test(q)
        && /\b(files?|project|main\.fbasic|\.fbasic)\b/.test(q)) {
        return false;
    }
    // Pure file edits without command/API questions — skip auto-RAG.
    if (/\b(edit|fix|change|update|delete|create|write)\b/.test(q)
        && /\b(files?|project|main\.fbasic|\.fbasic)\b/.test(q)
        && !needsCommandDocs(q)) {
        return false;
    }
    if (/\b(in |from )(my |the |this )?(code|file|project)\b/.test(q)
        && !needsCommandDocs(q)) {
        return false;
    }

    // Conceptual Fade / language questions — auto-RAG helps here.
    if (/\b(how do i|how to|what is|what does|explain|syntax|semantics)\b/.test(q)) return true;
    if (/\b(fade|fbasic)\b/.test(q)
        && /\b(loop|function|array|command|type|print|variable|class|module)\b/.test(q)) {
        return true;
    }
    if (/\b(monogame|sprites?|textures?|shaders?)\b/.test(q)
        && /\b(how|what|command)\b/.test(q)) {
        return true;
    }

    return false;
}

/** Prefetch search_docs before the model turn (mirrors list_files prefetch). */
export function shouldPrefetchDocs(query: string): boolean {
    return needsCommandDocs(query);
}
