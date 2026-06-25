/**
 * Playwright probe: two browser tabs pair via Trystero/WebRTC (same path as live collab).
 * Requires Playground dev server: npm run dev
 *
 *   npm run probe:ghostbot
 */
import { chromium } from 'playwright';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
process.env.PLAYWRIGHT_BROWSERS_PATH ??= resolve(__dirname, '..', 'node_modules', 'playwright', '.local-browsers');

const JOIN_CODE = 'TRYST01';
const APP_ID = 'fade-ghostbot';
const ACTION = 'ghost';

async function trysteroPeerScript({ role, code, appId, action }) {
    const { joinRoom } = await import('https://esm.sh/trystero@0.20.0/torrent');
    const ICE = {
        iceServers: [
            { urls: 'stun:stun.l.google.com:19302' },
            { urls: 'stun:stun1.l.google.com:19302' },
        ],
    };

    return new Promise((resolve, reject) => {
        const room = joinRoom({ appId, rtcConfig: ICE }, code);
        const [send, receive] = room.makeAction(action);
        const out = { role, pong: false, session: false };

        const timer = setTimeout(() => {
            room.leave();
            reject(new Error(`${role}: Trystero timeout`));
        }, role === 'playground' ? 90_000 : 120_000);

        const finish = (result) => {
            clearTimeout(timer);
            room.leave();
            resolve(result);
        };

        receive((data, peerId) => {
            let bytes;
            if (data instanceof Uint8Array) bytes = data;
            else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
            else return;
            let msg;
            try { msg = JSON.parse(new TextDecoder().decode(bytes)); } catch { return; }

            if (msg.type === 'ping' && role === 'ghost') {
                send(new TextEncoder().encode(JSON.stringify({ v: 1, type: 'pong' })), null);
                send(new TextEncoder().encode(JSON.stringify({
                    v: 1, type: 'session', joinCode: code, status: 'connected', peerId,
                })), null);
                finish({ role, replied: true });
            }
            if (msg.type === 'pong') out.pong = true;
            if (msg.type === 'session' && msg.status === 'connected') out.session = true;
            if (out.pong && out.session) finish(out);
        });

        room.onPeerJoin(() => {
            if (role === 'playground') {
                setTimeout(() => {
                    send(new TextEncoder().encode(JSON.stringify({ v: 1, type: 'ping' })), null);
                }, 300);
            }
        });
    });
}

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const ghostPage = await ctx.newPage();
const pgPage = await ctx.newPage();

// Blank pages — trystero loaded from esm.sh CDN inside evaluate.
await ghostPage.goto('about:blank');
await pgPage.goto('about:blank');

try {
    const [ghostRes, pgRes] = await Promise.all([
        ghostPage.evaluate(trysteroPeerScript, { role: 'ghost', code: JOIN_CODE, appId: APP_ID, action: ACTION }),
        pgPage.evaluate(trysteroPeerScript, { role: 'playground', code: JOIN_CODE, appId: APP_ID, action: ACTION }),
    ]);
    console.log('ghost:', ghostRes);
    console.log('playground:', pgRes);
    if (!pgRes.pong || !pgRes.session) {
        console.error('\n── VERDICT ──\nFAIL: Trystero pairing incomplete\n');
        process.exit(1);
    }
    console.log('\n── VERDICT ──\nPASS: Trystero/WebRTC pairing works between two browser peers\n');
} catch (e) {
    console.error('\n── VERDICT ──\nFAIL:', e.message, '\n');
    process.exit(1);
} finally {
    await browser.close();
}
