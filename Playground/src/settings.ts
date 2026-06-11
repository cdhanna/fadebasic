// User + workspace settings. Mirrors the VSCode split:
//   • User settings live in localStorage (`fade.settings.user.v1`) and follow
//     the device. Theme, font, personal preferences.
//   • Workspace settings live in OPFS at `<project>/.fade/settings.json` so
//     they travel with ZIP export and GitHub sharing. Project conventions
//     like tab size or search excludes.
//
// Effective settings = DEFAULTS overlaid by user, overlaid by workspace.
// Consumers (editor, search, autosave) subscribe via onSettingsChange and
// re-apply when anything changes.
//
// Schema: a flat dotted-key map. Keeps the JSON readable and the type-narrowed
// getter ergonomic (`s['editor.fontSize']`). Adding a setting is one entry in
// the SettingSpec table; the panel's GUI section + JSON editor both pick it
// up automatically.

export type SettingValue = string | number | boolean | string[];

export type SettingsMap = Record<string, SettingValue>;

// Where a setting lives. 'user' = personal-only; 'workspace' = also editable
// per project (overrides user). 'either' = honored from both, workspace wins.
export type Scope = 'user' | 'workspace' | 'either';

export type SettingType = 'number' | 'string' | 'boolean' | 'enum' | 'string-array';

export interface SettingSpec {
    key: string;
    label: string;
    description?: string;
    type: SettingType;
    scope: Scope;
    section: string;          // GUI grouping
    defaultValue: SettingValue;
    enumValues?: string[];    // for type='enum'
    min?: number;             // for type='number'
    max?: number;
    step?: number;
    // If true the GUI hides it (still editable via JSON view). Use for power-
    // user knobs that don't warrant a widget.
    advanced?: boolean;
}

// ─── Settings catalog ──────────────────────────────────────────────────────
// Order here controls render order inside each section. Adding a setting?
// Just append — both GUI and JSON editor pick it up.

export const SETTINGS_CATALOG: SettingSpec[] = [
    // Appearance
    {
        key: 'ui.theme', label: 'Theme',
        description: 'Color theme for the editor and surrounding UI. Auto follows your OS preference.',
        type: 'enum',
        enumValues: ['auto', 'dark', 'light', 'dracula', 'solarized-dark', 'monokai', 'nord', 'high-contrast', 'dbp'],
        scope: 'user', section: 'Appearance', defaultValue: 'dark',
    },

    // Editor
    {
        key: 'editor.fontSize', label: 'Font size',
        description: 'Editor font size, in pixels.',
        type: 'number', scope: 'user', section: 'Editor',
        defaultValue: 14, min: 8, max: 32, step: 1,
    },
    {
        key: 'editor.fontFamily', label: 'Font family',
        description: 'Editor font stack. Comma-separated CSS font-family list.',
        type: 'string', scope: 'user', section: 'Editor',
        defaultValue: 'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Courier New", monospace',
    },
    {
        key: 'editor.lineHeight', label: 'Line height',
        description: 'Editor line height in pixels. 0 = computed from font size.',
        type: 'number', scope: 'user', section: 'Editor',
        defaultValue: 0, min: 0, max: 40, step: 1, advanced: true,
    },
    {
        key: 'editor.tabSize', label: 'Tab size',
        description: 'Number of spaces a tab expands to.',
        type: 'number', scope: 'either', section: 'Editor',
        defaultValue: 2, min: 1, max: 8, step: 1,
    },
    {
        key: 'editor.insertSpaces', label: 'Insert spaces',
        description: 'When pressing Tab, insert spaces instead of a tab character.',
        type: 'boolean', scope: 'either', section: 'Editor',
        defaultValue: true,
    },
    {
        key: 'editor.wordWrap', label: 'Word wrap',
        description: 'Whether long lines wrap inside the editor.',
        type: 'enum', enumValues: ['off', 'on', 'bounded'],
        scope: 'user', section: 'Editor', defaultValue: 'off',
    },
    {
        key: 'editor.minimap', label: 'Show minimap',
        description: 'Show the overview minimap on the right side of the editor.',
        type: 'boolean', scope: 'user', section: 'Editor',
        defaultValue: false,
    },
    {
        key: 'editor.renderWhitespace', label: 'Render whitespace',
        description: 'How whitespace characters are rendered.',
        type: 'enum', enumValues: ['none', 'boundary', 'selection', 'all'],
        scope: 'user', section: 'Editor', defaultValue: 'none', advanced: true,
    },
    {
        key: 'editor.lineNumbers', label: 'Show line numbers',
        description: 'Show line numbers in the editor gutter.',
        type: 'boolean', scope: 'user', section: 'Editor',
        defaultValue: true,
    },

    // Search
    {
        key: 'search.exclude', label: 'Exclude globs',
        description: 'File patterns to skip when searching the workspace.',
        type: 'string-array', scope: 'either', section: 'Search',
        defaultValue: ['.fade/**', 'dist/**', 'node_modules/**'],
    },

    // Autosave
    {
        key: 'autosave.debounceMs', label: 'Autosave debounce (ms)',
        description: 'Idle time before unsaved edits flush to disk.',
        type: 'number', scope: 'user', section: 'Autosave',
        defaultValue: 600, min: 100, max: 5000, step: 50,
    },

    // Live Session
    {
        key: 'collab.gameFrameFps', label: 'Game stream FPS',
        description:
            'Frames per second the host broadcasts of its game canvas to observers. '
            + 'Higher is smoother but uses more bandwidth and CPU; lower is choppier '
            + 'but cheap. Takes effect on the next Run.',
        type: 'number', scope: 'user', section: 'Live Session',
        defaultValue: 12, min: 1, max: 30, step: 1,
    },
    {
        key: 'collab.gameFrameQuality', label: 'Game stream JPEG quality',
        description:
            'JPEG quality of broadcast game frames, 0.1-1.0. Lower compresses harder '
            + '(smaller frames, blockier image). 0.55 is the historical default.',
        type: 'number', scope: 'user', section: 'Live Session',
        defaultValue: 0.55, min: 0.1, max: 1.0, step: 0.05,
    },
];

