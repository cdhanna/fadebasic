// Catalog tab — dockview panel that browses the FadeLand asset catalog.
//
// Layout:
//   ┌─────────────────────────────────────────────────────────────┐
//   │ FadeLand Catalog · v… · 3 entries          [search]  [↻]    │
//   │ ┌─tag chips──────────────────────────────────────────────┐  │
//   │ │ pack ×4  image ×1  arcade ×0  …                        │  │
//   │ └────────────────────────────────────────────────────────┘  │
//   ├─────────────────────────────────────────────────────────────┤
//   │ ┌─tile─┐ ┌─tile─┐ ┌─tile─┐ ┌─tile─┐                         │
//   │ │      │ │      │ │      │ │      │                         │
//   │ └──────┘ └──────┘ └──────┘ └──────┘                         │
//   │ ┌─ DETAIL (when tile clicked) ───────────────────────────┐  │
//   │ │ preview · description · import · attribution           │  │
//   │ │ (for packs: file list with checkboxes)                 │  │
//   │ └────────────────────────────────────────────────────────┘  │
//   └─────────────────────────────────────────────────────────────┘
//
// Search/tag filtering: live, against the in-memory entry list. Text search
// uses simple substring match against name+slug+tags+description. Tag chips
// AND-intersect with the text filter.
//
// Pack imports: when the user opens a pack detail, the pack manifest is fetched
// lazily and the file list rendered with checkboxes. On import: fetch the zip
// once (sha-verified) → extract only the selected files with fflate → write
// each to OPFS under catalog-imports/<packSlug>/<internalPath>.

import { unzip } from 'fflate';
import type { CatalogClient, CatalogEntry, CatalogPackFile, CatalogPackManifest } from './catalog-client';
import { catalogFilename } from './catalog-client';

export interface CatalogPanelHandle {
    element: HTMLElement;
    init(): void;
    dispose(): void;
}

export interface CatalogPanelDeps {
    client: CatalogClient;
    writeBytes(path: string, bytes: Uint8Array): Promise<void>;
    exists(path: string): Promise<boolean>;
    onImported(path: string): void | Promise<void>;
}

const IMPORT_DIR = 'catalog-imports';
// Tags that show up everywhere (every entry derives them) — surfacing them as
// chips would just dilute the useful ones. Filter them out of the chip strip.
const HIDDEN_TAGS = new Set(['asset', 'image', 'audio', 'pack', 'local', 'remote', 'mirrored']);
const MAX_TAG_CHIPS = 18;

