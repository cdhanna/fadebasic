// Binary-file preview panel. One panel per opened binary file, hosted as
// a dockview component named "binary-preview". Mirrors the markdown-preview
// pattern but the source of truth is OPFS bytes (not a Monaco model), and
// dispatch is by extension:
//
//   .xnb                 → header + reader-chain metadata + per-kind payload
//                          preview (Texture2D Color → canvas, SoundEffect PCM
//                          → <audio>)
//   .png/.jpg/.gif/...   → <img src=blob:>
//   .wav/.mp3/.ogg       → <audio src=blob:>
//   anything else        → label-only fallback so future formats fail soft.
//
// Lifecycle: init() reads the bytes once, renders, retains blob URLs;
// dispose() revokes them.

import {
    classifyXnb,
    kindLabel,
    type XnbClassification,
} from './xnb/xnb-reader';
import { decodeSoundEffect, decodeTexture2D } from './xnb/xnb-previews';

export interface BinaryPreviewHandle {
    element: HTMLElement;
    init(parameters?: { params?: { filename?: string } }): void;
    dispose(): void;
    update?(event: { params: { filename?: string } }): void;
}

export interface BinaryPreviewDeps {
    readBytes(filename: string): Promise<Uint8Array>;
}

// Singleton panel id — there's exactly one Asset Preview tab at a time and
// it swaps its contents to whichever binary file was last clicked. Mirrors
// VSCode's behavior for preview tabs.
export const BINARY_PREVIEW_PANEL_ID = 'asset-preview';

// Legacy per-file panel ids looked like `binary-preview:<filename>`. Saved
// dockview layouts from before the single-tab refactor may still reference
// them; healLayout sweeps those out so the user doesn't end up with one
// dead tab per file they ever previewed.
export const LEGACY_BINARY_PREVIEW_ID_PREFIX = 'binary-preview:';

// Extensions the file list routes through the preview pane instead of
// Monaco. Order doesn't matter; lookup is set-membership.
export const BINARY_FILE_EXTENSIONS = new Set<string>([
    'xnb',
    'png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp',
    'wav', 'mp3', 'ogg',
]);

export function isBinaryFileName(name: string): boolean {
    return BINARY_FILE_EXTENSIONS.has(extensionOf(name));
}

