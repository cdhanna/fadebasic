// Typed tool registry. Each tool defines its arg schema in Zod once; the
// TS type AND the JSON Schema sent to the model are both derived from that
// single source. The agent loop interacts with tools only through the
// `ToolHandle` shape — execution is decoupled from the agent.

import { z, toJSONSchema } from 'zod';
import type { EditReviewRequest, EditReviewResult } from '../edit-review-types';
import type { Tool } from '../providers/types';

/** Workspace interface the tools depend on. Mirrors OpfsWorkspace's public
 *  surface so production wiring is trivial; tests bind to an in-memory impl. */
export interface ToolWorkspace {
    list(): Promise<string[]>;
    read(name: string): Promise<string>;
    write(name: string, content: string): Promise<void>;
    currentProject(): string;
}

/** Optional editor adapter — populated when the playground has a Monaco
 *  editor open. Tools that need editor context degrade gracefully when
 *  it's null. */
export interface EditorAdapter {
    /** Path of the currently-focused tab, or null. */
    activeFile(): string | null;
    /** Current cursor line (0-indexed) in the active editor, or null. */
    cursorLine(): number | null;
    /** Selected text in the active editor, or empty string. */
    selectionText(): string;
}

export type DiagnosticSeverity = 'error' | 'warning' | 'info' | 'hint';

/** A normalized LSP diagnostic — converted from Monaco markers (or the
 *  raw LSP envelope) into the shape we hand to the model. Positions are
 *  1-indexed because that matches what users read in editors. */
export interface DiagnosticEntry {
    /** Workspace-relative path, e.g. "main.fade". */
    path: string;
    severity: DiagnosticSeverity;
    /** 1-indexed inclusive. */
    line: number;
    column: number;
    endLine: number;
    endColumn: number;
    message: string;
    /** LSP diagnostic code (e.g. error number), if any. */
    code?: string;
    /** Producer — usually "fade". */
    source?: string;
}

/** Adapter the agent uses to read LSP diagnostics. Returns empty arrays
 *  when the LSP hasn't reported anything for the requested file — null is
 *  not a valid return. */
export interface DiagnosticsProvider {
    /** All diagnostics across every file the LSP has analyzed. */
    getAll(): Promise<DiagnosticEntry[]>;
    /** Diagnostics for a single workspace-relative path. Empty when the
     *  file is unknown or clean. */
    forFile(path: string): Promise<DiagnosticEntry[]>;
}

export interface ToolContext {
    workspace: ToolWorkspace;
    editor?: EditorAdapter;
    diagnostics?: DiagnosticsProvider;
    /** Optional pre-approval reviewer — runs before confirmEdit. When it
     *  rejects, the tool returns feedback to the agent (user never sees a
     *  diff). */
    reviewEdit?: (req: EditReviewRequest) => Promise<EditReviewResult>;
    onEditReviewStart?: () => void;
    onEditReviewEnd?: () => void;
    onEditReviewPhase?: (phase: 'lsp' | 'llm') => void;
    /** Set by the agent loop so long-running tool steps can honour abort. */
    abortSignal?: AbortSignal;
    /** When set, write_file / apply_edit must call this and await its
     *  resolution before writing. Resolves to true to proceed, false to
     *  reject the change. */
    confirmEdit?: (path: string, oldContent: string, newContent: string) => Promise<boolean>;
    /** Returns the active project's `type` (e.g. 'web', 'monogame') or
     *  undefined when nothing is selected. Tools that surface docs gate
     *  their queries on this so MonoGame-only content doesn't leak into
     *  a web project. */
    projectType?: () => string | undefined;
    /** Access to the shared asset Catalog (search + import). Optional. */
    catalog?: CatalogToolApi;
    /** LSP-check a standalone Fade snippet (e.g. code shown in an answer, not
     *  applied through a write tool). Returns diagnostics. Optional. */
    lintFadeSnippet?: (source: string) => Promise<DiagnosticEntry[]>;
}

