// Read-only Monaco diff editor for "show me what changed" affordances.
// Opens in its own dockview tab, mirroring conflict-editor's lifecycle.
//
// One panel per (context, path) — the panel id encodes both so opening
// the same diff twice just re-activates the existing tab. Params carry
// the resolved before/after strings + display labels; nothing async
// inside the component itself, so init() is essentially synchronous
// once dockview hands us params.

import * as monaco from 'monaco-editor';

export interface DiffViewerParams {
    /** Title for the dock tab. Caller composes context — e.g.
     *  "main.fbasic (Publish preview)" or "main.fbasic (Save #3)". */
    title: string;
    /** Path is informational here — used in placeholder messages when
     *  one side is null ("deleted") or the file is new. */
    path: string;
    /** Monaco language id for syntax highlighting (e.g. "fade",
     *  "json", "plaintext"). */
    languageId: string;
    /** Original ("before") content. Null = file didn't exist on the
     *  before side (added). */
    beforeText: string | null;
    /** New ("after") content. Null = file was deleted. */
    afterText: string | null;
    /** Display labels for the two sides — show up in the editor's
     *  side-by-side header. Defaults: "Before" / "After". */
    beforeLabel?: string;
    afterLabel?: string;
}

export interface DiffViewerComponent {
    element: HTMLElement;
    // Loose param typing mirrors dockview's GroupPanelPartInitParameters
    // (Record<string, any>) so this component is structurally
    // assignable to IContentRenderer. We validate the shape inside.
    init(parameters?: { params?: any }): void;
    update?(event: { params: any }): void;
    dispose(): void;
}

/** Build a dockview-compatible component. `initialParams` is optional
 *  because dockview calls init() with the real params right after
 *  createComponent — but having a default avoids a flash of "no
 *  params" content. */
export function createDiffViewer(initialParams?: DiffViewerParams): DiffViewerComponent {
    const root = document.createElement('div');
    root.style.display = 'flex';
    root.style.flexDirection = 'column';
    root.style.height = '100%';
    root.style.width = '100%';
    // Was `--vscode-editor-background` w/ a #1e1e1e fallback. Those `--vscode-*`
    // vars aren't set in our standalone Monaco build, so the fallback won
    // every time and the diff panel rendered dark even on light themes.
    // Bind to our own palette variables instead.
    root.style.background = 'var(--bg)';

    // Header strip — labels + path. Kept small; the diff editor's own
    // gutters do most of the visual work.
    const header = document.createElement('div');
    header.style.display = 'flex';
    header.style.alignItems = 'center';
    header.style.gap = '12px';
    header.style.padding = '4px 10px';
    header.style.borderBottom = '1px solid var(--border-2)';
    header.style.font = '12px/1.4 ui-sans-serif, system-ui, sans-serif';
    header.style.color = 'var(--fg)';
    header.style.flexShrink = '0';
    const pathSpan = document.createElement('span');
    pathSpan.style.fontFamily = 'ui-monospace, monospace';
    pathSpan.style.opacity = '0.85';
    const sidesSpan = document.createElement('span');
    sidesSpan.style.opacity = '0.55';
    sidesSpan.style.fontSize = '11px';
    header.append(pathSpan, sidesSpan);
    root.append(header);

    const editorHost = document.createElement('div');
    editorHost.style.flex = '1 1 auto';
    editorHost.style.minHeight = '0';
    editorHost.style.position = 'relative';
    root.append(editorHost);

    let editor: monaco.editor.IStandaloneDiffEditor | null = null;
    let originalModel: monaco.editor.ITextModel | null = null;
    let modifiedModel: monaco.editor.ITextModel | null = null;

    function render(params: DiffViewerParams) {
        pathSpan.textContent = params.path;
        const beforeLabel = params.beforeLabel ?? 'Before';
        const afterLabel = params.afterLabel ?? 'After';
        sidesSpan.textContent = `${beforeLabel} → ${afterLabel}`;

        const beforeContent = params.beforeText ?? '';
        const afterContent = params.afterText ?? '';

        if (!editor) {
            // Build a throwaway pair of models — unique URIs so they
            // can't collide with the regular editor's tab models.
            const stamp = Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
            originalModel = monaco.editor.createModel(
                beforeContent,
                params.languageId,
                monaco.Uri.parse(`inmemory://diff-before/${stamp}/${params.path}`),
            );
            modifiedModel = monaco.editor.createModel(
                afterContent,
                params.languageId,
                monaco.Uri.parse(`inmemory://diff-after/${stamp}/${params.path}`),
            );
            editor = monaco.editor.createDiffEditor(editorHost, {
                readOnly: true,
                originalEditable: false,
                automaticLayout: true,
                renderSideBySide: true,
                renderOverviewRuler: true,
                ignoreTrimWhitespace: false,
                // No `theme:` here — Monaco's theme is global. monaco.editor.setTheme()
                // already targets every editor; pinning vs-dark here would freeze the
                // diff viewer to dark even if the user switches to light/DBP/etc.
                fontSize: 12,
            });
            editor.setModel({ original: originalModel, modified: modifiedModel });
        } else {
            // Already initialised — just swap the contents.
            originalModel?.setValue(beforeContent);
            modifiedModel?.setValue(afterContent);
        }

        // Placeholder hints for added / deleted files — Monaco diff
        // alone renders empty-vs-content cleanly but a label makes the
        // intent obvious to a casual viewer.
        if (params.beforeText === null) {
            sidesSpan.textContent = `(added) → ${afterLabel}`;
        } else if (params.afterText === null) {
            sidesSpan.textContent = `${beforeLabel} → (deleted)`;
        }
    }

    if (initialParams) render(initialParams);

    return {
        element: root,
        init(parameters) {
            const p = parameters?.params as DiffViewerParams | undefined;
            if (p && typeof p.path === 'string') render(p);
        },
        update(event) {
            const p = event.params as DiffViewerParams;
            if (p && typeof p.path === 'string') render(p);
        },
        dispose() {
            editor?.dispose();
            editor = null;
            originalModel?.dispose();
            originalModel = null;
            modifiedModel?.dispose();
            modifiedModel = null;
        },
    };
}
