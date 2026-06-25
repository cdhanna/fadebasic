/** WebRTC visibility for the log pane.
 *
 *  Trystero opens a pool of RTCPeerConnections per announce (one per
 *  outstanding offer), so a few failed attempts are NORMAL — only a
 *  never-ending stream of failures with no success means the connection is
 *  actually stuck. This monitor wraps RTCPeerConnection to narrate attempts:
 *  failures get one line each, and the first success reports which candidate
 *  pair type won (host/srflx/relay) — the single most useful fact when
 *  debugging "why won't it pair on THIS network".
 *
 *  It also converts the uncaught "Ice connection failed." errors that
 *  simple-peer throws (no error listener on pool peers) into friendly log
 *  lines instead of silent window errors.
 */

type LogFn = (line: string) => void;

let installed = false;

async function selectedPairSummary(pc: RTCPeerConnection): Promise<string | null> {
    try {
        const stats = await pc.getStats();
        const byId = new Map<string, Record<string, unknown>>();
        stats.forEach((r) => byId.set(r.id, r as unknown as Record<string, unknown>));
        let summary: string | null = null;
        stats.forEach((r) => {
            const rec = r as unknown as Record<string, unknown>;
            if (rec.type === 'candidate-pair'
                && (rec.selected === true || rec.state === 'succeeded')
                && rec.nominated === true) {
                const local = byId.get(rec.localCandidateId as string);
                const remote = byId.get(rec.remoteCandidateId as string);
                if (local && remote) {
                    summary = `${local.candidateType}↔${remote.candidateType}`;
                }
            }
        });
        return summary;
    } catch {
        return null;
    }
}

export function installIceMonitor(log: LogFn): void {
    if (installed || typeof window.RTCPeerConnection === 'undefined') return;
    installed = true;

    let attempts = 0;
    let failures = 0;
    let successes = 0;

    const Orig = window.RTCPeerConnection;
    window.RTCPeerConnection = class extends Orig {
        constructor(...args: ConstructorParameters<typeof RTCPeerConnection>) {
            super(...args);
            const id = ++attempts;
            let reported = false;
            this.addEventListener('iceconnectionstatechange', () => {
                const s = this.iceConnectionState;
                if (s === 'failed' && !reported) {
                    reported = true;
                    failures++;
                    log(`webrtc attempt #${id}: ICE failed (${failures} failed / ${successes} ok so far)`);
                    if (failures >= 5 && successes === 0) {
                        log('webrtc: no attempt has connected yet — this network may '
                            + 'block direct peer connections. On macOS check System Settings '
                            + '→ Privacy & Security → Local Network (allow your terminal in '
                            + 'dev, GhostBot when packaged). Strict NAT needs a TURN server: '
                            + 'localStorage key "ghostbot.customIceServers".');
                    }
                } else if ((s === 'connected' || s === 'completed') && !reported) {
                    reported = true;
                    successes++;
                    void selectedPairSummary(this).then((pair) => {
                        log(`webrtc attempt #${id}: connected${pair ? ` via ${pair}` : ''}`);
                    });
                }
            });
        }
    } as typeof RTCPeerConnection;

    // simple-peer's pool peers have no 'error' listener, so ICE failures
    // surface as uncaught window errors — translate instead of scaring.
    window.addEventListener('error', (e) => {
        if (/ice connection failed/i.test(e.message ?? '')) {
            e.preventDefault();   // handled: the state-change listener above logs it
        }
    });
    window.addEventListener('unhandledrejection', (e) => {
        const msg = (e.reason as Error | undefined)?.message ?? String(e.reason ?? '');
        if (/ice connection failed/i.test(msg)) {
            e.preventDefault();
        } else if (msg) {
            log(`error: ${msg}`);
        }
    });
}