/** A catalog entry as surfaced to the agent — a trimmed projection of the
 *  full CatalogEntry (the model doesn't need URLs, hashes, etc.). */
export interface CatalogToolEntry {
    id: number;
    name: string;
    kind: 'asset' | 'pack';
    mime: string;
    tags: string[];
    description: string | null;
    bytes: number;
    license: string;
}

/** Agent access to the shared asset Catalog. Optional — tools degrade
 *  gracefully when the playground hasn't wired a catalog (e.g. in tests). */
export interface CatalogToolApi {
    search(query: string, opts?: {
        kind?: 'asset' | 'pack';
        category?: 'image' | 'audio' | 'font';
        tags?: string[];
        limit?: number;
    }): Promise<CatalogToolEntry[]>;
    /** Import a catalog entry into the current project. Returns the
     *  workspace-relative path(s) written. Throws on failure. */
    import(id: number): Promise<{ name: string; paths: string[] }>;
}

/** Result of a tool execution. Successful tools return arbitrary JSON;
 *  failed tools return an error object the agent should feed back to the
 *  model. */
export interface ToolResult {
    ok: boolean;
    /** Stringified for inclusion in the next prompt. */
    result: unknown;
}

export interface ToolHandle<TSchema extends z.ZodTypeAny = z.ZodTypeAny> {
    name: string;
    description: string;
    schema: TSchema;
    /** True if this tool is side-effect-free and safe to run in parallel
     *  with other read-only calls in the same iteration. apply_edit /
     *  create_file are NOT read-only — they require user confirmation. */
    readOnly?: boolean;
    /** Execute the tool with parsed, validated args. */
    execute(args: z.infer<TSchema>, ctx: ToolContext): Promise<ToolResult>;
}

/** Helper that preserves the schema's generic so `args` in `execute` is
 *  properly typed. Use this instead of annotating the tool const directly. */
export function defineTool<TSchema extends z.ZodTypeAny>(
    tool: ToolHandle<TSchema>,
): ToolHandle<TSchema> {
    return tool;
}

export class ToolRegistry {
    private tools = new Map<string, ToolHandle>();

    register<T extends z.ZodTypeAny>(tool: ToolHandle<T>): void {
        this.tools.set(tool.name, tool as ToolHandle);
    }

    get(name: string): ToolHandle | undefined {
        return this.tools.get(name);
    }

    /** All tools, in registration order. */
    list(): ToolHandle[] {
        return [...this.tools.values()];
    }

    /** Public-facing descriptions sent to the model. */
    describe(): Tool[] {
        return this.list().map(t => ({
            name: t.name,
            description: t.description,
            schema: zodToJsonSchema(t.schema),
        }));
    }

    /** Validate args against the tool's schema and execute, or return a
     *  validation error as a failed ToolResult. */
    async run(name: string, rawArgs: unknown, ctx: ToolContext): Promise<ToolResult> {
        const tool = this.tools.get(name);
        if (!tool) {
            return { ok: false, result: { error: `Unknown tool: ${name}` } };
        }
        const parsed = tool.schema.safeParse(rawArgs ?? {});
        if (!parsed.success) {
            return {
                ok: false,
                result: {
                    error: 'Invalid arguments',
                    issues: parsed.error.issues.map(i => ({
                        path: i.path.join('.'),
                        message: i.message,
                    })),
                },
            };
        }
        try {
            return await tool.execute(parsed.data, ctx);
        } catch (e) {
            return {
                ok: false,
                result: { error: (e as Error).message ?? String(e) },
            };
        }
    }
}

// ─── Zod → JSON Schema ─────────────────────────────────────────────────────
//
// Zod 4 ships with toJSONSchema — we use it directly. We strip the $schema
// preamble since small models don't read it and it bloats the prompt.

export function zodToJsonSchema(schema: z.ZodTypeAny): Record<string, unknown> {
    const result = toJSONSchema(schema, { unrepresentable: 'any' }) as Record<string, unknown>;
    delete result.$schema;
    return result;
}
