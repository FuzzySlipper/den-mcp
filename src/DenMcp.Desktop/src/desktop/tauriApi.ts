import type { TasksDashboardSnapshot, TasksDashboardSnapshotRequest } from '../electron/sidecarProtocol.ts';

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

export interface ShellAppearanceSettings {
  theme: string;
  accent: string;
  density: string;
  bodyFont: string;
  railMode: string;
  consoleMode: string;
  activeTab: string;
}

interface DenDesktopSidecarRuntimeApi {
  getOperatorStatus(): Promise<OperatorStatus>;
  getSettings(): Promise<OperatorSettings>;
  saveOperatorSettings(request: SaveOperatorSettingsRequest): Promise<OperatorSettings>;
  getAppearanceSettings<T = ShellAppearanceSettings>(): Promise<T>;
  saveAppearanceSettings<TRequest = Partial<ShellAppearanceSettings>, TResponse = ShellAppearanceSettings>(request: TRequest): Promise<TResponse>;
  refreshNow(): Promise<void>;
  listLocalSnapshots(): Promise<LocalSnapshotList>;
  listLocalSessionSnapshots(): Promise<LocalSessionSnapshotList>;
  getLatestDiffSnapshot(request: LatestDiffSnapshotRequest): Promise<DesktopDiffSnapshotLatestResult>;
  consoleListCommands(): Promise<ConsoleCommandListResponse>;
  consoleRunCommand(request: ConsoleCommandRunRequest): Promise<ConsoleCommandRunResponse>;
  terminalCreateSession(request: TerminalCreateSessionRequest): Promise<TerminalCreateSessionResponse>;
  terminalListSessions(request?: TerminalListSessionsRequest): Promise<TerminalListSessionsResponse>;
  terminalAttach(request: TerminalAttachRequest): Promise<TerminalAttachResponse>;
  terminalDetach(request: TerminalDetachRequest): Promise<TerminalDetachResponse>;
  terminalSendInput(request: TerminalSendInputRequest): Promise<TerminalSendInputResponse>;
  terminalResize(request: TerminalResizeRequest): Promise<TerminalResizeResponse>;
  terminalTerminate(request: TerminalTerminateRequest): Promise<TerminalTerminateResponse>;
  terminalReconnect(request: TerminalReconnectRequest): Promise<TerminalAttachResponse>;
  terminalAckOutput(request: TerminalAckOutputRequest): Promise<TerminalAckOutputResponse>;
  tasksGetDashboardSnapshot(request: TasksDashboardSnapshotRequest): Promise<TasksDashboardSnapshot>;
  onTerminalOutput(listener: (event: TerminalOutputEvent) => void): () => void;
  onTerminalStatus(listener: (event: TerminalStatusEvent) => void): () => void;
  onTerminalLifecycle(listener: (event: TerminalLifecycleEvent) => void): () => void;
  onTerminalBackpressure(listener: (event: TerminalBackpressureEvent) => void): () => void;
  onTerminalSessionList(listener: (event: TerminalListSessionsResponse) => void): () => void;
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
  title: string | null;
  display_name: string | null;
  cwd: string | null;
  kind: string | null;
  backend: string | null;
  status: string | null;
  started_at: string | null;
  last_activity_at: string | null;
  exited_at: string | null;
  exit_code: number | null;
  source_display_name: string | null;
  capabilities: unknown;
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

export async function getAppearanceSettings(): Promise<ShellAppearanceSettings> {
  return callSidecar('getAppearanceSettings', () => sidecarApi().getAppearanceSettings<ShellAppearanceSettings>());
}

export async function saveAppearanceSettings(request: Partial<ShellAppearanceSettings>): Promise<ShellAppearanceSettings> {
  return callSidecar('saveAppearanceSettings', () => sidecarApi().saveAppearanceSettings<Partial<ShellAppearanceSettings>, ShellAppearanceSettings>(request));
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

// ── Console command types (task #914) ──

export interface ConsoleCommandDefinition {
  name: string;
  displayName: string;
  description: string;
  needsTarget: boolean;
}

export interface ConsoleCommandLine {
  level: string;
  timestamp: string;
  source: string;
  message: string;
}

export interface ConsoleCommandRunRequest {
  command: string;
  projectId?: string | null;
  taskId?: number | null;
  workspaceId?: string | null;
  sessionId?: string | null;
}

export interface ConsoleCommandRunResponse {
  command: string;
  status: string;
  errorMessage?: string | null;
  lines: ConsoleCommandLine[];
}

export interface ConsoleCommandListResponse {
  commands: ConsoleCommandDefinition[];
}

export async function consoleListCommands(): Promise<ConsoleCommandListResponse> {
  return callSidecar('consoleListCommands', () => sidecarApi().consoleListCommands());
}

export async function consoleRunCommand(request: ConsoleCommandRunRequest): Promise<ConsoleCommandRunResponse> {
  return callSidecar('consoleRunCommand', () => sidecarApi().consoleRunCommand(request));
}

// ── Terminal stream/control protocol (#945/#911) ──

export interface TerminalSessionSummary {
  session_id: string;
  title?: string | null;
  display_name?: string | null;
  kind: string;
  backend: string;
  status: string;
  current_command?: string | null;
  agent_identity?: string | null;
  role?: string | null;
  project_id?: string | null;
  task_id?: number | null;
  workspace_id?: string | null;
  cwd?: string | null;
  source_instance_id?: string | null;
  source_display_name?: string | null;
  can_read_activity?: boolean;
  can_send_input?: boolean;
  can_resize?: boolean;
  can_terminate?: boolean;
  can_attach?: boolean;
  can_detach?: boolean;
  can_reconnect?: boolean;
  can_stream_terminal?: boolean;
  can_open_external_attach?: boolean;
  persistence_kind?: string | null;
  ownership_kind?: string | null;
  created_at?: string | null;
  last_observed_at?: string | null;
  last_activity_at?: string | null;
  exited_at?: string | null;
  exit_code?: number | null;
  warnings?: string[];
}

export interface TerminalCreateSessionRequest {
  project_id: string;
  task_id?: number | null;
  workspace_id?: string | null;
  title?: string | null;
  cwd?: string | null;
  backend?: string;
}

export interface TerminalCreateSessionResponse { session: TerminalSessionSummary; }
export interface TerminalListSessionsRequest { kind?: string | null; backend?: string | null; status?: string | null; }
export interface TerminalListSessionsResponse { sessions: TerminalSessionSummary[]; count: number; }
export interface TerminalViewport { cols: number; rows: number; }
export interface TerminalAttachRequest { session_id: string; mode?: string; viewport?: TerminalViewport | null; client_id?: string | null; replay?: { after_cursor?: string | null; max_bytes?: number; max_chunks?: number } | null; }
export interface TerminalAttachResponse { stream_id: string; session_id: string; attached_at?: string; start_cursor?: string; replay_available_from?: string; replay_gap?: boolean; capabilities?: Record<string, boolean>; limits?: { ack_after_bytes?: number; output_chunk_max_bytes?: number; input_chunk_max_bytes?: number; heartbeat_interval_ms?: number }; external_attach?: { available?: boolean; command?: string | null; description?: string | null } | null; }
export interface TerminalDetachRequest { stream_id: string; session_id: string; reason?: string | null; }
export interface TerminalDetachResponse { detached: boolean; backend_preserved: boolean; }
export interface TerminalSendInputRequest { session_id: string; stream_id?: string | null; input_id?: string | null; encoding?: string; data: string; byte_count?: number; }
export interface TerminalSendInputResponse { accepted: boolean; input_id?: string | null; written_bytes: number; }
export interface TerminalResizeRequest { session_id: string; stream_id?: string | null; cols: number; rows: number; }
export interface TerminalResizeResponse { accepted: boolean; cols: number; rows: number; }
export interface TerminalTerminateRequest { session_id: string; stream_id?: string | null; mode?: string; reason?: string | null; requested_by?: string | null; }
export interface TerminalTerminateResponse { accepted: boolean; mode: string; terminal_event_id?: string | null; }
export interface TerminalReconnectRequest { session_id: string; previous_stream_id?: string | null; last_seen_cursor?: string | null; viewport?: TerminalViewport | null; }
export interface TerminalAckOutputRequest { session_id: string; stream_id?: string | null; ack_cursor?: string | null; received_bytes?: number; }
export interface TerminalAckOutputResponse { accepted: boolean; }
export interface TerminalOutputEvent { stream_id: string; session_id: string; stream_cursor: string; terminal_sequence: number; encoding: string; data: string; byte_count: number; origin?: string | null; truncated?: boolean; }
export interface TerminalStatusEvent { session_id: string; status?: string | null; warnings?: string[]; observed_at?: string | null; capabilities?: Record<string, boolean> | null; }
export interface TerminalLifecycleEvent { event?: string; session_id: string; stream_id?: string | null; exit_code?: number | null; reason?: string; code?: string; message?: string; stream_cursor?: string | null; replay_gap?: boolean; }
export interface TerminalBackpressureEvent { session_id: string; stream_id?: string | null; state: string; queue_bytes: number; dropped_bytes: number; next_action?: string | null; }

export async function terminalCreateSession(request: TerminalCreateSessionRequest): Promise<TerminalCreateSessionResponse> {
  return callSidecar('terminalCreateSession', () => sidecarApi().terminalCreateSession(request));
}
export async function terminalListSessions(request: TerminalListSessionsRequest = {}): Promise<TerminalListSessionsResponse> {
  return callSidecar('terminalListSessions', () => sidecarApi().terminalListSessions(request));
}
export async function terminalAttach(request: TerminalAttachRequest): Promise<TerminalAttachResponse> {
  return callSidecar('terminalAttach', () => sidecarApi().terminalAttach(request));
}
export async function terminalDetach(request: TerminalDetachRequest): Promise<TerminalDetachResponse> {
  return callSidecar('terminalDetach', () => sidecarApi().terminalDetach(request));
}
export async function terminalSendInput(request: TerminalSendInputRequest): Promise<TerminalSendInputResponse> {
  return callSidecar('terminalSendInput', () => sidecarApi().terminalSendInput(request));
}
export async function terminalResize(request: TerminalResizeRequest): Promise<TerminalResizeResponse> {
  return callSidecar('terminalResize', () => sidecarApi().terminalResize(request));
}
export async function terminalTerminate(request: TerminalTerminateRequest): Promise<TerminalTerminateResponse> {
  return callSidecar('terminalTerminate', () => sidecarApi().terminalTerminate(request));
}
export async function terminalReconnect(request: TerminalReconnectRequest): Promise<TerminalAttachResponse> {
  return callSidecar('terminalReconnect', () => sidecarApi().terminalReconnect(request));
}
export async function terminalAckOutput(request: TerminalAckOutputRequest): Promise<TerminalAckOutputResponse> {
  return callSidecar('terminalAckOutput', () => sidecarApi().terminalAckOutput(request));
}
export function onTerminalOutput(callback: (event: TerminalOutputEvent) => void): Promise<() => void> {
  return listenSidecar('terminal output', () => sidecarApi().onTerminalOutput(callback));
}
export function onTerminalStatus(callback: (event: TerminalStatusEvent) => void): Promise<() => void> {
  return listenSidecar('terminal status', () => sidecarApi().onTerminalStatus(callback));
}
export function onTerminalLifecycle(callback: (event: TerminalLifecycleEvent) => void): Promise<() => void> {
  return listenSidecar('terminal lifecycle', () => sidecarApi().onTerminalLifecycle(callback));
}
export function onTerminalBackpressure(callback: (event: TerminalBackpressureEvent) => void): Promise<() => void> {
  return listenSidecar('terminal backpressure', () => sidecarApi().onTerminalBackpressure(callback));
}
export function onTerminalSessionList(callback: (event: TerminalListSessionsResponse) => void): Promise<() => void> {
  return listenSidecar('terminal session list', () => sidecarApi().onTerminalSessionList(callback));
}

// ── Tasks dashboard snapshot (#1028/#1029) ──

export interface TasksDashboardGetSnapshotRequest {
  project_id: string;
  parent_task_id?: number | null;
  focused_task_id?: number | null;
  include_done?: boolean;
}

export type TasksDashboardGetSnapshotResponse = TasksDashboardSnapshot;

export async function tasksGetDashboardSnapshot(request: TasksDashboardGetSnapshotRequest): Promise<TasksDashboardGetSnapshotResponse> {
  return callSidecar('tasksGetDashboardSnapshot', () => sidecarApi().tasksGetDashboardSnapshot(request as TasksDashboardSnapshotRequest));
}
