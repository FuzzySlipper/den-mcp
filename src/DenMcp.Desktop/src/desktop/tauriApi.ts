const DEFAULT_INVOKE_TIMEOUT_MS = 12_000;
const LISTEN_TIMEOUT_MS = 5_000;

function toErrorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

async function withTimeout<T>(promise: Promise<T>, label: string, timeoutMs: number): Promise<T> {
  let timeoutId: number | undefined;
  const timeout = new Promise<never>((_, reject) => {
    timeoutId = window.setTimeout(() => {
      reject(new Error(`${label} timed out after ${Math.round(timeoutMs / 1000)}s`));
    }, timeoutMs);
  });

  try {
    return await Promise.race([promise, timeout]);
  } catch (err) {
    throw new Error(`${label} failed: ${toErrorMessage(err)}`);
  } finally {
    if (timeoutId !== undefined) {
      window.clearTimeout(timeoutId);
    }
  }
}

interface DenDesktopSidecarRuntimeApi {
  getOperatorStatus(): Promise<OperatorStatus>;
  getSettings(): Promise<OperatorSettings>;
  saveOperatorSettings(request: SaveOperatorSettingsRequest): Promise<OperatorSettings>;
  refreshNow(): Promise<void>;
  listLocalSnapshots(): Promise<LocalSnapshotList>;
  listLocalSessionSnapshots(): Promise<LocalSessionSnapshotList>;
  getLatestDiffSnapshot(request: LatestDiffSnapshotRequest): Promise<DesktopDiffSnapshotLatestResult>;
  onOperatorStatus(listener: (status: OperatorStatus) => void): () => void;
  onGitSnapshots(listener: (snapshots: LocalGitSnapshot[]) => void): () => void;
  onSessionSnapshots(listener: (snapshots: LocalSessionSnapshot[]) => void): () => void;
}

declare global {
  interface Window {
    denDesktopSidecar?: DenDesktopSidecarRuntimeApi;
  }
}

function sidecarApi(): DenDesktopSidecarRuntimeApi {
  const api = window.denDesktopSidecar;
  if (!api) {
    throw new Error('Den Desktop sidecar preload API is unavailable.');
  }

  return api;
}

function callSidecar<T>(label: string, operation: () => Promise<T>, timeoutMs = DEFAULT_INVOKE_TIMEOUT_MS): Promise<T> {
  return withTimeout(operation(), `desktop bridge ${label}`, timeoutMs);
}

function listenSidecar(label: string, subscribe: () => () => void): Promise<() => void> {
  return withTimeout(Promise.resolve().then(subscribe), `desktop bridge listener ${label}`, LISTEN_TIMEOUT_MS);
}

export interface OperatorSettings {
  denBaseUrl: string;
  sourceInstanceId: string;
  sourceDisplayName: string | null;
  pollIntervalSeconds: number;
  maxChangedFiles: number;
}

export interface SaveOperatorSettingsRequest {
  denBaseUrl: string;
  sourceDisplayName: string | null;
  pollIntervalSeconds?: number;
  maxChangedFiles?: number;
}

export interface DiagnosticEntry {
  level: string;
  source: string;
  message: string;
  observedAt: string;
}

export interface DenConnectionStatus {
  state: 'unknown' | 'connected' | 'degraded' | 'offline' | 'misconfigured' | string;
  message: string | null;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  nextRetryAt: string | null;
}

export interface ObserverStatus {
  kind: string;
  state: string;
  scopesScanned: number;
  warningCount: number;
  lastRunAt: string | null;
  nextRunAt: string | null;
}

export interface OperatorStatus {
  phase: string;
  denConnection: DenConnectionStatus;
  sourceInstanceId: string;
  denBaseUrl: string;
  lastSyncAt: string | null;
  lastPublishAt: string | null;
  observerStatuses: ObserverStatus[];
  diagnostics: DiagnosticEntry[];
  projectCount: number;
  workspaceCount: number;
  localSnapshotCount: number;
  localSessionSnapshotCount: number;
}

export type DesktopSnapshotState =
  | 'ok'
  | 'path_not_visible'
  | 'not_git_repository'
  | 'git_error'
  | 'source_offline'
  | 'missing';

export interface GitDirtyCounts {
  total: number;
  staged: number;
  unstaged: number;
  untracked: number;
  modified: number;
  added: number;
  deleted: number;
  renamed: number;
}

export interface GitFileStatus {
  path: string;
  old_path: string | null;
  index_status: string | null;
  worktree_status: string | null;
  category: string;
  is_untracked: boolean;
}

export interface GitScope {
  projectId: string;
  projectName: string | null;
  taskId: number | null;
  workspaceId: string | null;
  rootPath: string;
  sourceKind: string;
}

