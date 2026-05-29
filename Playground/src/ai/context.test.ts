import { describe, it, expect } from 'vitest';
import { ContextEvictor, SUMMARY_PREFIX } from './context';
import { MockProvider, mockTurn } from './providers/mock';
import type { Msg } from './providers/types';

/** Build a system message of a fixed token budget impact for the tests. */
function sys(content: string): Msg {
    return { role: 'system', content };
}

function user(content: string): Msg {
    return { role: 'user', content };
}

function asst(content: string): Msg {
    return { role: 'assistant', content };
}

function toolResult(name: string, body: string): Msg {
    return { role: 'user', content: `<tool_result name="${name}">${body}</tool_result>` };
}

function repeat(s: string, n: number): string {
    return s.repeat(n);
}

describe('ContextEvictor — no-op when under budget', () => {
    it('does nothing if usage is below evictAt', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 1000, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({ provider, evictAt: 0.7 });
        const history: Msg[] = [user('hi'), asst('hello')];
        const before = JSON.stringify(history);

        const result = await evictor.evictToBudget(sys('S'), history);
        expect(result.saved).toBe(0);
        expect(JSON.stringify(history)).toBe(before);
    });
});

describe('ContextEvictor — Strategy 1: elision', () => {
    it('elides old tool result bodies, preserving the call marker', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 2,
            enableSummarization: false,
        });

        const bigBody = repeat('payload ', 80);
        const history: Msg[] = [
            user('first question'),
            asst('let me look'),
            toolResult('read_file', bigBody),
            asst('here is the answer'),
            user('current question'),
            asst('current answer'),
        ];

        const result = await evictor.evictToBudget(sys('S'), history);

        expect(result.elided).toBeGreaterThanOrEqual(1);
        // The tool result message should still exist as a marker
        const stub = history.find(m => m.role === 'user' && m.content.includes('<tool_result name="read_file">'));
        expect(stub).toBeDefined();
        expect(stub!.content).toContain('elided');
        expect(stub!.content).not.toContain('payload payload payload');
    });

    it('does not touch tool results within the keep-recent window', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 4,
            enableSummarization: false,
        });

        const bigBody = repeat('payload ', 80);
        const history: Msg[] = [
            user('q1'),
            asst('a1'),
            toolResult('read_file', bigBody),  // index 2 — within keep window (last 4)
            asst('final'),
        ];

        await evictor.evictToBudget(sys('S'), history);
        // The tool result should NOT be elided — it's in the keep window
        expect(history[2].content).toContain(bigBody);
    });
});

describe('ContextEvictor — Strategy 2: summarization', () => {
    it('replaces old turns with a [SUMMARY] system message', async () => {
        const provider = new MockProvider([
            mockTurn.text('- Decided to use FOR loops\n- Touched main.fade\n- Open: optimize render'),
        ], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 2,
            enableSummarization: true,
        });

        const history: Msg[] = [
            user(repeat('old question content ', 5)),
            asst(repeat('old answer content ', 5)),
            user(repeat('old followup content ', 5)),
            asst(repeat('old followup answer ', 5)),
            user('current'),
            asst('current reply'),
        ];

        const result = await evictor.evictToBudget(sys('S'), history);
        expect(result.summarized).toBeGreaterThan(0);

        // The first message should now be a [SUMMARY]
        expect(history[0].role).toBe('system');
        expect(history[0].content.startsWith(SUMMARY_PREFIX)).toBe(true);
        expect(history[0].content).toContain('FOR loops');

        // The last two messages should be preserved verbatim
        expect(history[history.length - 1].content).toBe('current reply');
        expect(history[history.length - 2].content).toBe('current');
    });

    it('skips summarization if disabled, falls through to hard-drop', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 2,
            enableSummarization: false,
        });

        const history: Msg[] = [
            user(repeat('old question content ', 30)),    // ~600 chars = ~150 tokens
            asst(repeat('old answer content ', 30)),      // ~570 chars = ~143 tokens
            user('current'),
            asst('current reply'),
        ];

        const result = await evictor.evictToBudget(sys('S'), history);
        expect(result.summarized).toBe(0);
        expect(result.dropped).toBeGreaterThan(0);
        // Last two preserved
        expect(history[history.length - 1].content).toBe('current reply');
        expect(history[history.length - 2].content).toBe('current');
    });
});

describe('ContextEvictor — Strategy 3: hard drop', () => {
    it('drops the oldest pair when nothing else gets us under target', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.4,
            evictTo: 0.2,
            keepRecentMessages: 2,
            enableSummarization: false,
        });

        const before: Msg[] = [
            user(repeat('drop me ', 30)),    // ~240 chars = ~60 tokens
            asst(repeat('drop too ', 30)),
            user('keep'),
            asst('keep'),
        ];

        const result = await evictor.evictToBudget(sys('S'), before);
        expect(result.dropped).toBeGreaterThanOrEqual(1);
        expect(before[before.length - 1].content).toBe('keep');
        expect(before[before.length - 2].content).toBe('keep');
        // The original "drop me" content should be gone
        expect(before.find(m => m.content.startsWith('drop me'))).toBeUndefined();
    });

    it('preserves a leading [SUMMARY] when dropping', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 300, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.4,
            evictTo: 0.2,
            keepRecentMessages: 2,
            enableSummarization: false,
        });

        const history: Msg[] = [
            { role: 'system', content: SUMMARY_PREFIX + 'older context: foo' },
            user(repeat('drop me ', 30)),
            asst(repeat('drop me too ', 30)),
            user('keep'),
            asst('keep'),
        ];

        await evictor.evictToBudget(sys('S'), history);
        // Summary should still be at position 0
        expect(history[0].content.startsWith(SUMMARY_PREFIX)).toBe(true);
        // Last two preserved
        expect(history[history.length - 1].content).toBe('keep');
    });
});

describe('ContextEvictor — ordering', () => {
    it('tries elision before summarization', async () => {
        const provider = new MockProvider([
            mockTurn.text('summary text'),
        ], {
            capabilities: { maxContext: 300, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 2,   // keep last 2 = current user + asst
            enableSummarization: true,
        });

        // The tool_result must be OUTSIDE the keep window — so put two
        // more messages after it before the "current" pair.
        const history: Msg[] = [
            user('q1'),
            asst('a1'),
            toolResult('read_file', repeat('big tool body ', 40)),  // ~600 chars
            asst('done looking'),
            user('current'),
            asst('current reply'),
        ];

        const result = await evictor.evictToBudget(sys('S'), history);
        // Elision should run first
        expect(result.elided).toBeGreaterThan(0);
    });
});

describe('ContextEvictor — event emission', () => {
    it('emits started and done events in order', async () => {
        const provider = new MockProvider([], {
            capabilities: { maxContext: 200, supportsTools: false, isCached: true },
        });
        const evictor = new ContextEvictor({
            provider,
            evictAt: 0.5,
            evictTo: 0.3,
            keepRecentMessages: 2,
            enableSummarization: false,
        });

        const kinds: string[] = [];
        evictor.on(e => { kinds.push(e.kind); });

        const history: Msg[] = [
            user('q'),
            asst('a'),
            toolResult('read_file', repeat('body ', 200)),  // ~1000 chars
            asst('done'),
            user('current'),
            asst('current reply'),
        ];

        await evictor.evictToBudget(sys('S'), history);
        expect(kinds[0]).toBe('started');
        expect(kinds[kinds.length - 1]).toBe('done');
        expect(kinds).toContain('elided');
    });
});
