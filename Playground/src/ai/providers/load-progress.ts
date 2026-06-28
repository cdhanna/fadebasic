// Smooth, monotonic progress for multi-file model downloads.
//
// transformers.js reports 0–100% per file; each new file resets to 0.
// After files are read (from network or IndexedDB), pipeline() still spends
// a long time compiling ONNX + uploading weights to WebGPU with NO progress
// callbacks — we reserve the top of the bar for that "warmup" phase.

export interface RawLoadProgress {
    status?: string;
    file?: string;
    name?: string;
    /** Per-file progress 0–100 from transformers.js. */
    progress?: number;
    loaded?: number;
    total?: number;
}

export interface SmoothedLoadProgress {
    /** 0–1, monotonically non-decreasing until reset(). */
    pct: number;
    /** Human label, e.g. "42% — download model.onnx" */
    text: string;
    /** Integer percent for UI, e.g. 42 */
    pctInt: number;
}

/** File I/O (download or cache read) never exceeds this — warmup uses the rest. */
const FILE_PHASE_MAX = 0.72;
/** Warmup creeps toward this until complete() jumps to 100%. */
const WARMUP_CAP = 0.94;

export class ModelLoadProgressTracker {
    private completedFiles = new Set<string>();
    private filesSeen = new Set<string>();
    private currentFile: string | null = null;
    private currentFilePct = 0;
    private maxPct = 0;
    private phase: 'files' | 'warmup' = 'files';
    private warmupDetail = 'initializing WebGPU session';

    reset(): void {
        this.completedFiles.clear();
        this.filesSeen.clear();
        this.currentFile = null;
        this.currentFilePct = 0;
        this.maxPct = 0;
        this.phase = 'files';
        this.warmupDetail = 'initializing WebGPU session';
    }

    update(raw: RawLoadProgress): SmoothedLoadProgress {
        const file = (raw.file ?? raw.name ?? '').trim();
        const status = (raw.status ?? '').toLowerCase();

        if (file) this.filesSeen.add(file);

        if (status === 'done' && file) {
            this.completedFiles.add(file);
            if (this.currentFile === file) {
                this.currentFile = null;
                this.currentFilePct = 0;
            }
        } else if (file) {
            this.phase = 'files';
            this.currentFile = file;
            if (typeof raw.progress === 'number') {
                this.currentFilePct = clamp01(raw.progress / 100);
            } else if (
                typeof raw.loaded === 'number'
                && typeof raw.total === 'number'
                && raw.total > 0
            ) {
                this.currentFilePct = clamp01(raw.loaded / raw.total);
            }
        }

        const computed = this.computeFilePct();
        this.maxPct = Math.max(this.maxPct, computed);
        const pctInt = Math.round(this.maxPct * 100);
        const detail = formatDetail(status, file, this.currentFile);
        const text = `${pctInt}% — ${detail}`;

        return { pct: this.maxPct, text, pctInt };
    }

    /** Called when file events have gone quiet but pipeline() hasn't resolved. */
    enterWarmup(detail?: string): SmoothedLoadProgress {
        this.phase = 'warmup';
        this.currentFile = null;
        if (detail) this.warmupDetail = detail;
        // Ensure we're at least at the end of the file phase.
        this.maxPct = Math.max(this.maxPct, FILE_PHASE_MAX);
        return this.emitWarmup();
    }

    /** Slow creep during warmup so the bar doesn't look frozen. */
    tickWarmup(): SmoothedLoadProgress {
        if (this.phase !== 'warmup') return this.enterWarmup();
        const next = Math.min(WARMUP_CAP, this.maxPct + 0.008);
        this.maxPct = Math.max(this.maxPct, next);
        return this.emitWarmup();
    }

    /** Call when weights are fully ready — jumps to 100%. */
    complete(label = 'ready'): SmoothedLoadProgress {
        this.maxPct = 1;
        return { pct: 1, text: `100% — ${label}`, pctInt: 100 };
    }

    private emitWarmup(): SmoothedLoadProgress {
        const pctInt = Math.round(this.maxPct * 100);
        const text = `${pctInt}% — ${this.warmupDetail}`;
        return { pct: this.maxPct, text, pctInt };
    }

    private computeFilePct(): number {
        const done = this.completedFiles.size;
        const inFlight = this.currentFile ? 1 : 0;
        const denom = Math.max(this.filesSeen.size, done + inFlight, 1);
        const fileFraction = (done + this.currentFilePct) / denom;
        return Math.min(FILE_PHASE_MAX, fileFraction * FILE_PHASE_MAX);
    }
}

function clamp01(n: number): number {
    if (n < 0) return 0;
    if (n > 1) return 1;
    return n;
}

function formatDetail(status: string, eventFile: string, currentFile: string | null): string {
    const short = shortenPath(currentFile ?? eventFile);
    if (status === 'ready') return 'ready';
    if (status === 'done' && short) return `finished ${short}`;
    if (status === 'download' && short) return `downloading ${short}`;
    if (status === 'progress' && short) return `reading ${short}`;
    if (status === 'initiate') return short ? `preparing ${short}` : 'preparing model';
    if (status && short) return `${status} ${short}`;
    if (short) return short;
    if (status) return status;
    return 'loading model files';
}

function shortenPath(path: string): string {
    if (!path) return '';
    const parts = path.replace(/\\/g, '/').split('/');
    return parts.length <= 2 ? path : parts.slice(-2).join('/');
}