export interface DesktopGitSnapshotRequest {
  task_id: number | null;
  workspace_id: string | null;
  root_path: string;
  state: DesktopSnapshotState;
  branch: string | null;
  is_detached: boolean;
  head_sha: string | null;
  upstream: string | null;
  ahead: number | null;
  behind: number | null;
  dirty_counts: GitDirtyCounts;
  changed_files: GitFileStatus[];
  warnings: string[];
  truncated: boolean;
  source_instance_id: string;
  source_display_name: string | null;
  observed_at: string;
}

export interface LocalGitSnapshot {
  scope: GitScope;
  request: DesktopGitSnapshotRequest;
  lastPublishStatus: 'pending' | 'published' | 'failed' | 'queued' | string;
  lastPublishError: string | null;
  lastPublishedAt: string | null;
}

export interface LocalSnapshotList {
  scopes: GitScope[];
  snapshots: LocalGitSnapshot[];
}

export interface DesktopSessionSnapshotRequest {
  task_id: number | null;
  workspace_id: string | null;
  session_id: string;
  parent_session_id: string | null;
  agent_identity: string | null;
  role: string | null;
  current_command: string | null;
  current_phase: string | null;
  recent_activity: unknown;
  child_sessions: unknown;
  control_capabilities: unknown;
  warnings: string[];
  source_instance_id: string;
  observed_at: string;
}

export interface LocalSessionSnapshot {
  projectId: string;
  request: DesktopSessionSnapshotRequest;
  lastPublishStatus: 'pending' | 'published' | 'failed' | 'queued' | string;
  lastPublishError: string | null;
  lastPublishedAt: string | null;
  artifactRoot: string | null;
}

export interface LocalSessionSnapshotList {
  snapshots: LocalSessionSnapshot[];
}

export interface LatestDiffSnapshotRequest {
  projectId: string;
  taskId: number | null;
  workspaceId: string | null;
  rootPath: string;
  path: string | null;
  sourceInstanceId: string;
}

export interface DesktopDiffSnapshotLatestResult {
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string | null;
  path: string | null;
  source_instance_id: string | null;
  state: DesktopSnapshotState;
  is_stale: boolean;
  freshness_status: string;
  snapshot: DesktopDiffSnapshot | null;
}

export interface DesktopDiffSnapshot {
  id: number;
  project_id: string;
  task_id: number | null;
  workspace_id: string | null;
  root_path: string;
  path: string | null;
  base_ref: string | null;
  head_ref: string | null;
  max_bytes: number;
  staged: boolean;
  diff: string;
  truncated: boolean;
  binary: boolean;
  warnings: string[];
  source_instance_id: string;
  source_display_name: string | null;
  observed_at: string;
  received_at: string;
  updated_at: string;
  is_stale: boolean;
  freshness_seconds: number;
}

export async function getOperatorStatus(): Promise<OperatorStatus> {
  return callSidecar('getOperatorStatus', () => sidecarApi().getOperatorStatus());
}

export async function getSettings(): Promise<OperatorSettings> {
  return callSidecar('getSettings', () => sidecarApi().getSettings());
}

export async function saveOperatorSettings(request: SaveOperatorSettingsRequest): Promise<OperatorSettings> {
  return callSidecar('saveOperatorSettings', () => sidecarApi().saveOperatorSettings(request));
}

export async function refreshNow(): Promise<void> {
  return callSidecar('refreshNow', () => sidecarApi().refreshNow());
}

export async function listLocalSnapshots(): Promise<LocalSnapshotList> {
  return callSidecar('listLocalSnapshots', () => sidecarApi().listLocalSnapshots());
}

export async function listLocalSessionSnapshots(): Promise<LocalSessionSnapshotList> {
  return callSidecar('listLocalSessionSnapshots', () => sidecarApi().listLocalSessionSnapshots());
}

export async function getLatestDiffSnapshot(request: LatestDiffSnapshotRequest): Promise<DesktopDiffSnapshotLatestResult> {
  return callSidecar('getLatestDiffSnapshot', () => sidecarApi().getLatestDiffSnapshot(request));
}

export function onOperatorStatus(callback: (status: OperatorStatus) => void): Promise<() => void> {
  return listenSidecar('operator status', () => sidecarApi().onOperatorStatus(callback));
}

export function onGitSnapshots(callback: (snapshots: LocalGitSnapshot[]) => void): Promise<() => void> {
  return listenSidecar('git snapshots', () => sidecarApi().onGitSnapshots(callback));
}

export function onSessionSnapshots(callback: (snapshots: LocalSessionSnapshot[]) => void): Promise<() => void> {
  return listenSidecar('session snapshots', () => sidecarApi().onSessionSnapshots(callback));
}