const BY_KEY = new Map(SETTINGS_CATALOG.map((s) => [s.key, s]));

// Convenient defaults map.
export const DEFAULTS: SettingsMap = Object.fromEntries(
    SETTINGS_CATALOG.map((s) => [s.key, s.defaultValue]),
);

export function specFor(key: string): SettingSpec | undefined {
    return BY_KEY.get(key);
}

// ─── Storage ───────────────────────────────────────────────────────────────

const USER_KEY = 'fade.settings.user.v1';
const WORKSPACE_FILE = '.fade/settings.json';

export interface SettingsWorkspace {
    read(path: string): Promise<string>;
    write(path: string, content: string): Promise<void>;
    mkdir(path: string): Promise<void>;
    currentProject(): string;
}

export interface SettingsState {
    user: SettingsMap;
    workspace: SettingsMap;
    effective: SettingsMap;
}

export type SettingsChangeListener = (state: SettingsState) => void;

// Pub/sub + cached state. The module is a singleton — only one settings
// controller per page lifetime.
let cached: SettingsState | null = null;
const listeners = new Set<SettingsChangeListener>();
let activeWorkspace: SettingsWorkspace | null = null;

export function onSettingsChange(cb: SettingsChangeListener): () => void {
    listeners.add(cb);
    if (cached) cb(cached);
    return () => { listeners.delete(cb); };
}

export function currentSettings(): SettingsState {
    if (cached) return cached;
    // Pre-bootstrap fallback so any consumer who reads before initSettings()
    // doesn't crash. Real values land once initSettings() resolves.
    return { user: {}, workspace: {}, effective: { ...DEFAULTS } };
}

export function getEffective<T extends SettingValue>(key: string): T {
    const s = currentSettings().effective;
    if (key in s) return s[key] as T;
    return DEFAULTS[key] as T;
}

function loadUser(): SettingsMap {
    try {
        const raw = localStorage.getItem(USER_KEY);
        if (!raw) return {};
        const parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object') return sanitize(parsed);
    } catch (e) {
        console.warn('[fade] failed to parse user settings', e);
    }
    return {};
}

function saveUser(s: SettingsMap): void {
    try { localStorage.setItem(USER_KEY, JSON.stringify(s, null, 2)); } catch (e) {
        console.warn('[fade] failed to save user settings', e);
    }
}

async function loadWorkspace(ws: SettingsWorkspace): Promise<SettingsMap> {
    try {
        const text = await ws.read(WORKSPACE_FILE);
        const parsed = JSON.parse(text);
        if (parsed && typeof parsed === 'object') return sanitize(parsed);
    } catch {
        // Missing file or unparseable JSON → empty. Workspace settings are
        // optional so we don't surface this as an error.
    }
    return {};
}

