// Bridges the playground's existing `OpfsWorkspace` (which the editor mutates
// directly) to the `WorkingTree` interface the Repo engine expects. Lets us
// share one set of file ops between the live editor and the
// snapshot/commit/checkout machinery.
//
// `OpfsWorkspace` uses text/bytes pairs (read/readBytes, write/writeBytes);
// the engine wants bytes uniformly. We pick `readBytes`/`writeBytes` since
// every file goes through this path regardless of text-vs-binary.

import type { WorkingTree } from './working-tree';

/** Minimal slice of OpfsWorkspace used here — declared structurally so tests
 *  and non-OPFS callers can plug in something else. The real `OpfsWorkspace`
 *  satisfies this without any code changes. */
export interface OpfsWorkspaceLike {
    list(): Promise<string[]>;
    readBytes(name: string): Promise<Uint8Array>;
    writeBytes(name: string, bytes: Uint8Array): Promise<void>;
    delete(name: string): Promise<void>;
}

/** Substrings that disqualify a path from being snapshotted. Used to keep
 *  scratch files (conflict copies, in particular) out of commits. */
const HIDDEN_FROM_COMMITS = ['.fade-conflict.'] as const;

export function isHiddenFromCommits(path: string): boolean {
    return HIDDEN_FROM_COMMITS.some((needle) => path.includes(needle));
}

export class OpfsWorkingTree implements WorkingTree {
    constructor(private readonly ws: OpfsWorkspaceLike) {}

    async list(): Promise<string[]> {
        const all = await this.ws.list();
        // Conflict-copy files written by the resolve-conflict flow must NOT
        // be committed — they're per-machine scratch. Filtering here keeps
        // the engine and the UI honest without each having to know.
        return all.filter((p) => !isHiddenFromCommits(p));
    }

    async read(path: string): Promise<Uint8Array> {
        return await this.ws.readBytes(path);
    }

    async write(path: string, bytes: Uint8Array): Promise<void> {
        await this.ws.writeBytes(path, bytes);
    }

    async delete(path: string): Promise<void> {
        await this.ws.delete(path);
    }

    async has(path: string): Promise<boolean> {
        // OpfsWorkspace doesn't expose existence directly; list() is the
        // canonical source. Callers (the engine) only invoke `has` during
        // checkout's "should I overwrite an existing path?" decision, so a
        // single list per checkout is acceptable cost.
        const names = await this.ws.list();
        return names.includes(path);
    }
}
