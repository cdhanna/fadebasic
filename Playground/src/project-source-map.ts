// Joined-document position translator for multi-source Fade projects.
//
// Mirrors FadeBasic.Sdk.SourceMap (see [FadeBasic/FadeBasic/Sdk/SourceMap.cs])
// — same line-counting convention so positions round-trip identically with
// what the LSP's Core handlers see on the other side of the worker boundary.
//
// Lifecycle: rebuild whenever fade.json's source list changes OR when any
// in-project file's content changes. Cheap to construct; lines.split('\n')
// dominates.

export interface ProjectSourceInput {
    // Workspace-relative name as it appears in fade.json (e.g. "main.fbasic"
    // or "lib/foo.fbasic"). Doubles as the dictionary key on lookups.
    name: string;
    text: string;
}

export interface FileLineRange {
    name: string;
    // Half-open [startLine, endLine) in joined-text coordinates.
    startLine: number;
    endLine: number;
}

export class ProjectSourceMap {
    readonly joined: string;
    readonly ranges: FileLineRange[];
    private readonly byName: Map<string, FileLineRange>;

    private constructor(joined: string, ranges: FileLineRange[]) {
        this.joined = joined;
        this.ranges = ranges;
        this.byName = new Map(ranges.map((r) => [r.name, r]));
    }

    /** Build a joined-text + range map from sources in declaration order.
     *  Matches the C# SourceMap.CreateSourceMap: split each file by newlines,
     *  strip a trailing empty entry (so a file ending in '\n' doesn't count
     *  as an extra line), then re-emit each line with a trailing '\n'. */
    static build(sources: ProjectSourceInput[]): ProjectSourceMap {
        let joined = '';
        let total = 0;
        const ranges: FileLineRange[] = [];
        for (const src of sources) {
            const lines = src.text.split('\n');
            // File.ReadAllText().SplitNewLines() returns one entry per actual
            // line — a final '\n' doesn't yield a phantom empty line. Mirror that.
            if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
            const startLine = total;
            for (const line of lines) {
                joined += line + '\n';
            }
            total += lines.length;
            ranges.push({ name: src.name, startLine, endLine: total });
        }
        return new ProjectSourceMap(joined, ranges);
    }

    /** Reverse map: joined (line, col) → originating file + local line. */
    fromProject(joinedLine: number, character: number): { name: string; line: number; character: number } | null {
        for (const r of this.ranges) {
            if (joinedLine >= r.startLine && joinedLine < r.endLine) {
                return { name: r.name, line: joinedLine - r.startLine, character };
            }
        }
        return null;
    }

    /** Forward map: per-file (name, line, col) → joined (line, col).
     *  Returns null when the name isn't a member of this project so callers
     *  can short-circuit to the standalone per-file LSP path. */
    toProject(name: string, line: number, character: number): { line: number; character: number } | null {
        const r = this.byName.get(name);
        if (!r) return null;
        return { line: r.startLine + line, character };
    }

    hasFile(name: string): boolean {
        return this.byName.has(name);
    }

    /** Names in original order — used by the diagnostics fan-out to pre-seed
     *  empty arrays so files that got "clean" still have their old markers
     *  cleared. */
    fileNames(): string[] {
        return this.ranges.map((r) => r.name);
    }
}
