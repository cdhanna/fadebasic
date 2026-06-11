export interface EditReviewRequest {
    path: string;
    oldContent: string;
    newContent: string;
    validateContent?: (path: string, content: string) => Promise<import('./tools').DiagnosticEntry[]>;
}

export interface EditReviewResult {
    approved: boolean;
    feedback: string;
}
