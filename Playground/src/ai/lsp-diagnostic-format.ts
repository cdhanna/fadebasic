import type { DiagnosticEntry } from './tools';

/** Format LSP entries for reviewer / agent feedback. */
export function formatDiagnosticFeedback(entries: DiagnosticEntry[], max = 8): string {
    const errors = entries.filter(e => e.severity === 'error');
    if (errors.length === 0) return '';
    const lines = errors.slice(0, max).map(
        e => `  L${e.line}: ${e.message}${e.code ? ` (${e.code})` : ''}`,
    );
    if (errors.length > max) lines.push(`  … +${errors.length - max} more error(s)`);
    return 'LSP compile errors in proposed file:\n' + lines.join('\n');
}
