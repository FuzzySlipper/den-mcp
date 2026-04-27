import { invoke } from '@tauri-apps/api/core';
import { listen } from '@tauri-apps/api/event';

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

export async function getOperatorStatus(): Promise<OperatorStatus> {
  return invoke('get_operator_status');
}

export async function getSettings(): Promise<OperatorSettings> {
  return invoke('get_settings');
}

export async function saveOperatorSettings(request: SaveOperatorSettingsRequest): Promise<OperatorSettings> {
  return invoke('save_operator_settings', { request });
}

export async function refreshNow(): Promise<void> {
  return invoke('refresh_now');
}

export async function listLocalSnapshots(): Promise<LocalSnapshotList> {
  return invoke('list_local_snapshots');
}

export function onOperatorStatus(callback: (status: OperatorStatus) => void): Promise<() => void> {
  return listen<OperatorStatus>('den://operator-status', (event) => callback(event.payload));
}

export function onGitSnapshots(callback: (snapshots: LocalGitSnapshot[]) => void): Promise<() => void> {
  return listen<LocalGitSnapshot[]>('den://git-snapshot-updated', (event) => callback(event.payload));
}
