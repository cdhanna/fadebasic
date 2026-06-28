// Public entry point for the sharing module. Centralizes the surface so
// callers (main.ts wiring + the collaboration panel + tests) import from
// one place.

export { sha256, sha256Hex, gitBlobSha, toHex, shardOf } from './hash';
export {
    diffGitTrees,
    flattenTreeToBlobShas,
    type GitTree,
    type GitTreeEntry,
    type GitCommitMeta,
    type TreeDiff,
} from './git-types';
export {
    HeadConflictError,
    type GitAdapter,
} from './adapter';
export { MockAdapter } from './mock-adapter';
export { MemoryWorkingTree, type WorkingTree } from './working-tree';
export {
    Repo,
    type CommitOptions,
    type FastForwardResult,
    type RepoOptions,
    type SyncedHead,
} from './repo';
export {
    GitHubAdapter,
    GitHubApiError,
    type CreateRepoOptions,
    type GitHubAdapterOptions,
} from './github-adapter';
export {
    DeviceFlowError,
    pollForToken,
    requestDeviceCode,
    signInWithDeviceFlow,
    validateToken,
    type DeviceCodePrompt,
    type PollForTokenOptions,
    type RequestDeviceCodeOptions,
    type SignInWithDeviceFlowOptions,
    type ValidatedToken,
} from './github-auth';
export {
    SessionTokenStore,
    MemoryTokenStore,
    isAccessExpired,
    isRefreshUsable,
    tokenSetToStored,
    type TokenStore,
    type StoredTokenSet,
} from './token-store';
export {
    openSignInDialog,
    type SignInDialogOptions,
} from './auth-ui';
export { OpfsWorkingTree, isHiddenFromCommits, type OpfsWorkspaceLike } from './opfs-working-tree';
export { lineDiff, type LineDiffMark, type LineDiffResult } from './line-diff';
export { attachGutter, type AttachGutterOptions, type GutterHandle } from './monaco-gutter';
export {
    diff3Merge,
    hasConflictMarkers,
    parseConflictRegions,
    type Diff3Result,
    type Diff3Options,
    type ConflictRegion,
    type ParsedConflictRegion,
} from './diff3';
export {
    mountConflictEditor,
    type ConflictEditorOptions,
    type ConflictEditorHandle,
} from './conflict-editor';
export {
    loadSyncIndex,
    saveSyncIndex,
    clearSyncIndex,
    isConnected,
    type ProjectSyncIndex,
} from './sync-index';
export {
    computeStatus,
    pathStatusHint,
    statusGlyph,
    type FileStatus,
    type FileStatusEntry,
} from './file-status';
export {
    mountCollaboration,
    type SharingCommitInfo,
    type CollaborationController,
    type CollaborationOptions,
} from './collaboration-panel';
export {
    createDiffViewer,
    type DiffViewerParams,
    type DiffViewerComponent,
} from './diff-viewer';
export {
    mountHistoryPanel,
    type HistoryPanelOptions,
    type HistoryPanelHandle,
} from './history-panel';
export {
    loadSaves,
    createSave,
    revertToSave,
    clearSaves,
    dropSave,
    upgradeSave,
    MemorySaveStorage,
    type LocalSave,
    type SaveStorage,
    type SaveWorkspaceLike,
} from './local-saves';
export { HashCache } from './file-status';
