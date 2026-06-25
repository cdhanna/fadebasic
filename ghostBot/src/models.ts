/** UI helpers for the model download catalog. */

export interface DownloadableModel {
    id: string;
    label: string;
    filename: string;
    size_label: string;
    description: string;
    recommended: boolean;
    downloaded: boolean;
    size_mb: number;
    incomplete: boolean;
}

export function pickDefaultDownloadModel(models: DownloadableModel[]): string | null {
    const ready = models.find(m => m.recommended && m.downloaded && !m.incomplete);
    if (ready) return ready.id;
    const rec = models.find(m => m.recommended);
    if (rec) return rec.id;
    return models[0]?.id ?? null;
}

export function hasUsableLocalModel(models: DownloadableModel[]): boolean {
    return models.some(m => m.downloaded && !m.incomplete);
}

export function formatModelStatus(m: DownloadableModel): string {
    if (!m.downloaded) return 'Not downloaded';
    if (m.incomplete) return `Incomplete (${m.size_mb} MB)`;
    return `Ready (${m.size_mb} MB)`;
}
