// Theme registry. Each preset bundles three layers that must switch in
// lockstep when ui.theme changes:
//   1. CSS palette  → applied by setting [data-theme="<id>"] on <html>.
//      The actual color values live in index.html under matching rules.
//   2. Monaco theme → the editor token colors. Registered at boot in
//      main.ts via monaco.editor.defineTheme.
//   3. Dockview     → tab strips, splitters, drop-target highlights. We
//      pick the closest of dockview-core's built-in theme classes.
//
// Adding a new theme is an append here + a CSS block in index.html + a
// monaco.editor.defineTheme call.

export interface ThemePreset {
    /** Stable id used in the settings file. */
    id: string;
    /** Human label shown in the settings dropdown. */
    label: string;
    /** Light-ish or dark-ish base — drives the matchMedia('prefers-color-scheme')
     *  resolution when ui.theme is 'auto'. */
    isDark: boolean;
    /** Monaco theme id registered via defineTheme. */
    monaco: string;
    /** Dockview theme className (matches a built-in CSS class shipped with
     *  dockview-core/dist/styles/dockview.css). */
    dockview: string;
}

export const THEME_PRESETS: ThemePreset[] = [
    {
        id: 'dark',
        label: 'Dark (default)',
        isDark: true,
        monaco: 'fade-dark',
        dockview: 'dockview-theme-vs',
    },
    {
        id: 'light',
        label: 'Light',
        isDark: false,
        monaco: 'fade-light',
        dockview: 'dockview-theme-light',
    },
    {
        id: 'dracula',
        label: 'Dracula',
        isDark: true,
        monaco: 'fade-dracula',
        dockview: 'dockview-theme-dracula',
    },
    {
        id: 'solarized-dark',
        label: 'Solarized Dark',
        isDark: true,
        monaco: 'fade-solarized-dark',
        dockview: 'dockview-theme-vs',
    },
    {
        id: 'monokai',
        label: 'Monokai',
        isDark: true,
        monaco: 'fade-monokai',
        dockview: 'dockview-theme-vs',
    },
    {
        id: 'nord',
        label: 'Nord',
        isDark: true,
        monaco: 'fade-nord',
        dockview: 'dockview-theme-vs',
    },
    {
        id: 'high-contrast',
        label: 'High Contrast',
        isDark: true,
        monaco: 'fade-high-contrast',
        dockview: 'dockview-theme-vs',
    },
    {
        // Tribute to the original DarkBASIC Professional editor: Windows-XP-era
        // beige chrome, white editor, bright blue keywords, grey-italic REM
        // comments, maroon strings.
        id: 'dbp',
        label: 'DBP (Dark Basic Pro)',
        isDark: false,
        monaco: 'fade-dbp',
        dockview: 'dockview-theme-light',
    },
];

const BY_ID = new Map(THEME_PRESETS.map((p) => [p.id, p]));

const FALLBACK_DARK = THEME_PRESETS[0];
const FALLBACK_LIGHT = THEME_PRESETS.find((p) => p.id === 'light')!;

/** Resolve a setting value (any string, possibly 'auto') to a concrete
 *  preset. 'auto' picks light/dark based on the OS preference; unknown ids
 *  fall back to the default dark theme. */
export function resolveTheme(requestedId: string): ThemePreset {
    if (requestedId === 'auto') {
        try {
            if (window.matchMedia('(prefers-color-scheme: light)').matches) return FALLBACK_LIGHT;
        } catch { /* matchMedia missing → dark */ }
        return FALLBACK_DARK;
    }
    return BY_ID.get(requestedId) ?? FALLBACK_DARK;
}

/** Ids in catalog order. Used to populate the settings dropdown. */
export function themeIds(): string[] {
    return ['auto', ...THEME_PRESETS.map((p) => p.id)];
}
