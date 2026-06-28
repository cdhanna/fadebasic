// Local save store — accrued snapshots the user can take WITHOUT pushing
// to GitHub. Storage is injectable; production uses OPFS (gigabytes of
// capacity) instead of localStorage (~5MB ceiling that base64-encoded
// binary assets blow through trivially).
//
// Mental model:
//   - "Save" = local checkpoint. Hit save often. Multiple saves accrue.
//   - "Publish" = push to remote. Squashes all accrued work into ONE
//     remote commit so the public history stays clean.
//   - "Save & publish" = save then immediately publish.
//
// Storage is injected so tests can pass a memory-backed implementation
// (`MemorySaveStorage`). Each save record includes `treeHashes` (path →
// git blob sha) so the panel can answer "did the working tree diverge
// from my latest save?" without decoding the base64-encoded file
// contents on every status refresh.

import { gitBlobSha } from './hash';

const SAVES_KEY_PREFIX = 'fade-sharing:saves-v1:';
const MAX_SAVES_PER_PROJECT = 10;

export interface LocalSave {
    /** Stable id — `<timestamp>-<rand>`. Used for revert targeting + UI keys. */
    id: string;
    message: string;
    /** ISO-8601 timestamp at save time. */
    time: string;
    /** path → base64-encoded file bytes. Includes every file in the
     *  working tree at save time (excluding `.fade-conflict.*` scratch). */
    files: Record<string, string>;
    /** path → git blob sha at save time. Lets the panel compare against
     *  the live working tree without decoding `files` on every refresh.
     *  Always populated by `createSave`; legacy saves without this field
     *  are upgraded via `upgradeSave`. */
    treeHashes?: Record<string, string>;
}

/** Minimal interface needed to read / write the working tree. The
 *  playground's `OpfsWorkspace` satisfies this without modification. */
export interface SaveWorkspaceLike {
    list(): Promise<string[]>;
    readBytes(name: string): Promise<Uint8Array>;
    writeBytes(name: string, bytes: Uint8Array): Promise<void>;
    delete(name: string): Promise<void>;
}

/** Storage backend — async because the OPFS-backed implementation can't
 *  do anything synchronously. Tests pass `MemorySaveStorage`. */
export interface SaveStorage {
    getItem(key: string): Promise<string | null>;
    setItem(key: string, value: string): Promise<void>;
    removeItem(key: string): Promise<void>;
}

/** Convenience in-memory storage that satisfies `SaveStorage`. Test
 *  helper; not used in production. */
export class MemorySaveStorage implements SaveStorage {
    private map = new Map<string, string>();
    async getItem(key: string): Promise<string | null> { return this.map.get(key) ?? null; }
    async setItem(key: string, value: string): Promise<void> { this.map.set(key, value); }
    async removeItem(key: string): Promise<void> { this.map.delete(key); }
    /** Test affordance. */
    snapshot(): Record<string, string> {
        return Object.fromEntries(this.map);
    }
}

/** OPFS-backed save storage. One file per key under
 *  `<opfs-root>/fade-saves/<sanitised-key>.json`. OPFS quotas are
 *  typically gigabytes (browser-managed), so a 5MB binary asset
 *  base64-bloated to 7MB no longer blows up the save chain like it
 *  did under localStorage's ~5MB limit. */
class OpfsSaveStorage implements SaveStorage {
    private dirPromise: Promise<FileSystemDirectoryHandle> | null = null;

    private async dir(): Promise<FileSystemDirectoryHandle> {
        if (!this.dirPromise) {
            this.dirPromise = (async () => {
                const root = await navigator.storage.getDirectory();
                return await root.getDirectoryHandle('fade-saves', { create: true });
            })();
        }
        return this.dirPromise;
    }

    private fileName(key: string): string {
        // Mirror localStorage's flat namespace — collapse path-unfriendly
        // chars (the key already starts with our prefix + project name).
        return key.replace(/[^A-Za-z0-9._-]/g, '_') + '.json';
    }

