/**
 * Playwright probe: pair the REAL Playground ghostbot-transport with the REAL
 * ghostBot session module, across browser engines.
 *
 *   - playground peer: chromium, real app origin https://localhost:5311,
 *     imports /src/ai/providers/ghostbot-transport.ts
 *   - ghost peer: chromium or webkit (webkit ~ Tauri's WKWebView),
 *     origin http://localhost:1420 (ghostBot `vite` dev server),
 *     imports /src/session.ts
 *
 * Requires both dev servers:
 *   Playground/  npm run dev          (https://localhost:5311)
 *   ghostBot/    npx vite --port 1420
 *
 * Usage:
 *   node scripts/probe-ghostbot-cross.mjs            # chromium ghost
 *   node scripts/probe-ghostbot-cross.mjs webkit     # webkit ghost (WKWebView-ish)
 */
import { chromium, webkit } from 'playwright';

const ghostEngineName = process.argv[2] === 'webkit' ? 'webkit' : 'chromium';
const ghostEngine = ghostEngineName === 'webkit' ? webkit : chromium;

const JOIN_CODE = 'PRB' + Math.random().toString(36).slice(2, 6).toUpperCase();
const PAIR_TIMEOUT_MS = 75_000;

function captureLogs(page, tag, sink) {
    page.on('pageerror', e => sink.push(`[${tag}][PE] ${e.message}`));
    page.on('console', m => sink.push(`[${tag}][${m.type()}] ${m.text()}`));
}

// Patch WebSocket before any module loads so we see tracker socket lifecycle.
const wsSpyInit = () => {
    window.__wsLog = [];
    const Orig = window.WebSocket;
    window.WebSocket = class extends Orig {
        constructor(url, protocols) {
            super(url, protocols);
            const log = (ev) => window.__wsLog.push(`${ev} ${url}`);
            log('NEW');
            this.addEventListener('open', () => log('OPEN'));
            this.addEventListener('error', () => log('ERROR'));
            this.addEventListener('close', (e) => window.__wsLog.push(`CLOSE(${e.code}) ${url}`));
        }
    };

    // ICE spy: log local/remote candidates and state transitions per connection.
    window.__iceLog = [];
    let pcSeq = 0;
    const summarizeCand = (c) => {
        if (!c || !c.candidate) return 'end-of-candidates';
        const m = c.candidate.match(/candidate:\S+ \d+ (\S+) \d+ (\S+) (\d+) typ (\S+)/);
        return m ? `${m[4]}/${m[1]} ${m[2]}:${m[3]}` : c.candidate;
    };
    const OrigPC = window.RTCPeerConnection;
    window.RTCPeerConnection = class extends OrigPC {
        constructor(...args) {
            super(...args);
            const id = `pc${pcSeq++}`;
            const log = (msg) => window.__iceLog.push(`[${id}] ${msg}`);
            log(`new ${JSON.stringify(args[0]?.iceServers?.map(s => s.urls) ?? null)}`);
            this.addEventListener('icecandidate', (e) => log(`local ${summarizeCand(e.candidate)}`));
            this.addEventListener('iceconnectionstatechange', () => log(`iceState ${this.iceConnectionState}`));
            this.addEventListener('icegatheringstatechange', () => log(`gathering ${this.iceGatheringState}`));
            this.addEventListener('connectionstatechange', () => log(`connState ${this.connectionState}`));
            const origAdd = this.addIceCandidate.bind(this);
            this.addIceCandidate = (cand, ...rest) => {
                log(`remote ${summarizeCand(cand)}`);
                return origAdd(cand, ...rest);
            };
        }
    };
};

const logs = [];
const pgBrowser = await chromium.launch({ headless: true });
const ghostBrowser = ghostEngineName === 'chromium'
    ? pgBrowser
    : await ghostEngine.launch({ headless: true });

const pgCtx = await pgBrowser.newContext({ ignoreHTTPSErrors: true });
const ghostCtx = await ghostBrowser.newContext({ ignoreHTTPSErrors: true });
const pgPage = await pgCtx.newPage();
const ghostPage = await ghostCtx.newPage();
captureLogs(pgPage, 'pg', logs);
captureLogs(ghostPage, 'ghost', logs);
await pgPage.addInitScript(wsSpyInit);
await ghostPage.addInitScript(wsSpyInit);