export function createBinaryPreview(
    initialFilename: string,
    deps: BinaryPreviewDeps,
): BinaryPreviewHandle {
    const root = document.createElement('div');
    root.className = 'binary-preview-host';
    root.dataset.filename = initialFilename;

    const toolbar = document.createElement('div');
    toolbar.className = 'binary-preview-toolbar';
    const title = document.createElement('span');
    title.className = 'binary-preview-title';
    title.textContent = initialFilename;
    toolbar.append(title);

    const body = document.createElement('div');
    body.className = 'binary-preview-body';

    root.append(toolbar, body);

    // `filename` mutates over the life of the handle — the same panel can
    // be re-targeted at a different file via update({ params: { filename }}).
    // A monotonically-increasing `loadToken` lets an in-flight readBytes()
    // know it's been superseded; the racing tail just discards its bytes
    // when its token no longer matches the live one.
    let filename = initialFilename;
    let loadToken = 0;

    let blobUrls: string[] = [];
    function trackBlob(url: string): string {
        blobUrls.push(url);
        return url;
    }
    function revokeBlobs() {
        for (const url of blobUrls) URL.revokeObjectURL(url);
        blobUrls = [];
    }

    function clear() {
        body.innerHTML = '';
    }

    function showMessage(message: string, tone: 'info' | 'error' = 'info') {
        clear();
        const div = document.createElement('div');
        div.className = tone === 'error' ? 'binary-preview-error' : 'binary-preview-empty';
        div.textContent = message;
        body.append(div);
    }

    // Switch to a different file. Revokes any blob URLs the previous file
    // held and re-renders from scratch.
    async function setFilename(name: string) {
        if (name === filename && loadToken > 0) return;
        filename = name;
        root.dataset.filename = name;
        title.textContent = name;
        revokeBlobs();
        clear();
        await load();
    }

    async function load() {
        const token = ++loadToken;
        // Empty filename = panel was created without an initial target
        // (restored layout w/o params). Paint a friendly placeholder and
        // wait for the next setFilename() to bring a real file in.
        if (!filename) {
            showMessage('Click a binary file in the workspace to preview it here.');
            return;
        }
        try {
            const bytes = await deps.readBytes(filename);
            // Bail if a newer setFilename() superseded this load before
            // readBytes resolved — we'd otherwise paint stale content on
            // top of the newer file's preview.
            if (token !== loadToken) return;
            const ext = extensionOf(filename);
            switch (ext) {
                case 'xnb':   return renderXnb(bytes);
                case 'png':
                case 'jpg':
                case 'jpeg':
                case 'gif':
                case 'webp':
                case 'bmp':   return renderImage(bytes, ext);
                case 'wav':
                case 'mp3':
                case 'ogg':   return renderAudio(bytes, ext);
                default:      return renderFallback(bytes);
            }
        } catch (e: any) {
            if (token !== loadToken) return;
            showMessage('Failed to load: ' + (e?.message ?? e), 'error');
        }
    }

    function renderXnb(bytes: Uint8Array) {
        clear();
        let cls: XnbClassification;
        try {
            cls = classifyXnb(bytes);
        } catch (e: any) {
            showMessage('Failed to read XNB: ' + (e?.message ?? e), 'error');
            return;
        }
        body.append(buildXnbMetaCard(cls));
        if (cls.parseError) {
            body.append(buildEmptyNote(cls.parseError));
            return;
        }
        switch (cls.kind) {
            case 'texture2d': return renderTexture2DPayload(cls);
            case 'sound-effect': return renderSoundPayload(cls);
            default:
                body.append(buildEmptyNote(
                    `No payload preview for ${kindLabel(cls.kind)} yet.`,
                ));
        }
    }

    function renderTexture2DPayload(cls: XnbClassification) {
        const decoded = decodeTexture2D(cls);
        if (!decoded) {
            body.append(buildEmptyNote('Texture payload could not be decoded.'));
            return;
        }
        const meta = document.createElement('div');
        meta.className = 'binary-preview-meta';
        meta.append(
            metaRow('Dimensions', `${decoded.width} × ${decoded.height}`),
            metaRow('Mip levels', String(decoded.mipCount)),
            metaRow('Surface format', decoded.surfaceFormatLabel),
        );
        body.append(meta);
        if (decoded.rgba) {
            const canvas = document.createElement('canvas');
            canvas.width = decoded.width;
            canvas.height = decoded.height;
            canvas.className = 'binary-preview-canvas';
            const ctx = canvas.getContext('2d');
            if (ctx) {
                // ImageData's constructor wants Uint8ClampedArray<ArrayBuffer>
                // but our subarray/views land as Uint8ClampedArray<ArrayBufferLike>
                // because SharedArrayBuffer is in the lib (prompt$ plumbing).
                // Cast to the precise expected type; the runtime check is the
                // length & shape, both of which we built correctly.
                const img = new ImageData(
                    decoded.rgba as Uint8ClampedArray<ArrayBuffer>,
                    decoded.width,
                    decoded.height,
                );
                ctx.putImageData(img, 0, 0);
            }
            body.append(canvas);
        } else if (decoded.notes) {
            body.append(buildEmptyNote(decoded.notes));
        }
    }

    function renderSoundPayload(cls: XnbClassification) {
        const decoded = decodeSoundEffect(cls);
        if (!decoded) {
            body.append(buildEmptyNote('Sound payload could not be decoded.'));
            return;
        }
        const meta = document.createElement('div');
        meta.className = 'binary-preview-meta';
        meta.append(
            metaRow('Format', decoded.formatTagLabel),
            metaRow('Channels', String(decoded.channels)),
            metaRow('Sample rate', `${decoded.sampleRate} Hz`),
            metaRow('Bits / sample', String(decoded.bitsPerSample)),
            metaRow('Duration', `${decoded.durationMs} ms`),
            metaRow('PCM data', formatBytes(decoded.dataLength)),
        );
        body.append(meta);
        if (decoded.wavBytes) {
            const blob = new Blob([decoded.wavBytes as BlobPart], { type: 'audio/wav' });
            const url = trackBlob(URL.createObjectURL(blob));
            const audio = document.createElement('audio');
            audio.controls = true;
            audio.src = url;
            audio.className = 'binary-preview-audio';
            body.append(audio);
        } else if (decoded.notes) {
            body.append(buildEmptyNote(decoded.notes));
        }
    }

    function renderImage(bytes: Uint8Array, ext: string) {
        clear();
        const mime = imageMime(ext);
        const blob = new Blob([bytes as BlobPart], { type: mime });
        const url = trackBlob(URL.createObjectURL(blob));
        const meta = document.createElement('div');
        meta.className = 'binary-preview-meta';
        meta.append(
            metaRow('Type', mime),
            metaRow('Size', formatBytes(bytes.length)),
        );
        body.append(meta);
        const img = document.createElement('img');
        img.src = url;
        img.className = 'binary-preview-image';
        img.alt = filename;
        body.append(img);
    }

    function renderAudio(bytes: Uint8Array, ext: string) {
        clear();
        const mime = audioMime(ext);
        const blob = new Blob([bytes as BlobPart], { type: mime });
        const url = trackBlob(URL.createObjectURL(blob));
        const meta = document.createElement('div');
        meta.className = 'binary-preview-meta';
        meta.append(
            metaRow('Type', mime),
            metaRow('Size', formatBytes(bytes.length)),
        );
        body.append(meta);
        const audio = document.createElement('audio');
        audio.controls = true;
        audio.src = url;
        audio.className = 'binary-preview-audio';
        body.append(audio);
    }

    function renderFallback(bytes: Uint8Array) {
        clear();
        const meta = document.createElement('div');
        meta.className = 'binary-preview-meta';
        meta.append(
            metaRow('Kind', 'Binary file'),
            metaRow('Size', formatBytes(bytes.length)),
        );
        body.append(meta);
        body.append(buildEmptyNote(
            `No preview available for .${extensionOf(filename) || 'unknown'} files yet.`,
        ));
    }

    return {
        element: root,
        // dockview hands us params via init(), NOT via createComponent (the
        // factory hook only gets {id, name}). So when the panel is first
        // created we pull the filename here. If a non-empty initial was
        // baked in at construction time, prefer the init-param filename
        // because it's the actually-requested one for THIS panel instance.
        init(parameters) {
            const initial = parameters?.params?.filename;
            if (typeof initial === 'string' && initial.length > 0 && initial !== filename) {
                void setFilename(initial);
            } else {
                void load();
            }
        },
        // dockview fires update() whenever the panel's params change (via
        // panel.api.updateParameters). We use it to swap the previewed
        // file in-place rather than spawning a per-file panel.
        update(event) {
            const next = event?.params?.filename;
            if (typeof next === 'string' && next.length > 0) {
                void setFilename(next);
            }
        },
        dispose() { revokeBlobs(); },
    };
}