export function createCatalogPanel(deps: CatalogPanelDeps): CatalogPanelHandle {
    const root = document.createElement('div');
    root.className = 'catalog-host';

    // ── Toolbar ───────────────────────────────────────────────────────────
    const toolbar = document.createElement('div');
    toolbar.className = 'catalog-toolbar';

    const title = document.createElement('span');
    title.className = 'catalog-title';
    title.textContent = 'FadeLand Catalog';
    toolbar.append(title);

    const versionLabel = document.createElement('span');
    versionLabel.className = 'catalog-version';
    toolbar.append(versionLabel);

    const spacer = document.createElement('span');
    spacer.style.flex = '1';
    toolbar.append(spacer);

    const searchInput = document.createElement('input');
    searchInput.type = 'search';
    searchInput.placeholder = 'Search…';
    searchInput.className = 'catalog-search-input';
    toolbar.append(searchInput);

    const refreshBtn = document.createElement('button');
    refreshBtn.className = 'catalog-refresh-btn';
    refreshBtn.textContent = 'Refresh';
    refreshBtn.title = 'Force-refresh from jsDelivr (clears local cache)';
    toolbar.append(refreshBtn);

    // ── Tag chip strip ────────────────────────────────────────────────────
    const tagBar = document.createElement('div');
    tagBar.className = 'catalog-tagbar';

    // ── Body / grid ───────────────────────────────────────────────────────
    const body = document.createElement('div');
    body.className = 'catalog-body';

    const grid = document.createElement('div');
    grid.className = 'catalog-grid';
    body.append(grid);

    root.append(toolbar, tagBar, body);

    // ── State ─────────────────────────────────────────────────────────────
    let loaded = false;
    let activeDetail: HTMLElement | null = null;
    let searchText = '';
    const activeTags = new Set<string>();

    function showStatus(message: string, tone: 'info' | 'error' = 'info') {
        grid.innerHTML = '';
        const div = document.createElement('div');
        div.className = tone === 'error' ? 'catalog-status error' : 'catalog-status';
        div.textContent = message;
        grid.append(div);
    }

    async function loadCatalog(force = false) {
        showStatus(force ? 'Refreshing catalog…' : 'Loading catalog…');
        try {
            await deps.client.load(force);
            const m = deps.client.getManifest();
            versionLabel.textContent = `v${m.version} · ${m.entryCount} entries`;
            renderTagBar();
            renderGrid();
            loaded = true;
        } catch (err) {
            const msg = (err as Error).message ?? String(err);
            showStatus(`Couldn't load catalog: ${msg}`, 'error');
        }
    }

    // ── Filtering ─────────────────────────────────────────────────────────
    function passesFilter(e: CatalogEntry): boolean {
        if (activeTags.size > 0) {
            for (const t of activeTags) {
                if (!e.tags.includes(t)) return false;
            }
        }
        if (searchText) {
            const hay = (
                e.name + ' ' + e.slug + ' ' +
                (e.description ?? '') + ' ' +
                e.tags.join(' ')
            ).toLowerCase();
            if (!hay.includes(searchText)) return false;
        }
        return true;
    }

    function visibleEntries(): CatalogEntry[] {
        return deps.client.getEntries().filter(passesFilter);
    }

    function renderTagBar() {
        tagBar.innerHTML = '';
        // Count tag frequency across ALL entries (not just visible) so the
        // chip set is stable as the user filters.
        const counts = new Map<string, number>();
        for (const e of deps.client.getEntries()) {
            for (const t of e.tags) {
                if (HIDDEN_TAGS.has(t)) continue;
                counts.set(t, (counts.get(t) ?? 0) + 1);
            }
        }
        // Sort by count desc, then alphabetical for ties.
        const sorted = [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
        // Always include any currently-active tag, even if it didn't make
        // the top-N cut — otherwise the user can't unclick it.
        const top = new Set(sorted.slice(0, MAX_TAG_CHIPS).map(([t]) => t));
        for (const t of activeTags) top.add(t);
        const ordered = [...top].sort((a, b) => (counts.get(b) ?? 0) - (counts.get(a) ?? 0) || a.localeCompare(b));

        if (ordered.length === 0) {
            const note = document.createElement('span');
            note.className = 'catalog-tagbar-empty';
            note.textContent = '(no tags yet)';
            tagBar.append(note);
            return;
        }
        for (const t of ordered) {
            const chip = document.createElement('button');
            chip.className = 'catalog-tag-chip';
            if (activeTags.has(t)) chip.classList.add('active');
            chip.textContent = `${t}${counts.get(t) ? ` ${counts.get(t)}` : ''}`;
            chip.addEventListener('click', () => {
                if (activeTags.has(t)) activeTags.delete(t);
                else activeTags.add(t);
                renderTagBar();
                renderGrid();
            });
            tagBar.append(chip);
        }
        if (activeTags.size > 0) {
            const clear = document.createElement('button');
            clear.className = 'catalog-tag-clear';
            clear.textContent = `Clear ${activeTags.size}`;
            clear.addEventListener('click', () => {
                activeTags.clear();
                renderTagBar();
                renderGrid();
            });
            tagBar.append(clear);
        }
    }

    // ── Grid ──────────────────────────────────────────────────────────────
    function renderGrid() {
        grid.innerHTML = '';
        activeDetail = null;
        const entries = visibleEntries();
        if (entries.length === 0) {
            const note = document.createElement('div');
            note.className = 'catalog-status';
            note.textContent = deps.client.getEntries().length === 0
                ? 'Catalog has no entries yet.'
                : 'No entries match your filter.';
            grid.append(note);
            return;
        }
        for (const entry of entries) grid.append(renderTile(entry));
    }

    function renderTile(entry: CatalogEntry): HTMLElement {
        const tile = document.createElement('div');
        tile.className = 'catalog-tile';
        tile.dataset.entryId = String(entry.id);

        const thumbWrap = document.createElement('div');
        thumbWrap.className = 'catalog-tile-thumb';
        const thumbUrl = deps.client.getThumbUrl(entry);
        if (thumbUrl) {
            const img = document.createElement('img');
            img.src = thumbUrl;
            img.alt = entry.name;
            img.loading = 'lazy';
            thumbWrap.append(img);
        } else {
            const placeholder = document.createElement('div');
            placeholder.className = 'catalog-tile-placeholder';
            placeholder.textContent = entry.mime.startsWith('audio/') ? '♪'
                : entry.mime.startsWith('font/') ? 'Aa'
                : '·';
            thumbWrap.append(placeholder);
        }
        tile.append(thumbWrap);

        const meta = document.createElement('div');
        meta.className = 'catalog-tile-meta';
        const name = document.createElement('div');
        name.className = 'catalog-tile-name';
        name.textContent = entry.name;
        const sub = document.createElement('div');
        sub.className = 'catalog-tile-sub';
        sub.textContent = describeEntry(entry);
        meta.append(name, sub);
        tile.append(meta);

        if (entry.kind === 'pack') {
            const badge = document.createElement('span');
            badge.className = 'catalog-tile-badge';
            badge.textContent = `PACK · ${entry.fileCount ?? 0}`;
            tile.append(badge);
        }

        tile.addEventListener('click', () => openDetail(entry, tile));
        return tile;
    }

    function describeEntry(entry: CatalogEntry): string {
        if (entry.kind === 'pack') {
            const mb = (entry.bytes / 1024 / 1024).toFixed(2);
            return `${entry.fileCount ?? 0} files · ${mb} MB zip`;
        }
        const parts: string[] = [];
        if (entry.width && entry.height) parts.push(`${entry.width}×${entry.height}`);
        if (entry.durationSec) parts.push(`${entry.durationSec.toFixed(1)}s`);
        parts.push(`${(entry.bytes / 1024).toFixed(1)} KB`);
        return parts.join(' · ');
    }

    // ── Detail card ───────────────────────────────────────────────────────
    function openDetail(entry: CatalogEntry, tile: HTMLElement) {
        if (activeDetail) disposeCard(activeDetail as DetailCard);
        const detail = renderDetailShell(entry);
        tile.after(detail);
        activeDetail = detail;
        detail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });

        if (entry.kind === 'pack') {
            void hydratePackDetail(entry, detail as DetailCard);
        }
    }

    // Cards collect cleanup callbacks (revoke blob URLs, cancel in-flight
    // preview fetches, remove FontFaces from document.fonts) which run when
    // the card is closed or replaced.
    interface DetailCard extends HTMLElement {
        __catalogCleanup?: Array<() => void>;
    }

    // Load a TTF/OTF blob as a FontFace under a sha-derived family name.
    // Returns the family string (callers set it as font-family) and the
    // FontFace instance (callers register cleanup via document.fonts.delete).
    async function loadFontFace(bytes: Uint8Array): Promise<{ family: string; face: FontFace }> {
        const sha = await sha256Hex(bytes);
        const family = `catalog-${sha.slice(0, 16)}`;
        // FontFace expects an ArrayBuffer (not ArrayBufferLike-backed Uint8Array)
        // — same TS narrowing as the Blob path above.
        const ab = new ArrayBuffer(bytes.byteLength);
        new Uint8Array(ab).set(bytes);
        const face = new FontFace(family, ab);
        await face.load();
        document.fonts.add(face);
        return { family, face };
    }
    function disposeCard(card: DetailCard) {
        for (const fn of card.__catalogCleanup ?? []) {
            try { fn(); } catch (e) { console.warn('[catalog] cleanup threw', e); }
        }
        card.__catalogCleanup = [];
        card.remove();
        if (activeDetail === card) activeDetail = null;
    }

    function renderDetailShell(entry: CatalogEntry): HTMLElement {
        const card = document.createElement('div') as DetailCard;
        card.className = 'catalog-detail';
        card.__catalogCleanup = [];

        const header = document.createElement('div');
        header.className = 'catalog-detail-header';
        const h = document.createElement('h3');
        h.textContent = entry.name;
        const close = document.createElement('button');
        close.className = 'catalog-detail-close';
        close.textContent = '✕';
        close.title = 'Close';
        close.addEventListener('click', () => disposeCard(card));
        header.append(h, close);
        card.append(header);

        if (entry.description) {
            const desc = document.createElement('p');
            desc.className = 'catalog-detail-desc';
            desc.textContent = entry.description;
            card.append(desc);
        }

        card.append(renderPreview(entry, card));

        if (entry.kind === 'pack') {
            const pack = document.createElement('div');
            pack.className = 'catalog-pack-files';
            pack.dataset.role = 'pack-files';
            pack.textContent = 'Loading pack file list…';
            card.append(pack);
        }

        card.append(renderActions(entry));
        card.append(renderAttribution(entry));

        return card;
    }

    function renderPreview(entry: CatalogEntry, card?: DetailCard): HTMLElement {
        const wrap = document.createElement('div');
        wrap.className = 'catalog-detail-preview';

        if (entry.kind === 'pack') {
            const summary = document.createElement('div');
            summary.className = 'catalog-pack-summary';
            const thumbUrl = deps.client.getThumbUrl(entry);
            if (thumbUrl) {
                const img = document.createElement('img');
                img.src = thumbUrl;
                img.alt = entry.name;
                summary.append(img);
            }
            const counts = document.createElement('div');
            counts.className = 'catalog-pack-counts';
            const pieces: string[] = [];
            if (entry.imageCount) pieces.push(`<strong>${entry.imageCount}</strong> images`);
            if (entry.audioCount) pieces.push(`<strong>${entry.audioCount}</strong> audio`);
            if (entry.fontCount)  pieces.push(`<strong>${entry.fontCount}</strong> fonts`);
            counts.innerHTML =
                `<div><strong>${entry.fileCount ?? 0}</strong> files</div>` +
                (pieces.length ? `<div>${pieces.join(' · ')}</div>` : '') +
                `<div>${((entry.totalExtractedBytes ?? 0) / 1024 / 1024).toFixed(2)} MB extracted · ${(entry.bytes / 1024 / 1024).toFixed(2)} MB zip</div>`;
            summary.append(counts);
            wrap.append(summary);
            return wrap;
        }

        if (entry.mime.startsWith('image/')) {
            const img = document.createElement('img');
            img.src = deps.client.getAssetUrl(entry);
            img.alt = entry.name;
            img.className = 'catalog-detail-image';
            wrap.append(img);
        } else if (entry.mime.startsWith('audio/')) {
            const audio = document.createElement('audio');
            audio.controls = true;
            audio.preload = 'metadata';
            audio.src = deps.client.getAssetUrl(entry);
            wrap.append(audio);
        } else if (entry.mime.startsWith('font/')) {
            // Fetch the TTF, register a FontFace, render an editable sample
            // in that face. Pre-filled with "fadeland"; user can click and
            // type their own preview text. Async — input shows a placeholder
            // and is disabled until the font loads. Cleanup removes the
            // FontFace from document.fonts when the card closes.
            const sample = document.createElement('input');
            sample.type = 'text';
            sample.className = 'catalog-font-sample loading';
            sample.value = 'fadeland';
            sample.spellcheck = false;
            sample.setAttribute('aria-label', 'Font preview text — editable');
            sample.title = 'Type to change the preview text';
            wrap.append(sample);
            void (async () => {
                try {
                    const bytes = await deps.client.fetchBytes(entry);
                    const { family, face } = await loadFontFace(bytes);
                    card?.__catalogCleanup?.push(() => {
                        try { document.fonts.delete(face); } catch { /* ignore */ }
                    });
                    sample.style.fontFamily = `'${family}', system-ui, sans-serif`;
                    sample.classList.remove('loading');
                } catch (err) {
                    sample.classList.remove('loading');
                    sample.classList.add('error');
                    sample.disabled = true;
                    sample.value = `Couldn't load font: ${(err as Error).message}`;
                }
            })();
        }

        return wrap;
    }

    // Single audio at a time across the whole pack panel — clicking a new
    // play button stops whatever's already playing, mirroring how every
    // audio-grid UI in the world behaves.
    let activeAudio: HTMLAudioElement | null = null;
    let activeAudioBtn: HTMLButtonElement | null = null;

    // For packs: fetch the pack manifest and render the file checklist.
    // Also kicks off a background zip fetch so each image row can lazy-render
    // a thumbnail extracted from the zip — same bytes get reused for Import.
    async function hydratePackDetail(entry: CatalogEntry, card: DetailCard) {
        const filesWrap = card.querySelector('[data-role="pack-files"]') as HTMLElement | null;
        const importBtn = card.querySelector('.catalog-import-btn') as HTMLButtonElement | null;
        if (!filesWrap || !importBtn) return;

        let manifest: CatalogPackManifest;
        try {
            manifest = await deps.client.getPackManifest(entry);
        } catch (err) {
            filesWrap.textContent = `Couldn't load pack contents: ${(err as Error).message}`;
            return;
        }

        const selected = new Set<string>();
        const rowsByPath = new Map<string, HTMLElement>();
        const blobUrls: string[] = [];
        // Extracted audio bytes wait here until the user clicks play; only
        // then do we create a Blob/Audio element (lazy). Avoids spawning
        // dozens of <audio> elements per pack the user never plays.
        const audioBytesByPath = new Map<string, Uint8Array>();
        // FontFaces loaded for font rows — removed from document.fonts when
        // the card closes so we don't leak fonts across panel sessions.
        const fontFaces: FontFace[] = [];
        let cancelled = false;
        card.__catalogCleanup?.push(() => {
            cancelled = true;
            if (activeAudio) {
                activeAudio.pause();
                activeAudio = null;
                activeAudioBtn = null;
            }
            for (const url of blobUrls) URL.revokeObjectURL(url);
            for (const face of fontFaces) {
                try { document.fonts.delete(face); } catch { /* ignore */ }
            }
        });

        // Memoized zip fetch — the preview loader and the import button both
        // call this. Whoever calls first triggers the download; everyone else
        // awaits the same promise. Cleared on cancel so we don't leak the
        // resolved bytes after the card closes.
        let zipPromise: Promise<Uint8Array> | null = null;
        function getZipBytes(): Promise<Uint8Array> {
            if (!zipPromise) zipPromise = deps.client.fetchBytes(entry);
            return zipPromise;
        }

        filesWrap.innerHTML = '';

        // Toolbar above the file list — count + select all / none + preview status.
        const fileToolbar = document.createElement('div');
        fileToolbar.className = 'catalog-pack-files-toolbar';
        const countLabel = document.createElement('span');
        countLabel.className = 'catalog-pack-files-count';
        const previewStatus = document.createElement('span');
        previewStatus.className = 'catalog-pack-files-preview-status';

        const updateCount = () => {
            countLabel.textContent = `${selected.size} of ${manifest.files.length} selected`;
            importBtn.disabled = selected.size === 0;
            importBtn.textContent = selected.size === 0
                ? 'Select files to import…'
                : `Import ${selected.size} file${selected.size === 1 ? '' : 's'} to ${IMPORT_DIR}/${entry.slug}/`;
        };
        const selectAll = document.createElement('button');
        selectAll.className = 'catalog-pack-files-action';
        selectAll.textContent = 'Select all';
        selectAll.addEventListener('click', () => {
            for (const f of manifest.files) selected.add(f.path);
            for (const cb of filesWrap.querySelectorAll<HTMLInputElement>('input[type=checkbox]')) cb.checked = true;
            updateCount();
        });
        const selectNone = document.createElement('button');
        selectNone.className = 'catalog-pack-files-action';
        selectNone.textContent = 'Select none';
        selectNone.addEventListener('click', () => {
            selected.clear();
            for (const cb of filesWrap.querySelectorAll<HTMLInputElement>('input[type=checkbox]')) cb.checked = false;
            updateCount();
        });
        fileToolbar.append(
            countLabel,
            document.createTextNode(' · '), selectAll,
            document.createTextNode(' · '), selectNone,
            previewStatus,
        );
        filesWrap.append(fileToolbar);

        // File rows. Each gets a small thumbnail slot; for images, the slot
        // is filled in after the background zip extraction completes. Audio
        // and other types render a static glyph placeholder.
        const list = document.createElement('div');
        list.className = 'catalog-pack-files-list';
        for (const f of manifest.files) {
            const row = document.createElement('label');
            row.className = 'catalog-pack-file-row';

            const cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.addEventListener('change', () => {
                if (cb.checked) selected.add(f.path); else selected.delete(f.path);
                updateCount();
            });

            const thumb = document.createElement('span');
            thumb.className = 'catalog-pack-file-thumb';
            if (f.mime.startsWith('image/')) {
                const img = document.createElement('img');
                img.alt = '';
                img.loading = 'lazy';
                thumb.append(img);
                thumb.classList.add('catalog-pack-file-thumb-pending');
            } else if (f.mime.startsWith('audio/')) {
                // Play button — disabled until the background zip extract
                // pulls the audio bytes out and stashes them in
                // audioBytesByPath. Lives inside the row's <label>, so
                // stopPropagation prevents the click from toggling the
                // adjacent checkbox.
                const playBtn = document.createElement('button');
                playBtn.type = 'button';
                playBtn.className = 'catalog-pack-audio-play';
                playBtn.textContent = '▶';
                playBtn.disabled = true;
                playBtn.title = `Preview ${f.path}`;
                playBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    e.preventDefault();
                    toggleAudioPreview(f, row, playBtn);
                });
                thumb.append(playBtn);
                thumb.classList.add('catalog-pack-file-thumb-audio', 'catalog-pack-file-thumb-pending');
            } else if (f.mime.startsWith('font/')) {
                // Fonts get a static "Aa" glyph. Real font preview (render
                // sample text in the actual face) would require loading the
                // bytes through FontFace API — a polish item, not v1.
                thumb.textContent = 'Aa';
                thumb.classList.add('catalog-pack-file-thumb-font');
            } else {
                thumb.textContent = '·';
            }

            // Split path so the basename (the actually-useful part) survives
            // truncation. Directory shows muted/dim; collapses to ellipsis
            // first when the row is narrow. Full path is in `title` for hover.
            const path = document.createElement('span');
            path.className = 'catalog-pack-file-path';
            path.title = f.path;
            const lastSlash = f.path.lastIndexOf('/');
            if (lastSlash >= 0) {
                const dir = document.createElement('span');
                dir.className = 'catalog-pack-file-dir';
                dir.textContent = f.path.slice(0, lastSlash + 1);
                path.append(dir);
            }
            const base = document.createElement('span');
            base.className = 'catalog-pack-file-base';
            base.textContent = lastSlash >= 0 ? f.path.slice(lastSlash + 1) : f.path;
            path.append(base);
            const meta = document.createElement('span');
            meta.className = 'catalog-pack-file-meta';
            meta.textContent = describePackFile(f);

            row.append(cb, thumb, path, meta);
            list.append(row);
            rowsByPath.set(f.path, row);
        }
        filesWrap.append(list);

        updateCount();

        // Import handler — reuses zipPromise so we don't redownload if the
        // preview loader already fetched the bytes.
        importBtn.onclick = () => doPackImport(entry, manifest, selected, importBtn, getZipBytes);

        // Background: fetch the zip, extract every image/audio file, then
        // pipe blob URLs (images: immediately into the row's <img>) and
        // stash bytes (audio: in-memory for lazy on-click playback) into
        // the rows. Skip if the user closes the card mid-fetch.
        void loadPackPreviews({
            manifest, rowsByPath, blobUrls, audioBytesByPath, fontFaces, previewStatus,
            getZipBytes,
            isCancelled: () => cancelled,
            packBytes: entry.bytes,
        });

        // Audio toggle handler — lazily creates Audio + blob URL on first
        // click, stops any other audio currently playing, toggles play/pause
        // for repeat clicks.
        function toggleAudioPreview(file: CatalogPackFile, row: HTMLElement, btn: HTMLButtonElement) {
            const bytes = audioBytesByPath.get(file.path);
            if (!bytes) return;

            type RowWithAudio = HTMLElement & { __audio?: HTMLAudioElement };
            const r = row as RowWithAudio;
            if (!r.__audio) {
                const ab = new ArrayBuffer(bytes.byteLength);
                new Uint8Array(ab).set(bytes);
                const blob = new Blob([ab], { type: file.mime });
                const url = URL.createObjectURL(blob);
                blobUrls.push(url);
                const audio = new Audio(url);
                audio.preload = 'metadata';
                audio.addEventListener('ended', () => {
                    btn.textContent = '▶';
                    if (activeAudio === audio) {
                        activeAudio = null;
                        activeAudioBtn = null;
                    }
                });
                r.__audio = audio;
            }
            const audio = r.__audio;

            // If something else is already playing, stop it first.
            if (activeAudio && activeAudio !== audio) {
                activeAudio.pause();
                activeAudio.currentTime = 0;
                if (activeAudioBtn) activeAudioBtn.textContent = '▶';
            }

            if (audio.paused) {
                void audio.play();
                btn.textContent = '⏸';
                activeAudio = audio;
                activeAudioBtn = btn;
            } else {
                audio.pause();
                audio.currentTime = 0;
                btn.textContent = '▶';
                if (activeAudio === audio) {
                    activeAudio = null;
                    activeAudioBtn = null;
                }
            }
        }
    }

    async function loadPackPreviews(ctx: {
        manifest: CatalogPackManifest;
        rowsByPath: Map<string, HTMLElement>;
        blobUrls: string[];
        audioBytesByPath: Map<string, Uint8Array>;
        fontFaces: FontFace[];
        previewStatus: HTMLElement;
        getZipBytes: () => Promise<Uint8Array>;
        isCancelled: () => boolean;
        packBytes: number;
    }) {
        const { manifest, rowsByPath, blobUrls, audioBytesByPath, fontFaces, previewStatus, getZipBytes, isCancelled, packBytes } = ctx;
        const previewable = manifest.files.filter((f) =>
            f.mime.startsWith('image/') || f.mime.startsWith('audio/') || f.mime.startsWith('font/'),
        );
        if (previewable.length === 0) return;

        const mb = (packBytes / 1024 / 1024).toFixed(2);
        previewStatus.textContent = ` · loading previews (${mb} MB zip)…`;

        let zipBytes: Uint8Array;
        try {
            zipBytes = await getZipBytes();
        } catch (err) {
            previewStatus.textContent = ` · preview load failed: ${(err as Error).message}`;
            return;
        }
        if (isCancelled()) return;

        const previewPaths = new Set(previewable.map((f) => f.path));
        let extracted: Record<string, Uint8Array>;
        try {
            extracted = await unzipFiltered(zipBytes, previewPaths);
        } catch (err) {
            previewStatus.textContent = ` · preview extract failed: ${(err as Error).message}`;
            return;
        }
        if (isCancelled()) return;

        const fileByPath = new Map(previewable.map((f) => [f.path, f]));
        let rendered = 0;
        for (const [path, bytes] of Object.entries(extracted)) {
            if (isCancelled()) return;
            const file = fileByPath.get(path);
            const row = rowsByPath.get(path);
            if (!file || !row) continue;
            const thumb = row.querySelector('.catalog-pack-file-thumb');

            if (file.mime.startsWith('image/')) {
                // Round-trip through a tight ArrayBuffer — fflate returns Uint8Array
                // backed by ArrayBufferLike which TS narrows away from BlobPart.
                const ab = new ArrayBuffer(bytes.byteLength);
                new Uint8Array(ab).set(bytes);
                const blob = new Blob([ab], { type: file.mime });
                const url = URL.createObjectURL(blob);
                blobUrls.push(url);
                const img = row.querySelector('img');
                if (img) img.src = url;
            } else if (file.mime.startsWith('audio/')) {
                // Stash bytes; the row's play button creates the Blob/Audio
                // lazily on first click so packs with hundreds of sounds
                // don't spawn hundreds of <audio> elements up-front.
                audioBytesByPath.set(path, bytes);
                const btn = row.querySelector('.catalog-pack-audio-play') as HTMLButtonElement | null;
                if (btn) btn.disabled = false;
            } else if (file.mime.startsWith('font/')) {
                // Load the font and style this row's filename text in its
                // own face — instant "this is what the font looks like"
                // preview using the filename as the sample text.
                try {
                    const { family, face } = await loadFontFace(bytes);
                    fontFaces.push(face);
                    const baseEl = row.querySelector('.catalog-pack-file-base') as HTMLElement | null;
                    const thumbEl = row.querySelector('.catalog-pack-file-thumb') as HTMLElement | null;
                    if (baseEl) baseEl.style.fontFamily = `'${family}', monospace`;
                    if (thumbEl) {
                        thumbEl.style.fontFamily = `'${family}', Georgia, serif`;
                        thumbEl.textContent = 'Aa';
                    }
                } catch { /* font failed to load — leave row with default styling */ }
            }
            thumb?.classList.remove('catalog-pack-file-thumb-pending');
            rendered++;
        }
        previewStatus.textContent = rendered === previewable.length
            ? ''
            : ` · ${rendered}/${previewable.length} previews`;
    }

    function describePackFile(f: CatalogPackFile): string {
        const parts: string[] = [];
        if (f.width && f.height) parts.push(`${f.width}×${f.height}`);
        if (f.durationSec) parts.push(`${f.durationSec.toFixed(1)}s`);
        if (f.channels === 1) parts.push('mono');
        else if (f.channels === 2) parts.push('stereo');
        if (f.sampleRate) parts.push(`${(f.sampleRate / 1000).toFixed(1)}k`);
        parts.push(`${(f.bytes / 1024).toFixed(1)} KB`);
        return parts.join(' · ');
    }

    function renderActions(entry: CatalogEntry): HTMLElement {
        const row = document.createElement('div');
        row.className = 'catalog-detail-actions';

        const importBtn = document.createElement('button');
        importBtn.className = 'catalog-import-btn';
        if (entry.kind === 'pack') {
            importBtn.textContent = 'Select files to import…';
            importBtn.disabled = true;        // hydratePackDetail unlocks this
        } else {
            importBtn.textContent = `Import to ${IMPORT_DIR}/`;
            importBtn.addEventListener('click', () => doAssetImport(entry, importBtn));
        }
        row.append(importBtn);

        if (entry.homepage) {
            const link = document.createElement('a');
            link.href = entry.homepage;
            link.target = '_blank';
            link.rel = 'noreferrer';
            link.className = 'catalog-detail-link';
            link.textContent = 'Source page →';
            row.append(link);
        }

        return row;
    }

    function renderAttribution(entry: CatalogEntry): HTMLElement {
        const div = document.createElement('div');
        div.className = 'catalog-detail-attribution';
        const parts: string[] = [`${entry.license}`];
        if (entry.attribution) parts.push(entry.attribution);
        if (entry.hosting === 'mirrored' && entry.originalUrl) {
            try { parts.push(`hosted by ${new URL(entry.originalUrl).hostname}`); } catch { /* ignore */ }
        }
        div.textContent = parts.join(' · ');
        return div;
    }

    // ── Import: single asset ──────────────────────────────────────────────
    async function doAssetImport(entry: CatalogEntry, btn: HTMLButtonElement) {
        const filename = filenameFor(entry);
        const path = `${IMPORT_DIR}/${filename}`;
        const original = btn.textContent ?? '';
        btn.disabled = true;
        btn.textContent = 'Downloading…';
        try {
            if (await deps.exists(path)) {
                if (!confirm(`${path} already exists. Overwrite?`)) {
                    btn.disabled = false;
                    btn.textContent = original;
                    return;
                }
            }
            const bytes = await deps.client.fetchBytes(entry);
            btn.textContent = 'Writing…';
            await deps.writeBytes(path, bytes);
            btn.textContent = 'Syncing…';
            await deps.onImported(path);
            btn.textContent = '✓ Imported';
            setTimeout(() => {
                btn.textContent = original;
                btn.disabled = false;
            }, 1500);
        } catch (err) {
            const msg = (err as Error).message ?? String(err);
            btn.textContent = `✗ ${msg.slice(0, 60)}`;
            setTimeout(() => {
                btn.textContent = original;
                btn.disabled = false;
            }, 4000);
        }
    }

    // ── Import: pack with selected files ──────────────────────────────────
    async function doPackImport(
        entry: CatalogEntry,
        manifest: CatalogPackManifest,
        selected: Set<string>,
        btn: HTMLButtonElement,
        getZipBytes: () => Promise<Uint8Array>,
    ) {
        const original = btn.textContent ?? '';
        btn.disabled = true;
        btn.textContent = `Downloading zip (${(entry.bytes / 1024 / 1024).toFixed(1)} MB)…`;
        try {
            const zipBytes = await getZipBytes();
            btn.textContent = 'Extracting…';
            const extracted = await unzipFiltered(zipBytes, selected);

            // Per-file sha verification — catches CDN corruption + accidental
            // zip mutations on the upstream end. Cheap (a few SHAs at most).
            const fileByPath = new Map(manifest.files.map((f) => [f.path, f]));
            for (const [path, bytes] of Object.entries(extracted)) {
                const expected = fileByPath.get(path);
                if (!expected) continue; // shouldn't happen — selected drove the filter
                const actual = await sha256Hex(bytes);
                if (actual !== expected.sha256) {
                    throw new Error(`sha mismatch for ${path} (expected ${expected.sha256.slice(0, 12)}…, got ${actual.slice(0, 12)}…)`);
                }
            }

            // Write to OPFS under catalog-imports/<packSlug>/<internalPath>.
            // Preserving the zip's internal path makes it obvious what came
            // from where and avoids collisions across packs.
            let written = 0;
            const total = Object.keys(extracted).length;
            for (const [internalPath, bytes] of Object.entries(extracted)) {
                btn.textContent = `Writing ${++written}/${total}…`;
                const outPath = `${IMPORT_DIR}/${entry.slug}/${internalPath}`;
                await deps.writeBytes(outPath, bytes);
            }
            btn.textContent = 'Syncing…';
            await deps.onImported(`${IMPORT_DIR}/${entry.slug}/`);
            btn.textContent = `✓ Imported ${total} file${total === 1 ? '' : 's'}`;
            setTimeout(() => {
                btn.textContent = original;
                btn.disabled = false;
            }, 2000);
        } catch (err) {
            const msg = (err as Error).message ?? String(err);
            btn.textContent = `✗ ${msg.slice(0, 70)}`;
            setTimeout(() => {
                btn.textContent = original;
                btn.disabled = false;
            }, 5000);
        }
    }

    // ── Search wiring ─────────────────────────────────────────────────────
    let searchDebounce: ReturnType<typeof setTimeout> | null = null;
    searchInput.addEventListener('input', () => {
        if (searchDebounce) clearTimeout(searchDebounce);
        searchDebounce = setTimeout(() => {
            searchText = searchInput.value.trim().toLowerCase();
            renderGrid();
        }, 80);
    });

    refreshBtn.addEventListener('click', () => loadCatalog(true));

    return {
        element: root,
        init() { if (!loaded) void loadCatalog(false); },
        dispose() { /* IDB cache stays warm */ },
    };
}

// fflate-based zip extraction filtered to the selected paths.
function unzipFiltered(zipBytes: Uint8Array, selected: Set<string>): Promise<Record<string, Uint8Array>> {
    return new Promise((resolve, reject) => {
        unzip(
            zipBytes,
            { filter: (file) => selected.has(file.name) },
            (err, files) => {
                if (err) reject(err);
                else resolve(files);
            },
        );
    });
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
    const ab = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(ab).set(bytes);
    const buf = await crypto.subtle.digest('SHA-256', ab);
    return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

function filenameFor(entry: CatalogEntry): string {
    return catalogFilename(entry);
}