async function saveWorkspace(ws: SettingsWorkspace, s: SettingsMap): Promise<void> {
    try { await ws.mkdir('.fade'); } catch { /* may already exist */ }
    await ws.write(WORKSPACE_FILE, JSON.stringify(s, null, 2) + '\n');
}

// Drop entries whose key isn't in the catalog or whose value doesn't match
// the declared type. Keeps the rest. Better to ignore one bad key than to
// throw away the entire file.
function sanitize(raw: Record<string, unknown>): SettingsMap {
    const out: SettingsMap = {};
    for (const [k, v] of Object.entries(raw)) {
        const spec = BY_KEY.get(k);
        if (!spec) continue;
        if (coerce(spec, v) !== undefined) out[k] = coerce(spec, v) as SettingValue;
    }
    return out;
}

function coerce(spec: SettingSpec, v: unknown): SettingValue | undefined {
    switch (spec.type) {
        case 'number':
            if (typeof v === 'number' && Number.isFinite(v)) {
                if (spec.min != null && v < spec.min) return spec.min;
                if (spec.max != null && v > spec.max) return spec.max;
                return v;
            }
            return undefined;
        case 'string':
            return typeof v === 'string' ? v : undefined;
        case 'boolean':
            return typeof v === 'boolean' ? v : undefined;
        case 'enum':
            return typeof v === 'string' && spec.enumValues?.includes(v) ? v : undefined;
        case 'string-array':
            if (Array.isArray(v) && v.every((x) => typeof x === 'string')) return v as string[];
            return undefined;
    }
}

function mergeEffective(user: SettingsMap, workspace: SettingsMap): SettingsMap {
    return { ...DEFAULTS, ...user, ...workspace };
}

function fire() {
    if (!cached) return;
    for (const l of listeners) {
        try { l(cached); } catch (e) { console.error('[fade] settings listener threw', e); }
    }
}

export async function initSettings(workspace: SettingsWorkspace): Promise<SettingsState> {
    activeWorkspace = workspace;
    const user = loadUser();
    const ws = await loadWorkspace(workspace);
    cached = { user, workspace: ws, effective: mergeEffective(user, ws) };
    fire();
    return cached;
}

// Project switch: reload the workspace half and rebuild effective.
export async function reloadWorkspaceSettings(): Promise<void> {
    if (!activeWorkspace || !cached) return;
    const ws = await loadWorkspace(activeWorkspace);
    cached = { ...cached, workspace: ws, effective: mergeEffective(cached.user, ws) };
    fire();
}

export async function updateUserSetting(key: string, value: SettingValue | undefined): Promise<void> {
    if (!cached) return;
    const next = { ...cached.user };
    if (value === undefined) delete next[key]; else next[key] = value;
    cached = { user: next, workspace: cached.workspace, effective: mergeEffective(next, cached.workspace) };
    saveUser(next);
    fire();
}

export async function updateWorkspaceSetting(key: string, value: SettingValue | undefined): Promise<void> {
    if (!cached || !activeWorkspace) return;
    const next = { ...cached.workspace };
    if (value === undefined) delete next[key]; else next[key] = value;
    cached = { user: cached.user, workspace: next, effective: mergeEffective(cached.user, next) };
    await saveWorkspace(activeWorkspace, next);
    fire();
}

// Bulk replace (used by the JSON editor when the user pastes a full settings
// object). Throws on parse error so the caller can surface a message.
export async function replaceUserSettings(json: string): Promise<void> {
    const parsed = JSON.parse(json);
    if (!parsed || typeof parsed !== 'object') throw new Error('Settings must be a JSON object.');
    const sanitized = sanitize(parsed as Record<string, unknown>);
    if (!cached) return;
    cached = { user: sanitized, workspace: cached.workspace, effective: mergeEffective(sanitized, cached.workspace) };
    saveUser(sanitized);
    fire();
}

export async function replaceWorkspaceSettings(json: string): Promise<void> {
    const parsed = JSON.parse(json);
    if (!parsed || typeof parsed !== 'object') throw new Error('Settings must be a JSON object.');
    const sanitized = sanitize(parsed as Record<string, unknown>);
    if (!cached || !activeWorkspace) return;
    cached = { user: cached.user, workspace: sanitized, effective: mergeEffective(cached.user, sanitized) };
    await saveWorkspace(activeWorkspace, sanitized);
    fire();
}
