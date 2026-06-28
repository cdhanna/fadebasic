import { describe, it, expect } from 'vitest';
import { SlashCommandRegistry, emptySlashState } from './registry';
import type { SlashCommand, SlashContext } from './types';
import { ToolRegistry } from '../tools';

function noopCtx(): Omit<SlashContext, 'lookup' | 'list'> {
    return {
        agent: null,
        provider: null,
        tools: new ToolRegistry(),
        state: emptySlashState(),
        callbacks: {},
    };
}

const sample: SlashCommand = {
    name: 'sample',
    description: 'sample command',
    aliases: ['s'],
    execute(args) {
        return { title: 'Sample', body: `args=${args}` };
    },
};

describe('SlashCommandRegistry.parse', () => {
    it('returns null for non-slash input', () => {
        expect(SlashCommandRegistry.parse('hello')).toBeNull();
        expect(SlashCommandRegistry.parse('')).toBeNull();
        expect(SlashCommandRegistry.parse('  ')).toBeNull();
    });

    it('rejects a bare slash as a command', () => {
        expect(SlashCommandRegistry.parse('/')).toBeNull();
    });

    it('parses bare name commands', () => {
        expect(SlashCommandRegistry.parse('/help')).toEqual({ name: 'help', args: '' });
    });

    it('parses commands with arguments', () => {
        expect(SlashCommandRegistry.parse('/search docs how do sprites rotate')).toEqual({
            name: 'search',
            args: 'docs how do sprites rotate',
        });
    });

    it('trims surrounding whitespace + lowercases name', () => {
        expect(SlashCommandRegistry.parse('   /HELP   ')).toEqual({ name: 'help', args: '' });
    });
});

describe('SlashCommandRegistry.run', () => {
    it('routes to the registered command', async () => {
        const r = new SlashCommandRegistry();
        r.register(sample);
        const result = await r.run('/sample foo bar', () => noopCtx());
        expect(result).toMatchObject({ title: 'Sample', body: 'args=foo bar' });
    });

    it('resolves an alias to the canonical command', async () => {
        const r = new SlashCommandRegistry();
        r.register(sample);
        const result = await r.run('/s', () => noopCtx());
        expect(result).toMatchObject({ title: 'Sample' });
    });

    it('returns an error SlashResult for unknown commands', async () => {
        const r = new SlashCommandRegistry();
        r.register(sample);
        const result = await r.run('/unknown', () => noopCtx());
        expect(result?.variant).toBe('error');
        expect(result?.title).toContain('Unknown command');
    });

    it('returns null when the input is not a slash command', async () => {
        const r = new SlashCommandRegistry();
        const result = await r.run('hello there', () => noopCtx());
        expect(result).toBeNull();
    });

    it('catches command exceptions and surfaces them as errors', async () => {
        const r = new SlashCommandRegistry();
        r.register({
            name: 'boom',
            description: 'throws',
            execute() { throw new Error('kaboom'); },
        });
        const result = await r.run('/boom', () => noopCtx());
        expect(result?.variant).toBe('error');
        expect(result?.body).toContain('kaboom');
    });

    it('list() returns commands sorted by name', () => {
        const r = new SlashCommandRegistry();
        r.register({ name: 'z', description: 'z', execute: () => null });
        r.register({ name: 'a', description: 'a', execute: () => null });
        r.register({ name: 'm', description: 'm', execute: () => null });
        expect(r.list().map(c => c.name)).toEqual(['a', 'm', 'z']);
    });
});
