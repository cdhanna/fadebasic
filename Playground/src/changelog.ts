// Playground changelog. New entries go at the TOP of CHANGELOG. The
// first entry's `version` is the active app version — that's what the
// Diagnostics panel displays and what `version-popup.ts` compares
// against the user's last-seen pointer in localStorage.
//
// Each entry groups its lines into Keep-a-Changelog categories. All
// category arrays are optional; the popup renders only the ones with
// at least one line, in the order declared on ChangelogEntry. Lines
// are plain strings — no Markdown — so keep them short and
// user-facing. Use `notes` for context that isn't a code change
// (deploy details, known issues, migration warnings).
//
// Bumping for a release: prepend a new object with a higher `version`,
// today's `date`, and whichever categories apply. The popup fires
// automatically on the next deploy for every user whose
// lastSeenVersion is below the new value.

export interface ChangelogEntry {
    version: string;
    date: string;
    added?: string[];
    changed?: string[];
    fixed?: string[];
    removed?: string[];
    notes?: string[];
}

export const CHANGELOG: ChangelogEntry[] = [
    {
        version: '0.1.0',
        date: '2026-05-29',
        notes: [
            'Initial Release! ',
        ],
    },
];

export const PLAYGROUND_VERSION = CHANGELOG[0].version;

// Categories rendered in this order by version-popup.ts. Declared as
// a const tuple so the renderer can iterate without hard-coding the
// sequence in two places.
export const CHANGELOG_CATEGORIES = [
    { key: 'notes',   label: 'Notes' },
    { key: 'added',   label: 'Added' },
    { key: 'changed', label: 'Changed' },
    { key: 'fixed',   label: 'Fixed' },
    { key: 'removed', label: 'Removed' },
] as const satisfies ReadonlyArray<{
    key: keyof Omit<ChangelogEntry, 'version' | 'date'>;
    label: string;
}>;