let verdict = null;
try {
    // Load blank pages on the right origins so Vite-served module imports work.
    await pgPage.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });
    await ghostPage.goto('http://localhost:1420/', { waitUntil: 'domcontentloaded' });

    // Ghost hub auto-approves every peer (probe mode) and answers streams.
    const ghostRun = ghostPage.evaluate(async ({ code, timeoutMs }) => {
        const { createGhostHub } = await import('/src/session.ts');
        const hub = createGhostHub(code);
        hub.setModelStatus({ loaded: true, name: 'probe-model' });
        hub.setStreamHandler(async function* () {
            yield { kind: 'text', delta: 'hello from ghost probe' };
            yield { kind: 'done', finishReason: 'stop' };
        });
        hub.onChange(() => {
            for (const c of hub.listConnections()) if (!c.approved) hub.approve(c.peerId);
        });
        hub.start();
        const sawPeer = await new Promise(resolve => {
            const t = setTimeout(() => resolve(false), timeoutMs);
            hub.onChange(() => { if (hub.listConnections().some(c => c.approved)) { clearTimeout(t); resolve(true); } });
        });
        return { connected: sawPeer, conns: hub.listConnections().length, wsLog: window.__wsLog, iceLog: window.__iceLog };
    }, { code: JOIN_CODE, timeoutMs: PAIR_TIMEOUT_MS });

    const pgRun = pgPage.evaluate(async ({ code, timeoutMs }) => {
        const { joinGhostRoom } = await import('/src/ai/providers/ghostbot-transport.ts');
        const room = await joinGhostRoom(code, { clientId: 'probe-client', label: 'Probe' });
        const events = [];
        room.onMessage(m => events.push(m.type + (m.status ? ':' + m.status : '')));

        // Ready = GhostBot approved us (auth approved or a post-approval pong).
        const approved = await new Promise(resolve => {
            const t = setTimeout(() => resolve(false), timeoutMs);
            room.onMessage(m => {
                if (m.type === 'pong' || (m.type === 'auth' && m.status === 'approved')) {
                    clearTimeout(t); resolve(true);
                }
            });
        });

        let streamedText = null;
        if (approved) {
            streamedText = await new Promise(resolve => {
                const t = setTimeout(() => resolve('TIMEOUT'), 15_000);
                let acc = '';
                room.onMessage(m => {
                    if (m.type === 'stream-event' && m.streamId === 99) {
                        if (m.event.kind === 'text') acc += m.event.delta;
                        if (m.event.kind === 'done') { clearTimeout(t); resolve(acc); }
                    }
                });
                room.send({ v: 1, type: 'stream', id: 99, messages: [{ role: 'user', content: 'hi' }] });
            });
        }
        return { connected: approved, streamedText, events, wsLog: window.__wsLog, iceLog: window.__iceLog };
    }, { code: JOIN_CODE, timeoutMs: PAIR_TIMEOUT_MS });

    const [ghostRes, pgRes] = await Promise.all([ghostRun, pgRun]);

    console.log(`\nghost engine: ${ghostEngineName}, join code: ${JOIN_CODE}`);
    console.log('ghost:', JSON.stringify(ghostRes, null, 2));
    console.log('playground:', JSON.stringify(pgRes, null, 2));

    const ok = ghostRes.connected && pgRes.connected
        && pgRes.streamedText === 'hello from ghost probe';
    verdict = ok ? null
        : `pairing incomplete (ghost.connected=${ghostRes.connected}, pg.connected=${pgRes.connected}, streamed=${JSON.stringify(pgRes.streamedText)})`;
} catch (e) {
    verdict = e.message;
} finally {
    if (verdict) {
        console.error('\ncaptured logs:\n' + logs.join('\n'));
        console.error(`\n── VERDICT ──\nFAIL (${ghostEngineName} ghost): ${verdict}\n`);
    } else {
        console.log(`\n── VERDICT ──\nPASS: real-module pairing + stream round-trip works (${ghostEngineName} ghost ↔ chromium playground)\n`);
    }
    await pgBrowser.close();
    if (ghostBrowser !== pgBrowser) await ghostBrowser.close();
    process.exit(verdict ? 1 : 0);
}
