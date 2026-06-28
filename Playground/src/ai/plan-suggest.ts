/** True when the user's message looks multi-step enough that the model
 *  should emit a <plan> before diving into tools. */
export function shouldSuggestPlan(query: string): boolean {
    const q = query.toLowerCase().trim();
    if (q.length < 80) return false;

    const actionHits = q.match(
        /\b(implement|build|refactor|rewrite|migrate|integrate|overhaul|restructure)\b/g,
    );
    if (actionHits && actionHits.length >= 1
        && /\b(and|then|also|multiple|several|all|every|each)\b/.test(q)) {
        return true;
    }

    const verbs = q.match(/\b(add|fix|create|update|change|remove|delete|write|read)\b/g);
    if (verbs && verbs.length >= 3) return true;

    if (/\b(step \d|first.+then|after that|and then)\b/.test(q)) return true;
    if (/\b(multiple files|several files|across (the )?project)\b/.test(q)) return true;

    return false;
}

export const PLAN_SUGGESTION_BLOCK =
    'This request looks multi-step. Before any tool calls, emit a short '
    + '<plan>{"goal":"…","steps":[{"tool":"…","description":"…"},…]}</plan> '
    + 'breaking the work into ordered steps, then execute step 1 immediately.';
