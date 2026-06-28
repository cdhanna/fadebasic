// Tiny channel-aware log bus. App-wide singleton (`appLog`) plus
// `createLogBus()` for tests. Producers call `getLogger(channel).info(...)`;
// consumers (the Logs panel, primarily) `subscribe()` for live updates or
// `snapshot()` for a backfill on mount.
//
// Why a bus instead of just `console.log`? Two reasons:
//   1. The Logs panel can filter by channel and level, persist a history,
//      and survive panel re-renders without losing earlier entries.
//   2. We can route certain channels to the existing Output panel or to
//      Playwright probes without each producer caring where its output goes.
//
// Capacity is bounded so a long session doesn't grow memory unboundedly.
// Default 2000 entries — a few minutes of busy sharing activity.

export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface LogEntry {
    /** Date.now() at emission. */
    time: number;
    level: LogLevel;
    /** Producer channel name — e.g. 'sharing', 'editor', 'lsp'. */
    channel: string;
    message: string;
    /** Optional step-of-N progress info for long operations. */
    progress?: { current: number; total: number };
}

export type LogListener = (entry: LogEntry) => void;

export interface LogBus {
    /** Emit a log entry. Time is stamped by the bus. */
    emit(entry: Omit<LogEntry, 'time'>): void;
    /** Subscribe for live entries. Returns an unsubscribe fn. */
    subscribe(listener: LogListener): () => void;
    /** Return a snapshot of the entries currently in the buffer, optionally
     *  filtered. Newest entry last. */
    snapshot(opts?: { channels?: string[]; levels?: LogLevel[]; limit?: number }): LogEntry[];
    /** Channels seen so far this session (for UI filter rendering). */
    channels(): string[];
    /** Clear the buffer. If `channel` is specified, only entries in that
     *  channel are removed. */
    clear(channel?: string): void;
}

export interface Logger {
    debug(message: string, progress?: { current: number; total: number }): void;
    info(message: string, progress?: { current: number; total: number }): void;
    warn(message: string, progress?: { current: number; total: number }): void;
    error(message: string): void;
}

export function createLogBus(opts: { capacity?: number } = {}): LogBus {
    const capacity = Math.max(50, opts.capacity ?? 2000);
    const buf: LogEntry[] = [];
    const listeners = new Set<LogListener>();
    const seenChannels = new Set<string>();

    return {
        emit(partial) {
            const entry: LogEntry = { time: Date.now(), ...partial };
            buf.push(entry);
            seenChannels.add(entry.channel);
            // Trim head when over capacity; cheap because we only drop a
            // batch when we exceed the high-water mark.
            if (buf.length > capacity) buf.splice(0, buf.length - capacity);
            for (const l of listeners) {
                try { l(entry); } catch (e) { /* listener errors must not crash producers */ console.error('[log-bus] listener threw', e); }
            }
        },
        subscribe(listener) {
            listeners.add(listener);
            return () => { listeners.delete(listener); };
        },
        snapshot(opts = {}) {
            let out: LogEntry[] = buf;
            if (opts.channels && opts.channels.length > 0) {
                const wanted = new Set(opts.channels);
                out = out.filter((e) => wanted.has(e.channel));
            }
            if (opts.levels && opts.levels.length > 0) {
                const wanted = new Set(opts.levels);
                out = out.filter((e) => wanted.has(e.level));
            }
            if (opts.limit !== undefined && opts.limit >= 0 && out.length > opts.limit) {
                out = out.slice(out.length - opts.limit);
            }
            return [...out];
        },
        channels() {
            return [...seenChannels].sort();
        },
        clear(channel) {
            if (channel) {
                for (let i = buf.length - 1; i >= 0; i--) {
                    if (buf[i].channel === channel) buf.splice(i, 1);
                }
            } else {
                buf.length = 0;
            }
        },
    };
}

export function makeLogger(bus: LogBus, channel: string): Logger {
    return {
        debug(message, progress) { bus.emit({ level: 'debug', channel, message, progress }); },
        info(message, progress)  { bus.emit({ level: 'info',  channel, message, progress }); },
        warn(message, progress)  { bus.emit({ level: 'warn',  channel, message, progress }); },
        error(message)           { bus.emit({ level: 'error', channel, message }); },
    };
}

/** App-wide default bus. The Logs dockview panel subscribes here at mount;
 *  producers (sharing, future editor/LSP integrations) emit through
 *  `getLogger(channel)`. */
export const appLog: LogBus = createLogBus({ capacity: 2000 });

export function getLogger(channel: string): Logger {
    return makeLogger(appLog, channel);
}
