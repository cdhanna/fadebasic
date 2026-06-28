/**
 * Multi-connection probe: one GhostBot hub, TWO Playground peers on the same
 * code. Verifies (1) both peers pair + get approved, and (2) per-peer
 * addressing — each peer receives ONLY its own stream's tokens, never the
 * other's. This is the core of the multi-connection + no-cross-leak fix.
 *
 * Requires Playground dev server (5311) and ghost vite (1420).
 */
import { chromium } from 'playwright';

const CODE = 'MULTI' + Math.floor(Math.random() * 10);
const TIMEOUT = 75_000;

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const ghostPage = await ctx.newPage();
const pgA = await ctx.newPage();
const pgB = await ctx.newPage();
const logs = [];
for (const [p, tag] of [[ghostPage, 'ghost'], [pgA, 'A'], [pgB, 'B']]) {
    p.on('pageerror', e => logs.push(`[${tag}] PE ${e.message}`));
}

let verdict = null;
try {
    await ghostPage.goto('http://localhost:1420/', { waitUntil: 'domcontentloaded' });
    await pgA.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });
    await pgB.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });

    // Ghost: hub that echoes the requesting peer's prompt back, auto-approves.
    const ghostRun = ghostPage.evaluate(async ({ code, timeoutMs }) => {
        const { createGhostHub } = await import('/src/session.ts');
        const hub = createGhostHub(code);
        hub.setModelStatus({ loaded: true, name: 'probe-model' });
        // Echo the user content so each peer can verify it got ITS reply.
        hub.setStreamHandler(async function* (req) {
            const user = req.messages.find(m => m.role === 'user')?.content ?? '?';
            yield { kind: 'text', delta: `reply-to:${user}` };
            yield { kind: 'done', finishReason: 'stop' };
        });
        hub.onChange(() => {
            for (const c of hub.listConnections()) if (!c.approved) hub.approve(c.peerId);
        });
        hub.start();
        await new Promise(r => {
            const t = setTimeout(r, timeoutMs);
            hub.onChange(() => { if (hub.listConnections().filter(c => c.approved).length >= 2) { clearTimeout(t); r(); } });
        });
        return { conns: hub.listConnections().length };
    }, { code: CODE, timeoutMs: TIMEOUT });

    const peerRun = (page, who) => page.evaluate(async ({ code, who, timeoutMs }) => {
        const { joinGhostRoom } = await import('/src/ai/providers/ghostbot-transport.ts');
        const room = await joinGhostRoom(code, { clientId: 'client-' + who, label: 'Peer ' + who });
        const approved = await new Promise(resolve => {
            const t = setTimeout(() => resolve(false), timeoutMs);
            room.onMessage(m => {
                if (m.type === 'pong' || (m.type === 'auth' && m.status === 'approved')) { clearTimeout(t); resolve(true); }
            });
        });
        // Each peer asks with a unique marker; collect ALL stream-event text
        // it receives (to detect cross-talk from the other peer's stream).
        const received = [];
        const text = await new Promise(resolve => {
            const t = setTimeout(() => resolve('TIMEOUT'), 20_000);
            let acc = '';
            room.onMessage(m => {
                if (m.type === 'stream-event' && m.event.kind === 'text') received.push(m.event.delta);
                if (m.type === 'stream-event' && m.streamId === 1) {
                    if (m.event.kind === 'text') acc += m.event.delta;
                    if (m.event.kind === 'done') { clearTimeout(t); resolve(acc); }
                }
            });
            room.send({ v: 1, type: 'stream', id: 1, messages: [{ role: 'user', content: 'marker-' + who }] });
        });
        return { approved, text, received };
    }, { code: CODE, who, timeoutMs: TIMEOUT });

    // Give the ghost a beat to start listening before peers join.
    await new Promise(r => setTimeout(r, 1500));
    const [ghostRes, aRes, bRes] = await Promise.all([ghostRun, peerRun(pgA, 'A'), peerRun(pgB, 'B')]);

    console.log('ghost:', JSON.stringify(ghostRes));
    console.log('A:', JSON.stringify(aRes));
    console.log('B:', JSON.stringify(bRes));

    const aGotOwn = aRes.text === 'reply-to:marker-A';
    const bGotOwn = bRes.text === 'reply-to:marker-B';
    const aClean = !aRes.received.some(d => d.includes('marker-B'));
    const bClean = !bRes.received.some(d => d.includes('marker-A'));

    if (ghostRes.conns < 2) verdict = `ghost saw ${ghostRes.conns} connections, expected 2`;
    else if (!aGotOwn || !bGotOwn) verdict = `wrong reply (A="${aRes.text}", B="${bRes.text}")`;
    else if (!aClean || !bClean) verdict = `CROSS-LEAK: a peer received the other's tokens (aClean=${aClean}, bClean=${bClean})`;
} catch (e) {
    verdict = e.message;
} finally {
    if (verdict) {
        console.error('\nlogs:\n' + logs.join('\n'));
        console.error(`\n── VERDICT ──\nFAIL: ${verdict}\n`);
    } else {
        console.log('\n── VERDICT ──\nPASS: two peers paired on one GhostBot, each got only its own stream (no cross-leak)\n');
    }
    await browser.close();
    process.exit(verdict ? 1 : 0);
}
