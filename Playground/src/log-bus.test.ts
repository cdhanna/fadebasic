import { describe, it, expect, vi } from 'vitest';
import { createLogBus, makeLogger, type LogEntry } from './log-bus';

describe('createLogBus', () => {
    it('emit + snapshot round-trips entries in order', () => {
        const bus = createLogBus();
        bus.emit({ level: 'info', channel: 'a', message: 'one' });
        bus.emit({ level: 'warn', channel: 'a', message: 'two' });
        const out = bus.snapshot();
        expect(out.map((e) => e.message)).toEqual(['one', 'two']);
        expect(out.every((e) => typeof e.time === 'number' && e.time > 0)).toBe(true);
    });

    it('subscribe receives live entries', () => {
        const bus = createLogBus();
        const seen: LogEntry[] = [];
        const unsub = bus.subscribe((e) => seen.push(e));
        bus.emit({ level: 'info', channel: 'a', message: 'live' });
        unsub();
        bus.emit({ level: 'info', channel: 'a', message: 'after-unsub' });
        expect(seen.map((e) => e.message)).toEqual(['live']);
    });

    it('snapshot filters by channel', () => {
        const bus = createLogBus();
        bus.emit({ level: 'info', channel: 'a', message: '1' });
        bus.emit({ level: 'info', channel: 'b', message: '2' });
        bus.emit({ level: 'info', channel: 'a', message: '3' });
        const out = bus.snapshot({ channels: ['a'] });
        expect(out.map((e) => e.message)).toEqual(['1', '3']);
    });

    it('snapshot filters by level', () => {
        const bus = createLogBus();
        bus.emit({ level: 'debug', channel: 'a', message: 'dbg' });
        bus.emit({ level: 'error', channel: 'a', message: 'err' });
        bus.emit({ level: 'info', channel: 'a', message: 'inf' });
        const out = bus.snapshot({ levels: ['info', 'error'] });
        expect(out.map((e) => e.level)).toEqual(['error', 'info']);
    });

    it('snapshot honors limit (newest entries kept)', () => {
        const bus = createLogBus();
        for (let i = 0; i < 10; i++) bus.emit({ level: 'info', channel: 'a', message: `m${i}` });
        const out = bus.snapshot({ limit: 3 });
        expect(out.map((e) => e.message)).toEqual(['m7', 'm8', 'm9']);
    });

    it('capacity trims oldest entries', () => {
        const bus = createLogBus({ capacity: 50 });           // floor is 50
        for (let i = 0; i < 80; i++) bus.emit({ level: 'info', channel: 'a', message: `m${i}` });
        const out = bus.snapshot();
        expect(out.length).toBe(50);
        expect(out[0].message).toBe('m30');
        expect(out[out.length - 1].message).toBe('m79');
    });

    it('channels() returns unique sorted channel names', () => {
        const bus = createLogBus();
        bus.emit({ level: 'info', channel: 'editor',  message: '' });
        bus.emit({ level: 'info', channel: 'sharing', message: '' });
        bus.emit({ level: 'info', channel: 'editor',  message: '' });
        expect(bus.channels()).toEqual(['editor', 'sharing']);
    });

    it('clear() with no channel wipes the whole buffer', () => {
        const bus = createLogBus();
        bus.emit({ level: 'info', channel: 'a', message: 'x' });
        bus.clear();
        expect(bus.snapshot()).toEqual([]);
    });

    it('clear(channel) only drops entries on that channel', () => {
        const bus = createLogBus();
        bus.emit({ level: 'info', channel: 'a', message: 'A1' });
        bus.emit({ level: 'info', channel: 'b', message: 'B1' });
        bus.emit({ level: 'info', channel: 'a', message: 'A2' });
        bus.clear('a');
        expect(bus.snapshot().map((e) => e.message)).toEqual(['B1']);
    });

    it('listener exceptions don\'t crash other listeners or producers', () => {
        const bus = createLogBus();
        const consoleErr = vi.spyOn(console, 'error').mockImplementation(() => {});
        bus.subscribe(() => { throw new Error('bad listener'); });
        const seen: string[] = [];
        bus.subscribe((e) => seen.push(e.message));
        bus.emit({ level: 'info', channel: 'a', message: 'ok' });
        expect(seen).toEqual(['ok']);
        consoleErr.mockRestore();
    });
});

describe('makeLogger', () => {
    it('routes each level method through the bus with the bound channel', () => {
        const bus = createLogBus();
        const log = makeLogger(bus, 'sharing');
        log.info('hi');
        log.warn('careful');
        log.error('oops');
        log.debug('dbg');
        const out = bus.snapshot();
        expect(out.map((e) => [e.level, e.channel, e.message])).toEqual([
            ['info',  'sharing', 'hi'],
            ['warn',  'sharing', 'careful'],
            ['error', 'sharing', 'oops'],
            ['debug', 'sharing', 'dbg'],
        ]);
    });

    it('passes through progress info on info/debug calls', () => {
        const bus = createLogBus();
        const log = makeLogger(bus, 'sharing');
        log.info('uploading', { current: 3, total: 12 });
        const out = bus.snapshot();
        expect(out[0].progress).toEqual({ current: 3, total: 12 });
    });
});
