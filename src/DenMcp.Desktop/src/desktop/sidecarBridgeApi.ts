import type { AppAgentBuildContextRequest, AppAgentCancelRequest, AppAgentInvokeToolRequest, AppAgentListToolsRequest, AppAgentResponse, AppAgentSelection, TaskUpdateRequest, TaskUpdateResponse, TasksDashboardSnapshot, TasksDashboardSnapshotRequest } from '../electron/sidecarProtocol.ts';

import { validateBuildContextResponse, validateCancelResponse, validateInvokeToolResponse, validateListToolsResponse } from './sidecarBridgeValidation.ts';

// Re-export canonical app-agent selection type from the protocol layer
// so consumers import from a single canonical path.
export type { AppAgentSelection } from '../electron/sidecarProtocol.ts';

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
  consoleRunCommandWithProgress(request: ConsoleCommandRunRequest, onProgress: (line: ConsoleCommandLine) => void): Promise<ConsoleCommandRunResponse>;
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
  taskUpdate(request: TaskUpdateRequest): Promise<TaskUpdateResponse>;
  messagesGetSnapshot(request: MessagesGetSnapshotRequest): Promise<MessagesGetSnapshotResponse>;
  documentsList(request: Record<string, unknown>): Promise<DocumentsListBridgeResponse>;
  documentGet(request: Record<string, unknown>): Promise<DocumentGetBridgeResponse>;
  documentStore(request: Record<string, unknown>): Promise<DocumentStoreBridgeResponse>;
  appAgentBuildContext(request?: AppAgentBuildContextRequest): Promise<AppAgentResponse>;
  appAgentListTools(request?: AppAgentListToolsRequest): Promise<AppAgentResponse>;
  appAgentInvokeTool(request: AppAgentInvokeToolRequest): Promise<AppAgentResponse>;
  appAgentCancelRequest(request: AppAgentCancelRequest): Promise<AppAgentResponse>;
  collaborationSendCompiledResponse(request: CollaborationSendCompiledResponseRequest): Promise<CollaborationSendCompiledResponseResponse>;
  onTerminalOutput(listener: (event: TerminalOutputEvent) => void): () => void;
  onTerminalStatus(listener: (event: TerminalStatusEvent) => void): () => void;
  onTerminalLifecycle(listener: (event: TerminalLifecycleEvent) => void): () => void;
  onTerminalBackpressure(listener: (event: TerminalBackpressureEvent) => void): () => void;
  onTerminalSessionList(listener: (event: TerminalListSessionsResponse) => void): () => void;
  onAppAgentRunState(listener: (event: AppAgentResponse) => void): () => void;
  onAppAgentToolCallState(listener: (event: AppAgentResponse) => void): () => void;
  onCollaborationDelivery(listener: (event: CollaborationDeliveryBridgeEvent) => void): () => void;
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
  includeHiddenSpaces: boolean;
  includeArchivedSpaces: boolean;
}

