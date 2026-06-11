import type { DiagnosticEntry, DiagnosticSeverity } from '../tools';
import { ProjectSourceMap, type ProjectSourceInput } from '../../project-source-map';

export { formatDiagnosticFeedback } from '../lsp-diagnostic-format';

export interface RawLspDiagnostic {
    severity: number;
    range: {
        start: { line: number; character: number };
        end: { line: number; character: number };
    };
    message: string;
    code?: string;
    source?: string;
}

export interface ProjectAwareLspValidateServices {
    projectLspUri: string;
    readProjectSources(): ProjectSourceInput[];
    isProjectSource(path: string): boolean;
    checkDiagnostics(uri: string, text: string): Promise<RawLspDiagnostic[]>;
    /** Restore the live project document after a speculative LSP check. */
    restoreProjectDoc(): void;
}

function isFadeSourcePath(path: string): boolean {
    return /\.(fbasic|fade)$/i.test(path);
}

function workspaceUri(path: string): string {
    return `file:///workspace/${path}`;
}

function severityFromLsp(s: number): DiagnosticSeverity {
    if (s === 1) return 'error';
    if (s === 2) return 'warning';
    if (s === 3) return 'info';
    return 'hint';
}

function rawToEntry(d: RawLspDiagnostic, path: string): DiagnosticEntry {
    return {
        path,
        severity: severityFromLsp(d.severity),
        line: d.range.start.line + 1,
        column: d.range.start.character + 1,
        endLine: d.range.end.line + 1,
        endColumn: d.range.end.character + 1,
        message: d.message,
        code: d.code,
        source: d.source ?? 'fade',
    };
}

function splitProjectDiagnostic(
    map: ProjectSourceMap,
    d: RawLspDiagnostic,
): { name: string; diagnostic: RawLspDiagnostic } | null {
    const startMap = map.fromProject(d.range.start.line, d.range.start.character);
    if (!startMap) return null;
    const endMap = map.fromProject(d.range.end.line, d.range.end.character);
    const end = endMap && endMap.name === startMap.name
        ? endMap
        : { name: startMap.name, line: startMap.line, character: startMap.character };
    return {
        name: startMap.name,
        diagnostic: {
            ...d,
            range: {
                start: { line: startMap.line, character: startMap.character },
                end: { line: end.line, character: end.character },
            },
        },
    };
}

function buildJoinedWithEdit(
    sources: ProjectSourceInput[],
    path: string,
    content: string,
): ProjectSourceMap | null {
    if (sources.length === 0) return null;
    const hasFile = sources.some(s => s.name === path);
    if (!hasFile) return null;
    const replaced = sources.map(s => (s.name === path ? { ...s, text: content } : s));
    return ProjectSourceMap.build(replaced);
}

/** Run the Fade LSP against proposed file content without persisting it.
 *  Project sources are validated on the joined PROJECT_LSP_URI document. */
export function createProjectAwareLspEditValidator(services: ProjectAwareLspValidateServices) {
    return async function validateEditContent(path: string, content: string): Promise<DiagnosticEntry[]> {
        if (!isFadeSourcePath(path)) return [];

        try {
            if (services.isProjectSource(path)) {
                const sources = services.readProjectSources();
                const map = buildJoinedWithEdit(sources, path, content);
                if (!map) return [];

                const raw = await services.checkDiagnostics(services.projectLspUri, map.joined);
                const entries: DiagnosticEntry[] = [];
                for (const d of raw) {
                    const split = splitProjectDiagnostic(map, d);
                    if (!split || split.name !== path) continue;
                    entries.push(rawToEntry(split.diagnostic, path));
                }
                return entries;
            }

            const uri = workspaceUri(path);
            const raw = await services.checkDiagnostics(uri, content);
            return raw.map(d => rawToEntry(d, path));
        } finally {
            services.restoreProjectDoc();
        }
    };
}
