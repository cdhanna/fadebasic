// Pull likely Fade *command invocations* out of a block of code — the leading
// word(s) of a statement that take an argument, e.g. `key down "left"` →
// "key down", `load image 1, "x"` → "load image". Used after a rejected edit
// to look up the real docs/signature for the commands the model used (it
// usually guessed them), and feed those back so the next attempt is correct.

// Words that START a statement but are language keywords, not commands.
const LEADING_KEYWORDS = new Set([
    'if', 'while', 'until', 'for', 'else', 'elseif', 'repeat', 'select', 'case',
    'then', 'and', 'or', 'not', 'to', 'step', 'global', 'local', 'return',
    'function', 'endfunction', 'exitfunction', 'do', 'loop', 'next', 'endif',
    'endwhile', 'endselect', 'endcase', 'exit', 'skip', 'goto', 'gosub',
]);

// Value positions where a bare token is being USED as a value (so a
// value-returning command there needs parens): after if/while/etc., after
// `=`/operators/comma/open-paren, or after and/or/not/return.
const VALUE_CONTEXT = /(?:\b(?:if|elseif|while|until|return|and|or|not)\b|[=<>+\-*/(,])\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*(\(?)/g;

/** Flag single-word commands used in a value position WITHOUT parentheses —
 *  `if leftKey then` should be `if leftKey() then`. This reads the code + the
 *  real command list directly, ignoring the (misleading) LSP error code. */
export function detectMissingCallParens(code: string, commandNames: string[]): string[] {
    // Only single-word commands are called like functions; multi-word commands
    // (`key down`) are statements and don't take this form.
    const single = new Set(commandNames.filter(c => c && !/\s/.test(c)).map(c => c.toLowerCase()));
    if (single.size === 0) return [];
    const out = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        VALUE_CONTEXT.lastIndex = 0;
        let m: RegExpExecArray | null;
        while ((m = VALUE_CONTEXT.exec(line)) !== null) {
            const ident = m[1];
            const hasParen = m[2] === '(';
            if (!hasParen && single.has(ident.toLowerCase())) {
                out.add(`\`${ident}\` is a command that returns a value — call it with parentheses: \`${ident}()\`.`);
            }
        }
    }
    return [...out];
}

export function extractCommandPhrases(code: string): string[] {
    const found = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        // Single-line statements are colon-separated.
        for (let seg of line.split(':')) {
            seg = seg.trim()
                // drop a leading control keyword so "IF key down …" → "key down …"
                .replace(/^(if|while|until|for|else|elseif|repeat|select|case)\s+/i, '');
            // Consider both the statement itself and the RHS of an assignment
            // (`clip = reserve sfx clip id(0)` → look up "reserve sfx clip id").
            const candidates = [seg];
            const asg = /^[a-z_][a-z0-9_]*\s*=\s*(.+)$/i.exec(seg);
            if (asg) candidates.push(asg[1].trim());
            for (const cand of candidates) {
                // A command call: 1–4 leading words, then an argument (string,
                // number, or open paren).
                const m = /^([a-z][a-z0-9_]*(?:\s+[a-z][a-z0-9_]*){0,3})\s*["\d(]/i.exec(cand);
                if (!m) continue;
                const phrase = m[1].trim().toLowerCase();
                if (LEADING_KEYWORDS.has(phrase.split(/\s+/)[0])) continue;
                found.add(phrase);
            }
        }
    }
    return [...found];
}
