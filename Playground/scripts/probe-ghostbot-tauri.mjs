/**
 * Playground side of the REAL-Tauri pairing test.
 *
 * Run the ghost side first:
 *   ghostBot/  tauri.conf.json devUrl set to http://localhost:1420/?probe=<CODE>
 *   ghostBot/  npm start     (tauri dev — real WKWebView)
 *
 * Then:
 *   Playground/  node scripts/probe-ghostbot-tauri.mjs <CODE>
 *
 * Joins the trystero room with the real Playground transport module and
 * reports whether the WKWebView ghost peer connects + streams.
 */
import { chromium } from 'playwright';

const JOIN_CODE = process.argv[2] || 'GHTAURI1';
const PAIR_TIMEOUT_MS = 90_000;

const logs = [];
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
page.on('pageerror', e => logs.push(`[PE] ${e.message}`));
page.on('console', m => logs.push(`[${m.type()}] ${m.text()}`));

let verdict = null;
try {
    await page.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });
    const res = await page.evaluate(async ({ code, timeoutMs }) => {
        const { joinGhostRoom } = await import('/src/ai/providers/ghostbot-transport.ts');
        const room = await joinGhostRoom(code);
        const events = [];
        room.onMessage(m => events.push(`${m.type}${m.status ? ':' + m.status : ''}`));
        room.onStatus(s => events.push(`status:${s}`));

        const connected = await new Promise(resolve => {
            const t = setTimeout(() => resolve(false), timeoutMs);
            room.onStatus(s => { if (s === 'connected') { clearTimeout(t); resolve(true); } });
            if (room.status === 'connected') { clearTimeout(t); resolve(true); }
        });

        let streamedText = null;
        if (connected) {
            streamedText = await new Promise(resolve => {
                const t = setTimeout(() => resolve('TIMEOUT'), 20_000);
                let acc = '';
                room.onMessage(m => {
                    if (m.type === 'stream-event' && m.streamId === 7) {
                        if (m.event.kind === 'text') acc += m.event.delta;
                        if (m.event.kind === 'done') { clearTimeout(t); resolve(acc); }
                    }
                });
                room.send({ v: 1, type: 'stream', id: 7, messages: [{ role: 'user', content: 'hi' }] });
            });
        }
        return { connected, streamedText, events };
    }, { code: JOIN_CODE, timeoutMs: PAIR_TIMEOUT_MS });

    console.log(`join code: ${JOIN_CODE}`);
    console.log(JSON.stringify(res, null, 2));
    const ok = res.connected && res.streamedText === 'hello from tauri ghost';
    verdict = ok ? null : `connected=${res.connected} streamed=${JSON.stringify(res.streamedText)}`;
} catch (e) {
    verdict = e.message;
} finally {
    if (verdict) {
        console.error('\nlogs:\n' + logs.join('\n'));
        console.error(`\n── VERDICT ──\nFAIL: ${verdict}\n`);
    } else {
        console.log('\n── VERDICT ──\nPASS: real WKWebView ghost ↔ chromium playground paired + streamed\n');
    }
    await browser.close();
    process.exit(verdict ? 1 : 0);
}
