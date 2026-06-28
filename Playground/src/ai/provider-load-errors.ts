/** User-facing messages when a ChatProvider fails to load. */

export function formatProviderLoadError(err: unknown, providerId: string): string {
    const raw = err instanceof Error ? err.message : String(err);
    const name = err instanceof Error ? err.name : '';

    if (
        name === 'ReferenceError'
        || err instanceof ReferenceError
        || /\bis not defined\b/i.test(raw)
    ) {
        return providerId === 'ghostbot:local'
            ? 'GhostBot UI failed to initialize. Reload the Playground page, then click Load Model again.'
            : 'Model UI failed to initialize. Reload the Playground page and try again.';
    }

    if (providerId === 'ghostbot:local') {
        if (raw.includes('did not connect within')) {
            return raw;
        }
        if (/Connection failed|ERR_CONNECTION_FAILURE|Ice connection failed/i.test(raw)) {
            return 'WebRTC connection to GhostBot failed. Ensure GhostBot is running, the join codes match, '
                + 'and both sides can reach the Trystero trackers. See /webrtc-probe.html to diagnose ICE.';
        }
        if (raw.includes('No GhostBot session')) {
            return 'GhostBot session not started. Click Load Model, then enter the join code in the GhostBot desktop app.';
        }
        if (/signaling|webrtc|room|torrent/i.test(raw)) {
            return `GhostBot could not open a signaling session: ${raw}`;
        }
        if (raw.startsWith('GhostBot')) return raw;
        return `GhostBot setup failed: ${raw}`;
    }

    if (providerId.startsWith('onnx:')) {
        if (/webgpu|gpu|ort/i.test(raw)) {
            return `ONNX model load failed (GPU): ${raw}`;
        }
        return `ONNX model load failed: ${raw}`;
    }

    return raw;
}

/** Short label for status chips — full detail goes in title/tooltip. */
export function providerErrorSummary(detail: string, providerId: string): string {
    if (providerId === 'ghostbot:local') {
        if (detail.includes('did not connect within')) return 'GhostBot not connected';
        if (detail.includes('Reload the Playground')) return 'GhostBot UI error';
        if (detail.startsWith('GhostBot')) {
            const first = detail.split(/[.!?\n]/)[0]?.trim();
            return first && first.length < 80 ? first : 'GhostBot setup failed';
        }
    }
    const first = detail.split(/[.!\n]/)[0]?.trim();
    if (first && first.length <= 72) return first;
    return 'Load failed';
}
