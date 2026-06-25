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
    // {
    //     version: '0.1.0',
    //     date: '2026-05-29',
    //     notes: [
    //         'Initial Release! ',
    //     ],
    // },
        {
            version: '0.4.2',
            date: '2026-06-25',
            added: [
                'Commands and syntax have help.',
                'abs() and sign() commands.',
            ],
            fixed: [
                'Type ahead fixes.',
                'Performance improvements.'
            ],
            changed: [
                'Debug UI uses delta compression for better performance.',
                'AI Chat uses local GhostBot download.'
            ]
        },
        {
            version: '0.4.1',
            date: '2026-06-10',
            changed: [
                'Local AI improvements.'
            ],
            added: [
                'Manual SDP mode for Live Collab.'
            ],
            fixed: [
                'Cannot freeze browser after fatal vm exception.',
                'Live Collab shows game starts faster.'
            ],
        },
        {
        version: '0.4.0',
        date: '2026-06-03',
        added: [
            'Audio support for `.mp3` and `.ogg` file formats.',
            'Font support for the `.ttf` file format.',
            'Shader support for the `.fx` file format.',
            'Debug UI controls.',
            'Gizmo controls.'
        ],
        changed: [
            'Live collaboration supports debug workflows.',
        ],
        fixed: [
            'Goto definition no longer has scrolling overflow bug'
        ]
    },
    {
        version: '0.3.1',
        date: '2026-05-31',
        fixed: [
            'Live collaboration works when workspace already exists.',
        ],
    },
     {
        version: '0.3.0',
        date: '2026-05-31',
        added: [
            'Texture support with limited compression options.',
            'Audio support with limited compression options.',
            'Limited Live collaboration support (no Firefox)',
            'Asset catalog'
        ],
        changed: [
            'When fatal error occurs, program halts on exception line.'
        ],
        fixed: [
            'Block comment hotkey works.',
            'Large arrays in Debug window do not overflow view.',
            'Improved type-aheads.'
        ],
    },
    {
        version: '0.2.0',
        date: '2026-05-29',
        fixed: [
            'Chrome no longer reports security issues on monogame.',
            'Internal links in the Language help docs jump to the right spot.',
            'Debugging tests now show test resolution.'
        ],
        changed: [
            'Clicking on a test header jumps focus to the test',
            'Breakpoint gutter is visually distinct and has a cursor pointer.',
            'Site title changed to `Fade Land` and added favicon. '
        ]
    },
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
