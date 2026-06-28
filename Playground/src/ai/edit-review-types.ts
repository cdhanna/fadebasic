export interface EditReviewRequest {
    path: string;
    oldContent: string;
    newContent: string;
    validateContent?: (path: string, content: string) => Promise<import('./tools').DiagnosticEntry[]>;
    /** Authoritative command list, used for a deterministic Fade syntax/command
     *  pre-check before the LSP runs (catches missing parens, invented
     *  commands, and cross-language mistakes with crisp, actionable feedback). */
    commandNames?: string[];
}

export interface EditReviewResult {
    approved: boolean;
    feedback: string;
}