    async getItem(key: string): Promise<string | null> {
        try {
            const fh = await (await this.dir()).getFileHandle(this.fileName(key));
            const file = await fh.getFile();
            return await file.text();
        } catch {
            // File missing or any other error → treat as absent. Saves are
            // a best-effort store; we never want a missing file to break
            // the panel.
            return null;
        }
    }

    async setItem(key: string, value: string): Promise<void> {
        const fh = await (await this.dir()).getFileHandle(this.fileName(key), { create: true });
        const w = await fh.createWritable();
        await w.write(value);
        await w.close();
    }

    async removeItem(key: string): Promise<void> {
        try {
            await (await this.dir()).removeEntry(this.fileName(key));
        } catch { /* already gone */ }
    }
}

/** Stub used when OPFS isn't available (Node test runner, pre-OPFS
 *  browsers). Drops writes on the floor; tests inject their own. */
class NullSaveStorage implements SaveStorage {
    async getItem(): Promise<string | null> { return null; }
    async setItem(): Promise<void> { /* nop */ }
    async removeItem(): Promise<void> { /* nop */ }
}

let _defaultStorage: SaveStorage | null = null;
/** Resolve the production storage backend. Lazy because OPFS isn't a
 *  cheap probe (it triggers a permissions check on some browsers). */
export function defaultSaveStorage(): SaveStorage {
    if (_defaultStorage) return _defaultStorage;
    const hasOpfs = typeof navigator !== 'undefined'
        && typeof navigator.storage !== 'undefined'
        && typeof navigator.storage.getDirectory === 'function';
    _defaultStorage = hasOpfs ? new OpfsSaveStorage() : new NullSaveStorage();
    return _defaultStorage;
}

/** One-shot migration from the legacy localStorage backend. Called by
 *  the panel on first load — copies any existing localStorage save
 *  chains into OPFS, then clears them from localStorage so a future
 *  storage-quota recovery doesn't trip the user up again. Safe to call
 *  multiple times; subsequent calls are no-ops once localStorage is
 *  empty of save keys. */
export async function migrateLegacyLocalStorageSaves(storage: SaveStorage = defaultSaveStorage()): Promise<number> {
    if (typeof localStorage === 'undefined') return 0;
    const keys: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
        const k = localStorage.key(i);
        if (k && k.startsWith(SAVES_KEY_PREFIX)) keys.push(k);
    }
    let moved = 0;
    for (const k of keys) {
        const raw = localStorage.getItem(k);
        if (!raw) continue;
        try {
            await storage.setItem(k, raw);
            localStorage.removeItem(k);
            moved++;
        } catch {
            // Migration is best-effort. Leave the localStorage entry in
            // place so the next attempt can retry.
        }
    }
    return moved;
}

/** Substrings that disqualify a path from save snapshots. Mirrors the
 *  filter the collaboration panel applies to commits — same idea: scratch
 *  conflict-copy files don't belong in a snapshot. */
const HIDDEN_FROM_SAVES = ['.fade-conflict.'] as const;
function isHiddenFromSaves(path: string): boolean {
    return HIDDEN_FROM_SAVES.some((needle) => path.includes(needle));
}

/** List the saves for `project`, newest first. Returns a defensive copy. */
export async function loadSaves(project: string, storage: SaveStorage = defaultSaveStorage()): Promise<LocalSave[]> {
    try {
        const raw = await storage.getItem(SAVES_KEY_PREFIX + project);
        if (!raw) return [];
        const arr = JSON.parse(raw) as LocalSave[];
        return Array.isArray(arr) ? [...arr] : [];
    } catch {
        return [];
    }
}

/** Replace the on-disk save list for `project`. Failures bubble up —
 *  OPFS quotas are huge so a failure here is exceptional and the
 *  caller (createSave) needs to know rather than silently dropping the
 *  user's snapshot like the old localStorage path did. */
