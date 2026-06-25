// Catch the single most common way the model goes wrong with Fade: it invents
// a command that doesn't exist (often a CamelCase / collapsed guess at a real
// one, e.g. `keydown()` for `key down`, or `getMouseX()` for `mouse x()`).
//
// The authoritative command list (from the LSP / loaded DLLs) is the source of
// truth. We read the proposed CODE directly and flag call-shaped identifiers
// that aren't a real command — but ONLY when the bad name closely resembles a
// real one, so we don't wrongly flag a user function or array defined in
// another file. The suggestion ("did you mean `key down`?") is what actually
// gets the small model unstuck; a bare LSP "invalid reference" does not.

const KEYWORDS = new Set([
    'if', 'then', 'else', 'elseif', 'endif', 'while', 'endwhile', 'for', 'to',
    'step', 'next', 'repeat', 'until', 'do', 'loop', 'select', 'case',
    'endcase', 'endselect', 'function', 'endfunction', 'exitfunction',
    'global', 'local', 'return', 'and', 'or', 'not', 'exit', 'skip', 'goto',
    'gosub', 'dim', 'true', 'false', 'mod', 'as', 'type', 'endtype', 'null',
]);

/** Levenshtein edit distance — small inputs, so the simple DP is fine. */
export function levenshtein(a: string, b: string): number {
    if (a === b) return 0;
    if (a.length === 0) return b.length;
    if (b.length === 0) return a.length;
    let prev = Array.from({ length: b.length + 1 }, (_, i) => i);
    let cur = new Array<number>(b.length + 1);
    for (let i = 1; i <= a.length; i++) {
        cur[0] = i;
        for (let j = 1; j <= b.length; j++) {
            const cost = a[i - 1] === b[j - 1] ? 0 : 1;
            cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost);
        }
        [prev, cur] = [cur, prev];
    }
    return prev[b.length];
}

/** Nearest real commands to a (probably invented) name, closest first. Compares
 *  against both the command and its space-collapsed form so `keydown` matches
 *  `key down`. Only returns commands within a length-scaled edit-distance
 *  threshold, so unrelated names yield nothing. */
export function suggestCommands(name: string, commandNames: string[], max = 3): string[] {
    const n = name.toLowerCase();
    if (n.length < 3) return [];
    // Min-1 floor: short names (3-5 chars) need a near-exact match, so we don't
    // emit misleading "did you mean asc?" for an invented `abs`. Longer names
    // keep the proportional, more forgiving budget for camelCase guesses.
    const thresh = Math.max(1, Math.floor(n.length / 3));
    const scored: Array<{ cmd: string; d: number }> = [];
    for (const cmd of commandNames) {
        const c = cmd.toLowerCase();
        const d = Math.min(levenshtein(n, c), levenshtein(n, c.replace(/\s+/g, '')));
        if (d <= thresh && c !== n) scored.push({ cmd, d });
    }
    scored.sort((a, b) => a.d - b.d || a.cmd.length - b.cmd.length);
    return scored.slice(0, max).map(s => s.cmd);
}

function collectLocalNames(code: string): Set<string> {
    const local = new Set<string>();
    const add = (s: string | undefined) => { if (s) local.add(s.toLowerCase()); };
    const grab = (re: RegExp, group = 1) => {
        re.lastIndex = 0;
        let m: RegExpExecArray | null;
        while ((m = re.exec(code)) !== null) add(m[group]);
    };
    grab(/\bfunction\s+([a-z_]\w*)/gi);
    grab(/\bdim\s+([a-z_]\w*)/gi);
    grab(/\bglobal\s+([a-z_]\w*)/gi);
    grab(/\blocal\s+([a-z_]\w*)/gi);
    grab(/\bfor\s+([a-z_]\w*)\s*=/gi);
    // Assignment LHS: `x = …` and `seg : y = …`.
    grab(/(?:^|:)\s*([a-z_]\w*)\s*=/gim);
    // Function parameters: `function f(a, b)` → a, b.
    const paramRe = /\bfunction\s+[a-z_]\w*\s*\(([^)]*)\)/gi;
    let pm: RegExpExecArray | null;
    while ((pm = paramRe.exec(code)) !== null) {
        for (const p of pm[1].split(',')) add(p.trim().split(/\s+/)[0]);
    }
    return local;
}

