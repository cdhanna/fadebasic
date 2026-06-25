// Turn raw LSP compile errors from a rejected edit into Fade-specific,
// actionable guidance. Small models loop when handed bare error codes
// ("No overload for command (147)"); a concrete "do this instead" line
// gets them unstuck — usually by pointing at search_docs or a real fix
// rather than another guess.

export function lspFixHint(feedback: string): string | undefined {
    const f = feedback.toLowerCase();
    const hints: string[] = [];

    if (/no overload for command|\(147\)/.test(f)) {
        hints.push(
            'A command was called with the wrong number/type of arguments ("no overload"). '
            + 'Call search_docs for that exact command to get its real argument list — do not guess the signature.',
        );
    }
    if (/unknown symbol|invalid reference|\(200\)/.test(f)) {
        hints.push(
            'A name is not recognized. Either (a) a variable is used before it is given a value — '
            + 'assign it first (e.g. `x = 0` BEFORE the loop that uses x), or (b) it is not a real '
            + 'command — use only commands from the provided list, or call search_docs to find the right one.',
        );
    }
    if (/ambiguous between a declaration or assignment|\(107\)/.test(f)) {
        hints.push(
            'A statement is ambiguous: initialize the variable with a clear value before using it '
            + '(e.g. `x = 0`) so it is unambiguously a declaration.',
        );
    }

    if (hints.length === 0) return undefined;
    return hints.join(' ')
        + ' Fix the specific lines, then retry — avoid re-submitting the same shape.';
}
