/** Map a RAG chunk source path to a Help-tab doc, when one exists. */
export function helpTabForSource(source: string): 'language' | 'playground' | null {
    const file = source.includes('/') ? source.split('/').pop()! : source;
    const lower = file.toLowerCase();
    if (lower === 'language.md') return 'language';
    if (lower === 'playground.md') return 'playground';
    return null;
}

/** GitHub fallback for docs not mirrored in the Help panel. */
export function externalDocUrl(source: string): string | null {
    if (source.startsWith('FadeBook/')) {
        const rel = source.slice('FadeBook/'.length);
        return `https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/${encodeURI(rel)}`;
    }
    if (source.startsWith('MonoGame/')) {
        const rel = source.slice('MonoGame/'.length);
        return `https://github.com/cdhanna/fadebasic/blob/main/Playground/rag_files/monogame/${encodeURI(rel)}`;
    }
    return null;
}

/** Last segment of a chunk heading path ("Language > Variables" → "Variables"). */
export function headingTail(heading: string): string {
    const parts = heading.split('>').map(s => s.trim()).filter(Boolean);
    return parts[parts.length - 1] ?? heading.trim();
}

export function normalizeHeadingMatch(s: string): string {
    return s.toLowerCase().replace(/[^\w\s]+/g, ' ').replace(/\s+/g, ' ').trim();
}

/** Guess a Help Commands entry name from a RAG heading (e.g. "print", "sprite draw"). */
export function guessCommandName(heading: string): string | null {
    const tail = headingTail(heading).replace(/`/g, '').trim();
    if (!tail || tail.length > 48) return null;
    // Fade commands are usually short phrases: "print", "draw sprite", "for".
    if (!/^[a-z][\w\s.-]*$/i.test(tail)) return null;
    return tail;
}