export interface SaveOperatorSettingsRequest {
  denBaseUrl: string;
  sourceDisplayName: string | null;
  pollIntervalSeconds?: number;
  maxChangedFiles?: number;
  includeHiddenSpaces?: boolean;
  includeArchivedSpaces?: boolean;
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

export interface DenSpace {
  id: string;
  name: string;
  kind: string;
  visibility: string;
  owner: string | null;
  rootPath: string | null;
  description: string | null;
  createdAt: string | null;
  updatedAt: string | null;
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
  spaceCount: number;
  spaces: DenSpace[];
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

/**
 * Run a console command with per-request progress frame delivery.
 * Progress lines are delivered to `onProgress` as they arrive from the
 * sidecar bridge, enabling incremental rendering before the final response.
 * Falls back to the batch-only consoleRunCommand when onProgress is not provided.
 */
export async function consoleRunCommandWithProgress(
  request: ConsoleCommandRunRequest,
  onProgress?: (line: ConsoleCommandLine) => void,
): Promise<ConsoleCommandRunResponse> {
  if (!onProgress) {
    return consoleRunCommand(request);
  }

  const api = sidecarApi();
  if (!api.consoleRunCommandWithProgress) {
    return consoleRunCommand(request);
  }

  return callSidecar('consoleRunCommandWithProgress', () =>
    api.consoleRunCommandWithProgress(request, onProgress),
  );
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
  can_deliver_compiled_response?: boolean;
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

// ── App agent types (task #1023/#908) ──
// AppAgentSelection is re-exported from '../electron/sidecarProtocol.ts' (see top of file).

export interface AppAgentToolDefinition {
  name: string;
  display_name: string;
  category: string;
  description: string;
  enabled: boolean;
  disabled_reason?: string | null;
  requires_explicit_target: boolean;
  destructive: boolean;
  requires_confirmation: boolean;
  cancellable: boolean;
  audit_event_type: string;
  capabilities: string[];
}

export interface AppAgentDisabledTool {
  name: string;
  reason: string;
}

export interface AppAgentTaskDependencySummary {
  task_id: number;
  title?: string | null;
  status?: string | null;
}

export interface AppAgentReviewFindingSummary {
  id?: number | null;
  category?: string | null;
  summary?: string | null;
  status?: string | null;
}

export interface AppAgentDenMessageSummary {
  id: number;
  sender: string;
  intent?: string | null;
  metadata_type?: string | null;
  content_summary: string;
  created_at?: string | null;
}

export interface AppAgentTaskSummary {
  id: number;
  project_id: string;
  title: string;
  status: string;
  priority: number;
  tags: string[];
  dependencies: AppAgentTaskDependencySummary[];
  recent_messages: AppAgentDenMessageSummary[];
  open_review_findings: AppAgentReviewFindingSummary[];
  review_state: string;
}

export interface AppAgentGitSnapshot {
  snapshots: LocalGitSnapshot[];
  selected_snapshot: LocalGitSnapshot | null;
}

export interface AppAgentSessionCapabilities {
  can_read_activity: boolean;
  can_attach: boolean;
  can_send_input: boolean;
  can_terminate: boolean;
  can_kill: boolean;
  reason?: string | null;
}

export interface AppAgentSessionSummary {
  session_id: string;
  title?: string | null;
  display_name?: string | null;
  kind: string;
  backend: string;
  status: string;
  project_id?: string | null;
  task_id?: number | null;
  workspace_id?: string | null;
  current_command?: string | null;
  capabilities: AppAgentSessionCapabilities;
  warnings: string[];
  last_activity_summary?: string | null;
}

export interface AppAgentCommandSummary {
  name: string;
  display_name: string;
  description: string;
  needs_target: boolean;
}

export interface AppAgentTerminalExcerpt {
  session_id: string;
  items: unknown[];
  next_cursor?: string | null;
  truncated: boolean;
  source: string;
  raw_terminal_bytes_persisted: boolean;
}

export interface AppAgentCollaborationState {
  active_session_id?: string | null;
  annotated_source_ref?: string | null;
  compiled_response_draft_ref?: string | null;
  summary: string;
}

export interface AppAgentAuthorityHints {
  allowed_tools: AppAgentToolDefinition[];
  disabled_tools: AppAgentDisabledTool[];
  cancel_available: boolean;
  stop_available: boolean;
  sandbox_scope: string;
}

export interface AppAgentAuditCorrelation {
  agent_run_id: string;
  operator_session_id?: string | null;
  trace_id: string;
  parent_request_id?: string | null;
  task_id?: number | null;
  project_id?: string | null;
}

export interface AppAgentContextPacket {
  context_version: number;
  selection: AppAgentSelection;
  task_summary: AppAgentTaskSummary | null;
  git_snapshot: AppAgentGitSnapshot;
  session_summaries: AppAgentSessionSummary[];
  command_summaries: AppAgentCommandSummary[];
  terminal_excerpts: AppAgentTerminalExcerpt[];
  collaboration_state: AppAgentCollaborationState;
  authority: AppAgentAuthorityHints;
  audit: AppAgentAuditCorrelation;
  warnings: string[];
  built_at: string;
}

export interface AppAgentBuildContextResponse {
  context: AppAgentContextPacket;
}

export interface AppAgentListToolsResponse {
  tools: AppAgentToolDefinition[];
}

export interface AppAgentInvokeToolResponse {
  tool_name: string;
  tool_call_id: string;
  status: string;
  result: unknown;
  audit: AppAgentAuditCorrelation;
}

export interface AppAgentCancelResponse {
  request_id: string;
  accepted: boolean;
  status: string;
}

export interface AppAgentRunStateEvent {
  agent_run_id: string;
  request_id?: string | null;
  status: string;
  tool_name?: string | null;
  message?: string | null;
  observed_at: string;
}

export interface AppAgentToolCallStateEvent {
  tool_call_id: string;
  agent_run_id: string;
  tool_name: string;
  status: string;
  started_at?: string | null;
  completed_at?: string | null;
  cancellable: boolean;
  target_summary?: string | null;
}

export async function appAgentBuildContext(request?: AppAgentBuildContextRequest): Promise<AppAgentBuildContextResponse> {
  const raw = await callSidecar('appAgentBuildContext', () => sidecarApi().appAgentBuildContext(request));
  return validateBuildContextResponse<AppAgentBuildContextResponse>(raw);
}

export async function appAgentListTools(request?: AppAgentListToolsRequest): Promise<AppAgentListToolsResponse> {
  const raw = await callSidecar('appAgentListTools', () => sidecarApi().appAgentListTools(request));
  return validateListToolsResponse<AppAgentListToolsResponse>(raw);
}

export async function appAgentInvokeTool(request: AppAgentInvokeToolRequest): Promise<AppAgentInvokeToolResponse> {
  const raw = await callSidecar('appAgentInvokeTool', () => sidecarApi().appAgentInvokeTool(request));
  return validateInvokeToolResponse<AppAgentInvokeToolResponse>(raw);
}

export async function appAgentCancelRequest(request: AppAgentCancelRequest): Promise<AppAgentCancelResponse> {
  const raw = await callSidecar('appAgentCancelRequest', () => sidecarApi().appAgentCancelRequest(request));
  return validateCancelResponse<AppAgentCancelResponse>(raw);
}

export function onAppAgentRunState(callback: (event: AppAgentRunStateEvent) => void): Promise<() => void> {
  return listenSidecar('app agent run state', () => sidecarApi().onAppAgentRunState((event: unknown) => callback(event as AppAgentRunStateEvent)));
}

export function onAppAgentToolCallState(callback: (event: AppAgentToolCallStateEvent) => void): Promise<() => void> {
  return listenSidecar('app agent tool call state', () => sidecarApi().onAppAgentToolCallState((event: unknown) => callback(event as AppAgentToolCallStateEvent)));
}

// ── Tasks dashboard snapshot (#1028/#1029) ──

/** Re-export the canonical protocol request type to avoid duplication drift. */
export type TasksDashboardGetSnapshotRequest = TasksDashboardSnapshotRequest;

export type TasksDashboardGetSnapshotResponse = TasksDashboardSnapshot;

// ── Messages tab snapshot (task #1092) ──────────────────────────────────────

export interface MessagesGetSnapshotRequest {
  project_id: string;
  task_id?: number | null;
  thread_id?: number | null;
  since?: string | null;
  limit?: number;
  unread_for?: string | null;
}

export interface MessagesMessageRow {
  id: number;
  sender: string;
  content: string;
  intent: string | null;
  metadata: Record<string, unknown> | null;
  metadata_type: string | null;
  task_id: number | null;
  thread_id: number | null;
  created_at: string | null;
  is_unread: boolean;
  content_summary: string;
}

export interface MessagesFreshness {
  source: string;
  generated_at: string | null;
  is_partial: boolean;
  warnings: string[];
  errors: string[];
}

export interface MessagesGetSnapshotResponse {
  snapshot_id: string;
  project_id: string;
  task_id: number | null;
  thread_id: number | null;
  generated_at: string;
  messages: MessagesMessageRow[];
  thread_root: MessagesMessageRow | null;
  unread_count: number;
  total_count: number;
  freshness: MessagesFreshness;
}

// ── Documents tab bridge API (task #1147) ──────────────────────────────────

export interface DocumentsListItem {
  slug: string;
  title: string;
  doc_type: string;
  tags: string[];
}

export interface DocumentsListBridgeResponse {
  documents: DocumentsListItem[];
}

export interface DocumentGetBridgeResponse {
  slug: string;
  title: string;
  content: string;
  doc_type: string;
  tags: string[];
}

export interface DocumentStoreBridgeRequest {
  project_id: string;
  slug: string;
  title: string;
  content: string;
  doc_type?: string | null;
}

export interface DocumentStoreBridgeResponse {
  slug: string;
  title: string;
  created: boolean;
}

export async function documentsList(request: { project_id: string }): Promise<DocumentsListBridgeResponse> {
  return callSidecar('documentsList', () =>
    sidecarApi().documentsList(request as unknown as Record<string, unknown>),
  );
}

export async function documentGet(request: { project_id: string; slug: string }): Promise<DocumentGetBridgeResponse> {
  return callSidecar('documentGet', () =>
    sidecarApi().documentGet(request as unknown as Record<string, unknown>),
  );
}

export async function documentStore(request: DocumentStoreBridgeRequest): Promise<DocumentStoreBridgeResponse> {
  return callSidecar('documentStore', () =>
    sidecarApi().documentStore(request as unknown as Record<string, unknown>),
  );
}

export async function tasksGetDashboardSnapshot(request: TasksDashboardGetSnapshotRequest): Promise<TasksDashboardGetSnapshotResponse> {
  return callSidecar('tasksGetDashboardSnapshot', () => sidecarApi().tasksGetDashboardSnapshot(request));
}

export async function taskUpdate(request: TaskUpdateRequest): Promise<TaskUpdateResponse> {
  return callSidecar('taskUpdate', () => sidecarApi().taskUpdate(request));
}

export async function messagesGetSnapshot(request: MessagesGetSnapshotRequest): Promise<MessagesGetSnapshotResponse> {
  return callSidecar('messagesGetSnapshot', () => sidecarApi().messagesGetSnapshot(request));
}

// ── Collaboration live-delivery bridge (task #1074) ─────────────────────────

export interface CollaborationSendCompiledResponseRequest {
  session_id: number;
  compiled_text?: string | null;
  target_session_id?: string | null;
  post_to_den?: boolean;
  requested_by?: string | null;
}

export interface CollaborationDenPostResult {
  posted: boolean;
  draft_id?: number | null;
  project_id?: string | null;
  error?: string | null;
}

export interface CollaborationDeliveryResultBridge {
  status: string;
  target_session_id?: string | null;
  target_session_status?: string | null;
  can_deliver: boolean;
  reason?: string | null;
  error?: string | null;
}

export interface CollaborationSendCompiledResponseResponse {
  compiled_text: string;
  den_post: CollaborationDenPostResult;
  delivery: CollaborationDeliveryResultBridge;
  session_id: number;
  target_session_id?: string | null;
}

export interface CollaborationDeliveryBridgeEvent {
  session_id: string;
  status: string;
  compiled_text_length: number;
  reason?: string | null;
  observed_at: string;
}

export async function collaborationSendCompiledResponse(
  request: CollaborationSendCompiledResponseRequest,
): Promise<CollaborationSendCompiledResponseResponse> {
  return callSidecar(
    'collaborationSendCompiledResponse',
    () => sidecarApi().collaborationSendCompiledResponse(request),
    30_000, // longer timeout for Den + live delivery
  );
}

export function onCollaborationDelivery(callback: (event: CollaborationDeliveryBridgeEvent) => void): Promise<() => void> {
  return listenSidecar('collaboration delivery', () => sidecarApi().onCollaborationDelivery((event: unknown) => callback(event as CollaborationDeliveryBridgeEvent)));
}