/** Flag a variable assignment whose name collides with a built-in command —
 *  `sprite = sprite(0)` is invalid because `sprite` is reserved. Only flags
 *  single-token command names (a multi-word command can't be an assignment
 *  target anyway) and ignores array-element writes like `arr(i) = x`. */
export function detectCommandAsVariable(code: string, commandNames: string[]): string[] {
    if (commandNames.length === 0) return [];
    const cmdSet = new Set(commandNames.map(c => c.toLowerCase()));
    const out: string[] = [];
    const seen = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        // `name = …`, optionally with a GLOBAL/LOCAL prefix; not `==`, not `name(i) = …`.
        const m = /^(?:global\s+|local\s+)?([a-z_]\w*)\s*=(?!=)/i.exec(line);
        if (!m) continue;
        const name = m[1];
        const low = name.toLowerCase();
        if (!cmdSet.has(low) || seen.has(low)) continue;
        seen.add(low);
        out.push(
            `\`${name}\` is a built-in command, so it cannot be a variable name `
            + `(\`${name} = …\` is invalid). Rename the variable, e.g. \`${name}1\` or \`my${name[0].toUpperCase()}${name.slice(1)}\`.`,
        );
    }
    return out;
}

/** Flag call-shaped identifiers (`name(...)`) that are NOT a real command, a
 *  language keyword, or a name declared in this code — but only when the name
 *  closely resembles a real command (so we're confident it's a guess, not a
 *  cross-file function/array reference). Returns one actionable note each. */
export function detectUnknownCommands(code: string, commandNames: string[]): string[] {
    if (commandNames.length === 0) return [];
    const cmdSet = new Set(commandNames.map(c => c.toLowerCase()));
    const local = collectLocalNames(code);
    const out: string[] = [];
    const seen = new Set<string>();

    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        const callRe = /\b([a-z_]\w*)\s*\(/gi;
        let m: RegExpExecArray | null;
        while ((m = callRe.exec(line)) !== null) {
            const id = m[1];
            const low = id.toLowerCase();
            if (low.length < 3) continue;
            if (KEYWORDS.has(low) || cmdSet.has(low) || local.has(low)) continue;
            if (seen.has(low)) continue;
            const sugg = suggestCommands(low, commandNames, 3);
            if (sugg.length === 0) continue; // not command-like → probably a real user symbol
            seen.add(low);
            out.push(
                `\`${id}(...)\` is not a Fade command. Did you mean: `
                + sugg.map(s => `\`${s}\``).join(', ')
                + '? Use a command from the provided list, or call search_docs.',
            );
            if (out.length >= 4) return out;
        }
    }
    return out;
}

const VALUE_BEFORE = /(?:[=,(<>+\-*/]|\b(?:if|elseif|while|until|return|and|or|not)\b)\s*$/i;
const escapeRe = (s: string): string => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Flag assigning to a command or a command's result — `sprite x(1) = 100`,
 *  `mouse x() = 0`. Commands (especially value-returning ones) are read-only;
 *  you change state through a setter command, not by assigning to a getter.
 *  Matches a statement that STARTS with a command name (multi-word aware),
 *  optionally followed by a `(...)` argument list, then `=` (not `==`). Array
 *  writes (`arr(i) = x`) are unaffected — `arr` isn't a command. */
export function detectAssignToCommandCall(code: string, commandNames: string[]): string[] {
    if (commandNames.length === 0) return [];
    const cmds = [...new Set(commandNames.map(c => c.trim()).filter(Boolean))]
        .sort((a, b) => b.length - a.length); // longest first: "sprite x" before "sprite"
    const out = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        for (const cmd of cmds) {
            // <cmd> [optional (...)]  =   (but not ==)
            const re = new RegExp(`^(${escapeRe(cmd)})(?![a-zA-Z0-9_])\\s*(\\([^)]*\\))?\\s*=(?!=)`, 'i');
            if (!re.test(line)) continue;
            out.add(
                `\`${cmd}\` is a command — you cannot assign to it or its result `
                + `(\`${cmd}${/\(/.test(line.slice(cmd.length)) ? '(…)' : ''} = …\` is invalid). Commands that `
                + `return a value are read-only; to change state, store the value in your own variable and call `
                + `the matching setter command (e.g. move a sprite with \`position sprite\`).`,
            );
            break;
        }
    }
    return [...out];
}

