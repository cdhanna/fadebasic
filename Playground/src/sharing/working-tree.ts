// The "working tree" abstraction — the local set of files the Repo engine
// reads from on commit and writes into on checkout. We keep it deliberately
// minimal so the engine can be tested without OPFS (with MemoryWorkingTree)
// and later wired to OpfsWorkspace (with OpfsWorkingTree, written in the
// integration phase).
//
// Paths are forward-slash-delimited project-relative strings; the working
// tree is responsible for translating those into its native storage shape.

export interface WorkingTree {
    /** List every file path currently in the tree (no directories). */
    list(): Promise<string[]>;
    read(path: string): Promise<Uint8Array>;
    write(path: string, bytes: Uint8Array): Promise<void>;
    delete(path: string): Promise<void>;
    has(path: string): Promise<boolean>;
}

// Defensive copies on the way in/out so tests can mutate buffers between
// calls without leaking into the tree's stored bytes.
export class MemoryWorkingTree implements WorkingTree {
    private files = new Map<string, Uint8Array>();

    async list(): Promise<string[]> {
        return [...this.files.keys()].sort();
    }

    async read(path: string): Promise<Uint8Array> {
        const b = this.files.get(path);
        if (!b) throw new Error(`working-tree: file not found: ${path}`);
        return new Uint8Array(b);
    }

    async write(path: string, bytes: Uint8Array): Promise<void> {
        this.files.set(path, new Uint8Array(bytes));
    }

    async delete(path: string): Promise<void> {
        this.files.delete(path);
    }

    async has(path: string): Promise<boolean> {
        return this.files.has(path);
    }
}
