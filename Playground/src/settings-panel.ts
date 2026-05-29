// Settings panel — hybrid form + raw JSON. Two top-level tabs (User /
// Workspace) and within each, categorized form sections with widgets per
// setting. A "Edit in settings.json →" link swaps the form for a Monaco
// JSON editor scoped to that side; pressing the back link returns to the
// form. All edits persist live (no Apply button); the JSON view validates
// on each keystroke and surfaces parse errors inline.

import * as monaco from 'monaco-editor';
import {
    SETTINGS_CATALOG,
    DEFAULTS,
    type SettingSpec,
    type SettingsState,
    type SettingValue,
    onSettingsChange,
    updateUserSetting,
    updateWorkspaceSetting,
    replaceUserSettings,
    replaceWorkspaceSettings,
} from './settings';

export interface SettingsPanelDeps {
    container: HTMLElement;
    // Current project name, surfaced in the Workspace tab header so users
    // know which project they're editing.
    getProjectName: () => string;
}

type Scope = 'user' | 'workspace';

export function mountSettingsPanel(deps: SettingsPanelDeps): { focus(): void; dispose(): void } {
    const { container, getProjectName } = deps;

    container.innerHTML = '';
    const root = document.createElement('div');
    root.className = 'settings-pane';

    // ── Scope tabs ─────────────────────────────────────────────────────────
    const tabs = document.createElement('div');
    tabs.className = 'settings-tabs';
    const userTab = makeTab('User', 'user');
    const wsTab = makeTab('Workspace', 'workspace');
    tabs.append(userTab, wsTab);
    root.appendChild(tabs);

    const body = document.createElement('div');
    body.className = 'settings-body';
    root.appendChild(body);

    container.appendChild(root);

    let activeScope: Scope = 'user';
    let viewMode: 'form' | 'json' = 'form';
    let jsonEditor: monaco.editor.IStandaloneCodeEditor | null = null;
    let jsonModel: monaco.editor.ITextModel | null = null;
    let jsonChangeDisposable: monaco.IDisposable | null = null;
    let currentState: SettingsState | null = null;

    const unsubscribe = onSettingsChange((state) => {
        currentState = state;
        // If the user is actively typing in the JSON editor, that editor IS
        // the source of truth — re-rendering would dispose it mid-edit and
        // wipe their text. The form auto-refreshes when they switch back.
        if (viewMode === 'json' && jsonEditor) return;
        render();
    });

    function makeTab(label: string, scope: Scope): HTMLButtonElement {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'settings-tab';
        btn.textContent = label;
        btn.addEventListener('click', () => {
            // Always drop back to the form view on a tab click — even when
            // clicking the active tab — so the tab also acts as a "show me
            // the GUI again" affordance when the user is deep in the JSON
            // editor. Skip the render if nothing would actually change.
            if (activeScope === scope && viewMode === 'form') return;
            activeScope = scope;
            viewMode = 'form';
            render();
        });
        return btn;
    }

    function render() {
        if (!currentState) return;
        userTab.classList.toggle('active', activeScope === 'user');
        wsTab.classList.toggle('active', activeScope === 'workspace');

        if (viewMode === 'form') renderForm();
        else renderJson();
    }

    function renderForm() {
        disposeJsonEditor();
        body.innerHTML = '';

        const header = document.createElement('div');
        header.className = 'settings-header';
        if (activeScope === 'user') {
            header.innerHTML = '<span class="settings-header-title">User settings</span>'
                + '<span class="settings-header-sub">Personal preferences, stored on this device.</span>';
        } else {
            const proj = escapeHtml(getProjectName() || '(no project)');
            header.innerHTML = `<span class="settings-header-title">Workspace settings</span>`
                + `<span class="settings-header-sub">Travels with the project: <code>${proj}/.fade/settings.json</code></span>`;
        }
        const jsonLink = document.createElement('button');
        jsonLink.type = 'button';
        jsonLink.className = 'settings-link';
        jsonLink.textContent = 'Edit in settings.json →';
        jsonLink.addEventListener('click', () => {
            viewMode = 'json';
            render();
        });
        header.appendChild(jsonLink);
        body.appendChild(header);

        // Group specs by section, filtered by what's editable in this scope.
        const visibleSpecs = SETTINGS_CATALOG.filter((s) => isEditableIn(s, activeScope) && !s.advanced);
        const sectionOrder: string[] = [];
        const bySection = new Map<string, SettingSpec[]>();
        for (const spec of visibleSpecs) {
            if (!bySection.has(spec.section)) {
                bySection.set(spec.section, []);
                sectionOrder.push(spec.section);
            }
            bySection.get(spec.section)!.push(spec);
        }

        const form = document.createElement('div');
        form.className = 'settings-form';
        for (const sectionName of sectionOrder) {
            const sectionEl = document.createElement('div');
            sectionEl.className = 'settings-section';
            const heading = document.createElement('div');
            heading.className = 'settings-section-title';
            heading.textContent = sectionName;
            sectionEl.appendChild(heading);
            for (const spec of bySection.get(sectionName)!) {
                sectionEl.appendChild(renderField(spec));
            }
            form.appendChild(sectionEl);
        }
        body.appendChild(form);
    }

    function renderField(spec: SettingSpec): HTMLElement {
        const row = document.createElement('div');
        row.className = 'settings-field';
        row.dataset.key = spec.key;

        const labelWrap = document.createElement('div');
        labelWrap.className = 'settings-field-label';
        const label = document.createElement('div');
        label.className = 'settings-field-label-text';
        label.textContent = spec.label;
        labelWrap.appendChild(label);
        if (spec.description) {
            const desc = document.createElement('div');
            desc.className = 'settings-field-desc';
            desc.textContent = spec.description;
            labelWrap.appendChild(desc);
        }
        row.appendChild(labelWrap);

        const controlWrap = document.createElement('div');
        controlWrap.className = 'settings-field-control';
        controlWrap.appendChild(renderControl(spec));
        row.appendChild(controlWrap);

        return row;
    }

    function effectiveValueForScope(spec: SettingSpec, scope: Scope): SettingValue {
        if (!currentState) return spec.defaultValue;
        const own = scope === 'user' ? currentState.user[spec.key] : currentState.workspace[spec.key];
        if (own !== undefined) return own;
        // Fall back to the "lower" scope so the widget shows what's actually
        // in effect. The placeholder/dim class signals "inherited".
        return scope === 'workspace' && currentState.user[spec.key] !== undefined
            ? currentState.user[spec.key]
            : (DEFAULTS[spec.key] ?? spec.defaultValue);
    }

    function isOverridden(spec: SettingSpec, scope: Scope): boolean {
        if (!currentState) return false;
        const own = scope === 'user' ? currentState.user[spec.key] : currentState.workspace[spec.key];
        return own !== undefined;
    }

    function setValue(spec: SettingSpec, value: SettingValue | undefined) {
        if (activeScope === 'user') void updateUserSetting(spec.key, value);
        else void updateWorkspaceSetting(spec.key, value);
    }

    function renderControl(spec: SettingSpec): HTMLElement {
        const wrap = document.createElement('div');
        wrap.className = 'settings-control';
        const inherited = !isOverridden(spec, activeScope);
        if (inherited) wrap.classList.add('inherited');

        const value = effectiveValueForScope(spec, activeScope);
        const input = makeInput(spec, value);
        wrap.appendChild(input);

        // Reset link — only meaningful if this scope owns an override.
        if (!inherited) {
            const reset = document.createElement('button');
            reset.type = 'button';
            reset.className = 'settings-reset';
            reset.title = 'Reset to default';
            reset.textContent = '↺';
            reset.addEventListener('click', () => setValue(spec, undefined));
            wrap.appendChild(reset);
        }

        return wrap;
    }

    function makeInput(spec: SettingSpec, value: SettingValue): HTMLElement {
        switch (spec.type) {
            case 'number': {
                const inp = document.createElement('input');
                inp.type = 'number';
                inp.className = 'settings-input settings-input-number';
                if (spec.min != null) inp.min = String(spec.min);
                if (spec.max != null) inp.max = String(spec.max);
                if (spec.step != null) inp.step = String(spec.step);
                inp.value = String(value as number);
                inp.addEventListener('change', () => {
                    const n = Number(inp.value);
                    if (Number.isFinite(n)) setValue(spec, n);
                });
                return inp;
            }
            case 'string': {
                const inp = document.createElement('input');
                inp.type = 'text';
                inp.className = 'settings-input settings-input-text';
                inp.value = value as string;
                inp.addEventListener('change', () => setValue(spec, inp.value));
                return inp;
            }
            case 'boolean': {
                const labelEl = document.createElement('label');
                labelEl.className = 'settings-checkbox';
                const inp = document.createElement('input');
                inp.type = 'checkbox';
                inp.checked = value as boolean;
                inp.addEventListener('change', () => setValue(spec, inp.checked));
                labelEl.appendChild(inp);
                const cap = document.createElement('span');
                cap.className = 'settings-checkbox-caption';
                cap.textContent = (value as boolean) ? 'Enabled' : 'Disabled';
                inp.addEventListener('change', () => {
                    cap.textContent = inp.checked ? 'Enabled' : 'Disabled';
                });
                labelEl.appendChild(cap);
                return labelEl;
            }
            case 'enum': {
                const sel = document.createElement('select');
                sel.className = 'settings-input settings-input-select';
                for (const ev of spec.enumValues ?? []) {
                    const opt = document.createElement('option');
                    opt.value = ev;
                    opt.textContent = ev;
                    if (ev === value) opt.selected = true;
                    sel.appendChild(opt);
                }
                sel.addEventListener('change', () => setValue(spec, sel.value));
                return sel;
            }
            case 'string-array': {
                const ta = document.createElement('textarea');
                ta.className = 'settings-input settings-input-textarea';
                ta.spellcheck = false;
                ta.rows = Math.max(2, (value as string[]).length);
                ta.value = (value as string[]).join('\n');
                ta.placeholder = 'One pattern per line';
                ta.addEventListener('change', () => {
                    const lines = ta.value.split('\n').map((s) => s.trim()).filter((s) => s.length > 0);
                    setValue(spec, lines);
                });
                return ta;
            }
        }
    }

    function renderJson() {
        body.innerHTML = '';

        const header = document.createElement('div');
        header.className = 'settings-header';
        if (activeScope === 'user') {
            header.innerHTML = '<span class="settings-header-title">User settings (JSON)</span>'
                + '<span class="settings-header-sub">Stored in <code>localStorage</code> as <code>fade.settings.user.v1</code>.</span>';
        } else {
            const proj = escapeHtml(getProjectName() || '(no project)');
            header.innerHTML = `<span class="settings-header-title">Workspace settings (JSON)</span>`
                + `<span class="settings-header-sub"><code>${proj}/.fade/settings.json</code></span>`;
        }
        const back = document.createElement('button');
        back.type = 'button';
        back.className = 'settings-link';
        back.textContent = '← Back to form';
        back.addEventListener('click', () => {
            viewMode = 'form';
            render();
        });
        header.appendChild(back);
        body.appendChild(header);

        const editorHost = document.createElement('div');
        editorHost.className = 'settings-json-host';
        body.appendChild(editorHost);

        const status = document.createElement('div');
        status.className = 'settings-json-status';
        body.appendChild(status);

        const initial = currentState
            ? (activeScope === 'user' ? currentState.user : currentState.workspace)
            : {};
        const initialText = JSON.stringify(initial, null, 2);

        jsonModel = monaco.editor.createModel(initialText, 'json');
        jsonEditor = monaco.editor.create(editorHost, {
            model: jsonModel,
            theme: 'fade-dark',
            automaticLayout: true,
            minimap: { enabled: false },
            fontSize: 13,
            scrollBeyondLastLine: false,
            tabSize: 2,
            insertSpaces: true,
        } as monaco.editor.IStandaloneEditorConstructionOptions);

        let saveTimer: number | undefined;
        jsonChangeDisposable = jsonModel.onDidChangeContent(() => {
            if (saveTimer != null) window.clearTimeout(saveTimer);
            saveTimer = window.setTimeout(async () => {
                const text = jsonModel?.getValue() ?? '';
                try {
                    if (activeScope === 'user') await replaceUserSettings(text);
                    else await replaceWorkspaceSettings(text);
                    status.textContent = 'Saved';
                    status.classList.remove('error');
                    window.setTimeout(() => { if (status.textContent === 'Saved') status.textContent = ''; }, 1200);
                } catch (e) {
                    status.textContent = `Invalid JSON: ${(e as Error).message}`;
                    status.classList.add('error');
                }
            }, 350);
        });
    }

    function disposeJsonEditor() {
        if (jsonChangeDisposable) { jsonChangeDisposable.dispose(); jsonChangeDisposable = null; }
        if (jsonEditor) { jsonEditor.dispose(); jsonEditor = null; }
        if (jsonModel) { jsonModel.dispose(); jsonModel = null; }
    }

    function isEditableIn(spec: SettingSpec, scope: Scope): boolean {
        if (spec.scope === 'either') return true;
        return spec.scope === scope;
    }

    return {
        focus() {
            const firstInput = body.querySelector<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(
                'input, select, textarea',
            );
            firstInput?.focus();
        },
        dispose() {
            unsubscribe();
            disposeJsonEditor();
        },
    };
}

function escapeHtml(s: string): string {
    return s
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}