async function writeSaves(project: string, saves: LocalSave[], storage: SaveStorage): Promise<void> {
    await storage.setItem(SAVES_KEY_PREFIX + project, JSON.stringify(saves));
}

/** Snapshot the working tree to a new local save. Returns the created
 *  save record. Older saves beyond `MAX_SAVES_PER_PROJECT` are evicted
 *  (oldest-first). The save includes both base64 file contents AND a
 *  `treeHashes` map for fast comparison later. */
export async function createSave(
    project: string,
    workspace: SaveWorkspaceLike,
    message: string,
    storage: SaveStorage = defaultSaveStorage(),
): Promise<LocalSave> {
    const paths = (await workspace.list()).filter((p) => !isHiddenFromSaves(p));
    const files: Record<string, string> = {};
    const treeHashes: Record<string, string> = {};
    for (const path of paths) {
        const bytes = await workspace.readBytes(path);
        files[path] = bytesToBase64(bytes);
        treeHashes[path] = await gitBlobSha(bytes);
    }
    const save: LocalSave = {
        id: `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 7)}`,
        message,
        time: new Date().toISOString(),
        files,
        treeHashes,
    };
    const saves = await loadSaves(project, storage);
    // Newest first.
    saves.unshift(save);
    while (saves.length > MAX_SAVES_PER_PROJECT) saves.pop();
    await writeSaves(project, saves, storage);
    return save;
}

/** Restore the working tree to match `save`. Writes every file the save
 *  contains and deletes any non-save files (except hidden ones like
 *  `.fade-conflict.*` which we leave alone). */
export async function revertToSave(
    workspace: SaveWorkspaceLike,
    save: LocalSave,
): Promise<void> {
    const currentPaths = new Set(await workspace.list());
    for (const [path, b64] of Object.entries(save.files)) {
        await workspace.writeBytes(path, base64ToBytes(b64));
        currentPaths.delete(path);
    }
    for (const path of currentPaths) {
        if (isHiddenFromSaves(path)) continue;
        try { await workspace.delete(path); } catch { /* fade.json guard etc — leave it */ }
    }
}

/** Wipe all saves for a project. Called after a successful Publish so the
 *  pending-saves chip resets. */
export async function clearSaves(project: string, storage: SaveStorage = defaultSaveStorage()): Promise<void> {
    try { await storage.removeItem(SAVES_KEY_PREFIX + project); } catch { /* ignore */ }
}

/** Drop one specific save by id. No-op if not found. */
export async function dropSave(project: string, id: string, storage: SaveStorage = defaultSaveStorage()): Promise<void> {
    const saves = (await loadSaves(project, storage)).filter((s) => s.id !== id);
    await writeSaves(project, saves, storage);
}

/** Compute `treeHashes` for a save record that was written before the
 *  field existed. Returns a NEW save record with the field populated;
 *  caller is responsible for persisting if they want to skip the
 *  recomputation next time. */
export async function upgradeSave(save: LocalSave): Promise<LocalSave> {
    if (save.treeHashes) return save;
    const treeHashes: Record<string, string> = {};
    for (const [path, b64] of Object.entries(save.files)) {
        treeHashes[path] = await gitBlobSha(base64ToBytes(b64));
    }
    return { ...save, treeHashes };
}

// ─── base64 helpers (mirror the adapter's) ──────────────────────────────────

function bytesToBase64(bytes: Uint8Array): string {
    if (typeof Buffer !== 'undefined') return Buffer.from(bytes).toString('base64');
    let bin = '';
    const CHUNK = 0x8000;
    for (let i = 0; i < bytes.length; i += CHUNK) {
        bin += String.fromCharCode.apply(null, bytes.subarray(i, i + CHUNK) as unknown as number[]);
    }
    return btoa(bin);
}

function base64ToBytes(b64: string): Uint8Array {
    const clean = b64.replace(/\s+/g, '');
    if (typeof Buffer !== 'undefined') {
        const buf = Buffer.from(clean, 'base64');
        return new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
    }
    const bin = atob(clean);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}
