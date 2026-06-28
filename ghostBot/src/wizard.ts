/** Pure wizard / UI helpers — safe to unit test without DOM or Tauri. */

export function formatEta(sec: number): string {
    if (sec <= 0) return '—';
    if (sec < 60) return `~${sec}s left`;
    const m = Math.floor(sec / 60);
    const s = sec % 60;
    return `~${m}m ${s}s left`;
}

export function isWizardStepUnlocked(
    step: number,
    state: { modelDownloaded: boolean; modelLoaded: boolean },
): boolean {
    if (step === 1) return true;
    if (step === 2) return state.modelDownloaded;
    if (step === 3) return state.modelLoaded;
    return false;
}

export function normalizeJoinCode(code: string): string {
    return code.trim().toUpperCase();
}

export function sessionPillClass(status: string): string {
    switch (status) {
        case 'connected': return 'connected';
        case 'inferring': return 'inferring';
        case 'waiting': return 'waiting';
        case 'error': return 'error';
        default: return 'idle';
    }
}
