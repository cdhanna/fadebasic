/** True when the user is asking for a project/workspace inventory. Triggers
 *  an automatic list_files prefetch — intentionally narrow so generic
 *  "what files?" still goes through the normal model tool-call path. */
export function shouldRequireWorkspaceTools(query: string): boolean {
    const q = query.toLowerCase().trim();
    if (/\bwhat(?:'s| is) (?:in |inside )?(?:this |the |my )?(?:project|workspace|repo)\b/.test(q)) {
        return true;
    }
    if (/\bwhat(?:'s| is) (?:this |the |my )?(?:project|workspace)\b/.test(q)) return true;
    if (/\bwhat files? (?:are )?(?:in |inside )(?:this |the |my )?(?:project|workspace|repo)\b/.test(q)) {
        return true;
    }
    if (/\b(list|show|describe)\b.*\b(?:this |the |my )?(?:project|workspace|repo)\b/.test(q)) {
        return true;
    }
    if (/\b(?:this |the |my )?(?:project|workspace|repo)\b.*\b(list|show|describe|contain|include|files?)\b/.test(q)) {
        return true;
    }
    return false;
}

export const WORKSPACE_TOOLS_BLOCK =
    'The user is asking about workspace contents. You MUST call list_files '
    + '(or read_file for one specific path) before answering — do not invent '
    + 'or paraphrase a file list from memory.';

export const WORKSPACE_PREFETCHED_BLOCK =
    'list_files was run automatically for this question. Answer from the '
    + '<tool_result name="list_files"> already in the conversation — do not '
    + 'call list_files again unless the user asks you to refresh the listing.';
