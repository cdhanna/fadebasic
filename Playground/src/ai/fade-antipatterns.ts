// Detect known "wrote it like another language" mistakes in Fade source and
// return a specific correction for each. The LSP error codes are opaque
// ("missing EndFunction clause", "no overload"); these translate the actual
// offending text into "you wrote X, write Y instead" — fed back next to the
// errors so the model fixes the real problem instead of guessing again.
//
// Expand alongside FADE_RULES as new failure modes show up.

export function detectFadeAntiPatterns(code: string): string[] {
    const out: string[] = [];
    const add = (s: string) => { if (!out.includes(s)) out.push(s); };

    // `end function` / `end while` / `end if` … — block-enders are ONE word.
    const spacedEnd = /\bend\s+(function|while|if|select|case)\b/gi;
    let m: RegExpExecArray | null;
    while ((m = spacedEnd.exec(code)) !== null) {
        const kw = m[1].toLowerCase();
        add(
            `\`end ${kw}\` is wrong: \`end\` on its own is a command that STOPS the program, `
            + `so this halts execution and leaves a stray \`${kw}\`. Block-enders are ONE word — write \`end${kw}\`.`,
        );
    }

    // `wend` — a QBASIC-ism. Fade closes a WHILE with `endwhile`.
    if (/\bwend\b/i.test(code)) {
        add('Close a `WHILE` loop with `endwhile`, not `wend` (`wend` does not exist in Fade).');
    }

    // `loop` used as a WHILE-style closer, e.g. `loop while x` / `loop until x`
    // — Fade's `DO … LOOP` takes a bare `loop`; conditional loops use
    // `WHILE … ENDWHILE` or `REPEAT … UNTIL`.
    if (/\bloop\s+(while|until)\b/i.test(code)) {
        add('There is no `loop while`/`loop until`. Use `WHILE <expr> … ENDWHILE`, or `REPEAT … UNTIL <expr>`, or a bare `DO … LOOP`.');
    }

    // (Compound assignment `+=` `-=` `*=` `/=` IS valid Fade — the parser
    //  desugars it to `x = x + 1`. Do NOT flag it.)

    // `while true` / `until false` — conditions are numeric.
    if (/\b(while|until|if)\s+(true|false)\b/i.test(code)) {
        add('There is no `true`/`false`. Conditions are numeric — use `1` for always-true (e.g. `WHILE 1`, or `DO … LOOP` for an infinite loop).');
    }

    // `//` or trailing `#` comments.
    if (/(^|\s)\/\//.test(code)) {
        add('Comments use a backtick (`` ` ``), not `//`.');
    }

    // Single-line `IF … THEN <statement>` followed by a stray `ENDIF`. The
    // single-line form is complete on its own line; `ENDIF` is only for the
    // block form (`IF <expr>` alone, body on following lines).
    const lines = code.split('\n');
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();
        // `if <cond> then <something>` — a statement AFTER then (not block form).
        if (!/^if\b.+\bthen\b\s*\S/i.test(line)) continue;
        let j = i + 1;
        while (j < lines.length && (!lines[j].trim() || lines[j].trim().startsWith('`'))) j++;
        if (j < lines.length && /^endif\b/i.test(lines[j].trim())) {
            add('A single-line `IF … THEN <statement>` is complete — remove the following `ENDIF`. `ENDIF` is only for the block form (`IF <expr>` alone on its line, body below, then `ENDIF`).');
            break;
        }
    }

    return out;
}