// The MonoGame asset-loading commands. Each takes an id + a *content path with
// NO file extension* — `texture 1, "ship"`, never `"ship.png"`. The content
// pipeline resolves the bare name; an extension makes the load fail.
const ASSET_LOAD_COMMANDS = ['load sfx clip', 'texture', 'font', 'effect'];
// Media extensions a model tends to tack on. Kept tight to avoid flagging a
// legitimate string that merely ends in a dotted word.
const ASSET_EXT = /\.(png|jpe?g|bmp|gif|tga|dds|wav|ogg|mp3|m4a|ttf|otf|fnt|spritefont|xnb|fx|fxb|spv|hlsl|glsl)$/i;

/** Flag an asset-loading command given a string argument that includes a FILE
 *  EXTENSION — `texture 1, "ship.png"` should be `texture 1, "ship"`. The four
 *  loaders (`texture`, `font`, `effect`, `load sfx clip`) take a bare content
 *  path; the extension breaks the load. Only fires for these commands at
 *  statement position, so an arbitrary string literal elsewhere is left alone. */
export function detectAssetExtension(code: string): string[] {
    const cmds = [...ASSET_LOAD_COMMANDS].sort((a, b) => b.length - a.length); // longest first
    const out = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        const cmd = cmds.find(c =>
            new RegExp(`^${escapeRe(c)}(?![a-zA-Z0-9_])`, 'i').test(line));
        if (!cmd) continue;
        // Inspect string literals on this statement for a media extension.
        const strRe = /"([^"]*)"/g;
        let m: RegExpExecArray | null;
        while ((m = strRe.exec(line)) !== null) {
            const ext = ASSET_EXT.exec(m[1]);
            if (!ext) continue;
            const bare = m[1].slice(0, m[1].length - ext[0].length);
            out.add(
                `\`${cmd}\` loads an asset by its content path with NO file extension — `
                + `write \`"${bare}"\`, not \`"${m[1]}"\`. Drop the \`${ext[0]}\`.`,
            );
        }
    }
    return [...out];
}

/** Flag VALUE-RETURNING commands used WITHOUT parentheses in a value position —
 *  `position sprite 1, mouse x, mouse y` → `mouse x` / `mouse y` need `()`.
 *
 *  Unlike a name-only heuristic, this takes the authoritative list of commands
 *  that actually RETURN a value (derived from their signatures), so it never
 *  misfires on void/statement commands (e.g. `texture`), and it handles
 *  MULTI-WORD commands (`mouse x`) which the old single-word check skipped.
 *  Only flags occurrences in value position (after `=`, `,`, an operator, `(`,
 *  or if/while/return/and/or/not) so a value-returning command invoked as a
 *  statement isn't wrongly flagged. */
export function detectMissingValueCallParens(code: string, valueCommands: ReadonlyArray<string>): string[] {
    if (valueCommands.length === 0) return [];
    const cmds = [...new Set(valueCommands.map(c => c.trim()).filter(Boolean))]
        .sort((a, b) => b.length - a.length); // longest first: "mouse x" before "mouse"
    const out = new Set<string>();
    for (const rawLine of code.split('\n')) {
        const line = rawLine.trim();
        if (!line || line.startsWith('`')) continue;
        const cleaned = line.replace(/"[^"]*"/g, '""'); // ignore string contents
        for (const vc of cmds) {
            const re = new RegExp(`(^|[^a-zA-Z0-9_])(${escapeRe(vc)})(?![a-zA-Z0-9_])`, 'gi');
            let m: RegExpExecArray | null;
            while ((m = re.exec(cleaned)) !== null) {
                const beforeEnd = m.index + m[1].length;          // start of the command
                const after = cleaned.slice(beforeEnd + m[2].length);
                if (/^\s*\(/.test(after)) continue;                // already called: vc(...)
                if (!VALUE_BEFORE.test(cleaned.slice(0, beforeEnd))) continue; // not value position
                out.add(
                    `\`${vc}\` returns a value, so it MUST be called with parentheses — write `
                    + `\`${vc}(...)\` (or \`${vc}()\` if it takes no arguments). You used it without \`()\`.`,
                );
                break;
            }
        }
    }
    return [...out];
}