function buildXnbMetaCard(cls: XnbClassification): HTMLElement {
    const meta = document.createElement('div');
    meta.className = 'binary-preview-meta';
    meta.append(
        metaRow('Kind', kindLabel(cls.kind)),
        metaRow('Platform', cls.header.platformLabel),
        metaRow('Format version', String(cls.header.formatVersion)),
        metaRow('Profile', cls.header.isHiDef ? 'HiDef' : 'Reach'),
        metaRow(
            'Compression',
            cls.header.isLz4 ? 'LZ4' : cls.header.isLzx ? 'LZX' : 'None',
        ),
        metaRow('File size', formatBytes(cls.header.fileSize)),
    );
    if (cls.rootReader) {
        meta.append(metaRow('Root reader', cls.rootReader.shortName));
    }
    if (cls.readers.length > 1) {
        meta.append(metaRow('Type readers', String(cls.readers.length)));
    }
    return meta;
}

function buildEmptyNote(text: string): HTMLElement {
    const note = document.createElement('div');
    note.className = 'binary-preview-empty';
    note.textContent = text;
    return note;
}

function metaRow(label: string, value: string): HTMLElement {
    const row = document.createElement('div');
    row.className = 'binary-preview-row';
    const k = document.createElement('span');
    k.className = 'binary-preview-key';
    k.textContent = label;
    const v = document.createElement('span');
    v.className = 'binary-preview-value';
    v.textContent = value;
    row.append(k, v);
    return row;
}

function extensionOf(filename: string): string {
    const i = filename.lastIndexOf('.');
    return i >= 0 ? filename.slice(i + 1).toLowerCase() : '';
}

function imageMime(ext: string): string {
    switch (ext) {
        case 'jpg':
        case 'jpeg': return 'image/jpeg';
        case 'png':  return 'image/png';
        case 'gif':  return 'image/gif';
        case 'webp': return 'image/webp';
        case 'bmp':  return 'image/bmp';
        default:     return 'application/octet-stream';
    }
}

function audioMime(ext: string): string {
    switch (ext) {
        case 'wav': return 'audio/wav';
        case 'mp3': return 'audio/mpeg';
        case 'ogg': return 'audio/ogg';
        default:    return 'application/octet-stream';
    }
}

function formatBytes(n: number): string {
    if (n < 1024) return `${n} B`;
    if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
    return `${(n / 1024 / 1024).toFixed(2)} MB`;
}
