import type {
  BridgeCallOptions,
  BridgeCommandSpec,
  BridgeEventFrame,
  BridgeEventSpec,
  BridgeSchemaBundle,
  CheckedBridgeClient,
  JsonValue,
} from '../bridge/contract.ts';
import { createBridgeCommandFacade } from '../bridge/contract.ts';

export const DEN_DESKTOP_READY_PREFIX = 'DEN_DESKTOP_BRIDGE_READY ';
export const DEN_DESKTOP_PROTOCOL_VERSION = '1.0';
export const DEN_DESKTOP_SCHEMA_VERSION = 'den-desktop@2026-04-29';
export const DEN_DESKTOP_SCHEMA_BUNDLE_ID = 'den-desktop.sidecar@2026-04-29';

export interface SidecarReadySentinel {
  port: number;
  endpoint_path: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  app_id: string;
  app_version: string;
}

export interface SidecarHealthResponse {
  process_id: number;
  uptime_ms: number;
  ready_state: string;
  app_id: string;
  app_version: string;
  config_path: string;
  log_path?: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  active_request_count: number;
  degraded_subsystems: string[];
  last_error?: JsonValue;
}

export interface SidecarCapabilitiesResponse {
  app_id: string;
  app_version: string;
  protocol_version: string;
  schema_version: string;
  schema_bundle_id: string;
  supported_transports: string[];
  commands: JsonValue[];
  events: JsonValue[];
  feature_flags: string[];
}

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

export interface TerminalCreateSessionRequest extends Record<string, JsonValue | undefined> {
  project_id: string;
  task_id?: number | null;
  workspace_id?: string | null;
  title?: string | null;
  cwd?: string | null;
  backend?: string;
}

export interface TerminalAttachRequest extends Record<string, JsonValue | undefined> {
  terminal_protocol_version?: string;
  session_id: string;
  mode?: 'terminal_stream' | 'activity_only' | 'external_attach_info' | string;
  client_id?: string | null;
  viewport?: { cols: number; rows: number } | null;
  replay?: { after_cursor?: string | null; max_bytes?: number; max_chunks?: number } | null;
}

export interface TerminalDetachRequest extends Record<string, JsonValue | undefined> {
  stream_id: string;
  session_id: string;
  reason?: string | null;
}

export interface TerminalSendInputRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  stream_id?: string | null;
  input_id?: string | null;
  encoding?: 'utf8' | 'base64' | string;
  data: string;
  byte_count?: number;
}

export interface TerminalResizeRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  stream_id?: string | null;
  cols: number;
  rows: number;
}

export interface TerminalTerminateRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  stream_id?: string | null;
  mode?: string;
  reason?: string | null;
  requested_by?: string | null;
}

export interface TerminalReconnectRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  previous_stream_id?: string | null;
  last_seen_cursor?: string | null;
  viewport?: { cols: number; rows: number } | null;
}

export interface TerminalAckOutputRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  stream_id?: string | null;
  ack_cursor?: string | null;
  received_bytes?: number;
}

export interface TerminalReadActivityRequest extends Record<string, JsonValue | undefined> {
  session_id: string;
  after_cursor?: string | null;
  limit?: number;
}

export interface TerminalListSessionsRequest extends Record<string, JsonValue | undefined> {
  kind?: string | null;
  backend?: string | null;
  status?: string | null;
}

export interface AppAgentSelection {
  project_id?: string | null;
  task_id?: number | null;
  workspace_id?: string | null;
  current_route?: string | null;
  current_tab?: string | null;
  session_id?: string | null;
  selected_file_path?: string | null;
  selected_diff_range?: string | null;
}

export interface TasksDashboardSnapshotRequest extends Record<string, JsonValue | undefined> {
  project_id: string;
  parent_task_id?: number | null;
  focused_task_id?: number | null;
  include_done?: boolean;
}

export interface TasksDashboardSnapshot {
  snapshot_id: string;
  project_id: string;
  parent_task_id?: number | null;
  focused_task_id?: number | null;
  generated_at: string;
  header: TasksDashboardHeader;
  tasks: TasksDashboardTaskRow[];
  waves: TasksDashboardWave[];
  lanes: TasksDashboardLane[];
  freshness: TasksDashboardFreshness;
}

// ── Messages tab projection (task #1092) ──────────────────────────────────

export interface MessagesSnapshotRequest extends Record<string, JsonValue | undefined> {
  project_id: string;
  task_id?: number | null;
  thread_id?: number | null;
  since?: string | null;
  limit?: number;
  unread_for?: string | null;
}

export interface MessagesSnapshot {
  snapshot_id: string;
  project_id: string;
  task_id?: number | null;
  thread_id?: number | null;
  generated_at: string;
  messages: MessagesMessageRow[];
  thread_root: MessagesMessageRow | null;
  unread_count: number;
  total_count: number;
  freshness: MessagesFreshness;
}

export interface MessagesMessageRow {
  id: number;
  sender: string;
  content: string;
  intent: string | null;
  metadata: Record<string, JsonValue> | null;
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

export interface TasksDashboardHeader {
  state: string;
  task_count: number;
  done_count?: number;
  active_count?: number;
  review_count?: number;
  blocked_count?: number;
  completion_percent: number;
  total_tokens?: number | null;
  total_cost?: number | null;
  currency?: string | null;
  last_updated_at?: string | null;
}

export interface TasksDashboardTaskRow {
  id: number;
  project_id: string;
  parent_id?: number | null;
  title: string;
  status: string;
  computed_state: string;
  priority: number;
  assigned_to?: string | null;
  tags: string[];
  description: string;
  message_count: number;
  recent_messages: TasksDashboardRecentMessageRow[];
  dependency_count: number;
  subtask_count: number;
  subtask_ids: number[];
  created_at?: string | null;
  dependencies: Array<Record<string, JsonValue>>;
  packets: Array<Record<string, JsonValue>>;
  review: Record<string, JsonValue>;
  run_summary: Record<string, JsonValue>;
  agent_lifecycle: Record<string, JsonValue>;
  session_chips: Array<Record<string, JsonValue>>;
}

export interface TasksDashboardRecentMessageRow {
  id: number;
  sender: string;
  intent?: string | null;
  metadata_type?: string | null;
  content_summary: string;
  created_at?: string | null;
}

export interface TasksDashboardWave {
  index: number;
  label: string;
  state: string;
  task_ids: number[];
  summary?: string | null;
}

export interface TasksDashboardLane {
  lane_key: string;
  task_id?: number | null;
  label: string;
  role?: string | null;
  state: string;
  branch?: string | null;
  worktree_path?: string | null;
  latest_run?: Record<string, JsonValue> | null;
  latest_agent_event?: Record<string, JsonValue> | null;
  session_chips: Array<Record<string, JsonValue>>;
}

// ── Task update bridge (task #1152) ────────────────────────────────────────────

export interface TaskUpdateRequest extends Record<string, JsonValue | undefined> {
  project_id: string;
  task_id: number;
  agent: string;
  title?: string | null;
  description?: string | null;
  status?: string | null;
  priority?: number | null;
  assigned_to?: string | null;
}

export interface TaskUpdateResponse {
  task_id: number;
  project_id: string;
  title: string;
  status: string;
  priority: number;
  assigned_to?: string | null;
}

// ── Documents tab (task #1147) ────────────────────────────────────────────

export interface DocumentsListRequest {
  project_id: string;
}

export interface DocumentsListResponse {
  documents: DocumentListItem[];
}

export interface DocumentListItem {
  slug: string;
  title: string;
  doc_type: string;
  tags: string[];
}

export interface DocumentGetRequest {
  project_id: string;
  slug: string;
}

export interface DocumentGetResponse {
  slug: string;
  title: string;
  content: string;
  doc_type: string;
  tags: string[];
}

export interface DocumentStoreRequest {
  project_id: string;
  slug: string;
  title: string;
  content: string;
  doc_type?: string | null;
}

export interface DocumentStoreResponse {
  slug: string;
  title: string;
  created: boolean;
}

export interface TasksDashboardFreshness {
  source: string;
  generated_at?: string | null;
  is_partial: boolean;
  warnings: string[];
  errors: string[];
}

export interface AppAgentBuildContextRequest {
  selection?: AppAgentSelection;
  agent_run_id?: string | null;
  parent_request_id?: string | null;
  trace_id?: string | null;
  terminal_excerpts?: Array<Record<string, JsonValue>>;
  message_limit?: number;
}

export interface AppAgentListToolsRequest {
  selection?: AppAgentSelection;
}

export interface AppAgentInvokeToolRequest {
  tool_name: string;
  input?: Record<string, JsonValue>;
  selection?: AppAgentSelection;
  agent_run_id?: string | null;
  trace_id?: string | null;
}

export interface AppAgentCancelRequest extends Record<string, JsonValue | undefined> {
  request_id: string;
  reason?: string | null;
}

// Collaboration response live-delivery bridge command and event.
// Re-introduced (task #1074) as a typed, allow-listed path for delivering
// compiled collaboration responses through the sidecar to live agent sessions.
// The renderer saves to Den first (Den-post-first), then optionally delivers
// through this bridge when running under Electron with a live session target.

export type TerminalResponse = Record<string, JsonValue>;
export type TerminalEventPayload = Record<string, JsonValue>;
export type AppAgentResponse = Record<string, JsonValue>;

export const sidecarCommands: Record<string, BridgeCommandSpec<JsonValue, JsonValue>> = {
  terminalCreateSession: {
    command: 'den_desktop.terminal.create_session',
    requestSchema: 'den_desktop.terminal.create_session.request',
    responseSchema: 'den_desktop.terminal.create_session.response',
  },
  terminalListSessions: {
    command: 'den_desktop.terminal.list_sessions',
    requestSchema: 'den_desktop.terminal.list_sessions.request',
    responseSchema: 'den_desktop.terminal.list_sessions.response',
  },
  terminalReadActivity: {
    command: 'den_desktop.terminal.read_activity',
    requestSchema: 'den_desktop.terminal.read_activity.request',
    responseSchema: 'den_desktop.terminal.read_activity.response',
  },
  terminalAttach: {
    command: 'den_desktop.terminal.attach',
    requestSchema: 'den_desktop.terminal.attach.request',
    responseSchema: 'den_desktop.terminal.attach.response',
  },
  terminalDetach: {
    command: 'den_desktop.terminal.detach',
    requestSchema: 'den_desktop.terminal.detach.request',
    responseSchema: 'den_desktop.terminal.detach.response',
  },
  terminalSendInput: {
    command: 'den_desktop.terminal.send_input',
    requestSchema: 'den_desktop.terminal.send_input.request',
    responseSchema: 'den_desktop.terminal.send_input.response',
  },
  terminalResize: {
    command: 'den_desktop.terminal.resize',
    requestSchema: 'den_desktop.terminal.resize.request',
    responseSchema: 'den_desktop.terminal.resize.response',
  },
  terminalTerminate: {
    command: 'den_desktop.terminal.terminate',
    requestSchema: 'den_desktop.terminal.terminate.request',
    responseSchema: 'den_desktop.terminal.terminate.response',
  },
  terminalReconnect: {
    command: 'den_desktop.terminal.reconnect',
    requestSchema: 'den_desktop.terminal.reconnect.request',
    responseSchema: 'den_desktop.terminal.reconnect.response',
  },
  terminalAckOutput: {
    command: 'den_desktop.terminal.ack_output',
    requestSchema: 'den_desktop.terminal.ack_output.request',
    responseSchema: 'den_desktop.terminal.ack_output.response',
  },
  consoleListCommands: {
    command: 'den_desktop.console.list_commands',
    requestSchema: 'den_desktop.console.list_commands.request',
    responseSchema: 'den_desktop.console.list_commands.response',
  },
  consoleRunCommand: {
    command: 'den_desktop.console.run_command',
    requestSchema: 'den_desktop.console.run_command.request',
    responseSchema: 'den_desktop.console.run_command.response',
    supportsProgress: true,
  },
  appAgentBuildContext: {
    command: 'den_desktop.app_agent.build_context',
    requestSchema: 'den_desktop.app_agent.build_context.request',
    responseSchema: 'den_desktop.app_agent.build_context.response',
    supportsCancellation: true,
  },
  appAgentListTools: {
    command: 'den_desktop.app_agent.list_tools',
    requestSchema: 'den_desktop.app_agent.list_tools.request',
    responseSchema: 'den_desktop.app_agent.list_tools.response',
  },
  appAgentInvokeTool: {
    command: 'den_desktop.app_agent.invoke_tool',
    requestSchema: 'den_desktop.app_agent.invoke_tool.request',
    responseSchema: 'den_desktop.app_agent.invoke_tool.response',
    supportsCancellation: true,
    supportsProgress: true,
  },
  appAgentCancelRequest: {
    command: 'den_desktop.app_agent.cancel_request',
    requestSchema: 'den_desktop.app_agent.cancel_request.request',
    responseSchema: 'den_desktop.app_agent.cancel_request.response',
  },
  collaborationSendCompiledResponse: {
    command: 'den_desktop.collaboration.send_compiled_response',
    requestSchema: 'den_desktop.collaboration.send_compiled_response.request',
    responseSchema: 'den_desktop.collaboration.send_compiled_response.response',
  },
  tasksGetDashboardSnapshot: {
    command: 'den_desktop.tasks.get_dashboard_snapshot',
    requestSchema: 'den_desktop.tasks.get_dashboard_snapshot.request',
    responseSchema: 'den_desktop.tasks.get_dashboard_snapshot.response',
    supportsCancellation: true,
  },
  messagesGetSnapshot: {
    command: 'den_desktop.messages.get_snapshot',
    requestSchema: 'den_desktop.messages.get_snapshot.request',
    responseSchema: 'den_desktop.messages.get_snapshot.response',
    supportsCancellation: true,
  },
  documentsList: {
    command: 'den_desktop.documents.list',
    requestSchema: 'den_desktop.documents.list.request',
    responseSchema: 'den_desktop.documents.list.response',
  },
  documentGet: {
    command: 'den_desktop.documents.get',
    requestSchema: 'den_desktop.documents.get.request',
    responseSchema: 'den_desktop.documents.get.response',
  },
  documentStore: {
    command: 'den_desktop.documents.store',
    requestSchema: 'den_desktop.documents.store.request',
    responseSchema: 'den_desktop.documents.store.response',
  },
  taskUpdate: {
    command: 'den_desktop.tasks.update',
    requestSchema: 'den_desktop.tasks.update.request',
    responseSchema: 'den_desktop.tasks.update.response',
  },
  getHealth: {
    command: 'bridge.get_health',
    requestSchema: 'bridge.get_health.request',
    responseSchema: 'bridge.get_health.response',
  },
  getCapabilities: {
    command: 'bridge.get_capabilities',
    requestSchema: 'bridge.get_capabilities.request',
    responseSchema: 'bridge.get_capabilities.response',
  },
  getOperatorStatus: {
    command: 'den_desktop.operator.get_status',
    requestSchema: 'den_desktop.operator.get_status.request',
    responseSchema: 'den_desktop.operator.get_status.response',
  },
  getSettings: {
    command: 'den_desktop.operator.get_settings',
    requestSchema: 'den_desktop.operator.get_settings.request',
    responseSchema: 'den_desktop.operator.get_settings.response',
  },
  saveOperatorSettings: {
    command: 'den_desktop.operator.save_settings',
    requestSchema: 'den_desktop.operator.save_settings.request',
    responseSchema: 'den_desktop.operator.save_settings.response',
  },
  getAppearanceSettings: {
    command: 'den_desktop.operator.get_appearance_settings',
    requestSchema: 'den_desktop.operator.get_appearance_settings.request',
    responseSchema: 'den_desktop.operator.get_appearance_settings.response',
  },
  saveAppearanceSettings: {
    command: 'den_desktop.operator.save_appearance_settings',
    requestSchema: 'den_desktop.operator.save_appearance_settings.request',
    responseSchema: 'den_desktop.operator.save_appearance_settings.response',
  },
  refreshNow: {
    command: 'den_desktop.operator.refresh_now',
    requestSchema: 'den_desktop.operator.refresh_now.request',
    responseSchema: 'den_desktop.operator.refresh_now.response',
  },
  listLocalSnapshots: {
    command: 'den_desktop.operator.list_local_git_snapshots',
    requestSchema: 'den_desktop.operator.list_local_git_snapshots.request',
    responseSchema: 'den_desktop.operator.list_local_git_snapshots.response',
  },
  listLocalSessionSnapshots: {
    command: 'den_desktop.operator.list_local_session_snapshots',
    requestSchema: 'den_desktop.operator.list_local_session_snapshots.request',
    responseSchema: 'den_desktop.operator.list_local_session_snapshots.response',
  },
  getLatestDiffSnapshot: {
    command: 'den_desktop.operator.get_latest_diff_snapshot',
    requestSchema: 'den_desktop.operator.get_latest_diff_snapshot.request',
    responseSchema: 'den_desktop.operator.get_latest_diff_snapshot.response',
  },
};

export const sidecarEvents: Record<string, BridgeEventSpec<JsonValue>> = {
  operatorStatus: {
    event: 'den://operator-status',
    payloadSchema: 'den://operator-status.payload',
  },
  gitSnapshots: {
    event: 'den://git-snapshot-updated',
    payloadSchema: 'den://git-snapshot-updated.payload',
  },
  sessionSnapshots: {
    event: 'den://session-snapshot-updated',
    payloadSchema: 'den://session-snapshot-updated.payload',
  },
  terminalOutput: {
    event: 'den.terminal.output',
    payloadSchema: 'den.terminal.output.payload',
  },
  terminalReplayComplete: {
    event: 'den.terminal.replay_complete',
    payloadSchema: 'den.terminal.replay_complete.payload',
  },
  terminalExit: {
    event: 'den.terminal.exit',
    payloadSchema: 'den.terminal.exit.payload',
  },
  terminalError: {
    event: 'den.terminal.error',
    payloadSchema: 'den.terminal.error.payload',
  },
  terminalHeartbeat: {
    event: 'den.terminal.heartbeat',
    payloadSchema: 'den.terminal.heartbeat.payload',
  },
  terminalBackpressure: {
    event: 'den.terminal.backpressure',
    payloadSchema: 'den.terminal.backpressure.payload',
  },
  terminalSessionStatus: {
    event: 'den.terminal.session_status_changed',
    payloadSchema: 'den.terminal.session_status_changed.payload',
  },
  terminalSessionList: {
    event: 'den.terminal.session_list_updated',
    payloadSchema: 'den.terminal.session_list_updated.payload',
  },
  appAgentRunState: {
    event: 'den.app_agent.run_state_changed',
    payloadSchema: 'den.app_agent.run_state_changed.payload',
  },
  appAgentToolCallState: {
    event: 'den.app_agent.tool_call_state_changed',
    payloadSchema: 'den.app_agent.tool_call_state_changed.payload',
  },
  collaborationDelivery: {
    event: 'den.collaboration.delivery_state_changed',
    payloadSchema: 'den.collaboration.delivery_state_changed.payload',
  },
  // Collaboration delivery lifecycle event for UI observability.
};

export type SidecarBridgeClient = CheckedBridgeClient<typeof sidecarCommands, typeof sidecarEvents>;

export function createSidecarBridgeFacade(client: SidecarBridgeClient) {
  const facade = createBridgeCommandFacade(client);
  return {
    getHealth: async (): Promise<SidecarHealthResponse> => facade.getHealth({}) as unknown as SidecarHealthResponse,
    getCapabilities: async (): Promise<SidecarCapabilitiesResponse> => facade.getCapabilities({}) as unknown as SidecarCapabilitiesResponse,
    getOperatorStatus: async <T>(): Promise<T> => facade.getOperatorStatus({}) as Promise<T>,
    getSettings: async <T>(): Promise<T> => facade.getSettings({}) as Promise<T>,
    saveOperatorSettings: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.saveOperatorSettings(request as JsonValue) as Promise<TResponse>,
    getAppearanceSettings: async <T>(): Promise<T> => facade.getAppearanceSettings({}) as Promise<T>,
    saveAppearanceSettings: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.saveAppearanceSettings(request as JsonValue) as Promise<TResponse>,
    refreshNow: async (): Promise<void> => { await facade.refreshNow({}); },
    listLocalSnapshots: async <T>(): Promise<T> => facade.listLocalSnapshots({}) as Promise<T>,
    listLocalSessionSnapshots: async <T>(): Promise<T> => facade.listLocalSessionSnapshots({}) as Promise<T>,
    getLatestDiffSnapshot: async <TRequest, TResponse>(request: TRequest): Promise<TResponse> =>
      facade.getLatestDiffSnapshot(request as JsonValue) as Promise<TResponse>,
    consoleListCommands: async <T>(): Promise<T> => facade.consoleListCommands({}) as Promise<T>,
    consoleRunCommand: async <TRequest, TResponse>(
      request: TRequest,
      options?: BridgeCallOptions,
    ): Promise<TResponse> =>
      facade.consoleRunCommand(request as JsonValue, options) as Promise<TResponse>,
    appAgentBuildContext: async <TResponse = AppAgentResponse>(request: AppAgentBuildContextRequest = {}): Promise<TResponse> =>
      facade.appAgentBuildContext(request as unknown as JsonValue) as Promise<TResponse>,
    appAgentListTools: async <TResponse = AppAgentResponse>(request: AppAgentListToolsRequest = {}): Promise<TResponse> =>
      facade.appAgentListTools(request as unknown as JsonValue) as Promise<TResponse>,
    appAgentInvokeTool: async <TResponse = AppAgentResponse>(request: AppAgentInvokeToolRequest): Promise<TResponse> =>
      facade.appAgentInvokeTool(request as unknown as JsonValue) as Promise<TResponse>,
    appAgentCancelRequest: async <TResponse = AppAgentResponse>(request: AppAgentCancelRequest): Promise<TResponse> =>
      facade.appAgentCancelRequest(request as JsonValue) as Promise<TResponse>,
    // collaborationSendCompiledResponse: typed live-delivery bridge path (task #1074).
    collaborationSendCompiledResponse: async <TResponse = Record<string, JsonValue>>(request: Record<string, JsonValue>): Promise<TResponse> =>
      facade.collaborationSendCompiledResponse(request as JsonValue) as Promise<TResponse>,
    tasksGetDashboardSnapshot: async <TResponse = TasksDashboardSnapshot>(request: TasksDashboardSnapshotRequest): Promise<TResponse> =>
      facade.tasksGetDashboardSnapshot(request as JsonValue) as Promise<TResponse>,
    messagesGetSnapshot: async <TResponse = MessagesSnapshot>(request: MessagesSnapshotRequest): Promise<TResponse> =>
      facade.messagesGetSnapshot(request as JsonValue) as Promise<TResponse>,
    documentsList: async <TResponse = DocumentsListResponse>(request: DocumentsListRequest): Promise<TResponse> =>
      facade.documentsList(request as unknown as JsonValue) as Promise<TResponse>,
    documentGet: async <TResponse = DocumentGetResponse>(request: DocumentGetRequest): Promise<TResponse> =>
      facade.documentGet(request as unknown as JsonValue) as Promise<TResponse>,
    documentStore: async <TResponse = DocumentStoreResponse>(request: DocumentStoreRequest): Promise<TResponse> =>
      facade.documentStore(request as unknown as JsonValue) as Promise<TResponse>,
    taskUpdate: async <TResponse = TaskUpdateResponse>(request: TaskUpdateRequest): Promise<TResponse> =>
      facade.taskUpdate(request as JsonValue) as Promise<TResponse>,
    terminalCreateSession: async <TResponse = TerminalResponse>(request: TerminalCreateSessionRequest): Promise<TResponse> =>
      facade.terminalCreateSession(request as JsonValue) as Promise<TResponse>,
    terminalListSessions: async <TResponse = TerminalResponse>(request: TerminalListSessionsRequest = {}): Promise<TResponse> =>
      facade.terminalListSessions(request as JsonValue) as Promise<TResponse>,
    terminalReadActivity: async <TResponse = TerminalResponse>(request: TerminalReadActivityRequest): Promise<TResponse> =>
      facade.terminalReadActivity(request as JsonValue) as Promise<TResponse>,
    terminalAttach: async <TResponse = TerminalResponse>(request: TerminalAttachRequest): Promise<TResponse> =>
      facade.terminalAttach(request as JsonValue) as Promise<TResponse>,
    terminalDetach: async <TResponse = TerminalResponse>(request: TerminalDetachRequest): Promise<TResponse> =>
      facade.terminalDetach(request as JsonValue) as Promise<TResponse>,
    terminalSendInput: async <TResponse = TerminalResponse>(request: TerminalSendInputRequest): Promise<TResponse> =>
      facade.terminalSendInput(request as JsonValue) as Promise<TResponse>,
    terminalResize: async <TResponse = TerminalResponse>(request: TerminalResizeRequest): Promise<TResponse> =>
      facade.terminalResize(request as JsonValue) as Promise<TResponse>,
    terminalTerminate: async <TResponse = TerminalResponse>(request: TerminalTerminateRequest): Promise<TResponse> =>
      facade.terminalTerminate(request as JsonValue) as Promise<TResponse>,
    terminalReconnect: async <TResponse = TerminalResponse>(request: TerminalReconnectRequest): Promise<TResponse> =>
      facade.terminalReconnect(request as JsonValue) as Promise<TResponse>,
    terminalAckOutput: async <TResponse = TerminalResponse>(request: TerminalAckOutputRequest): Promise<TResponse> =>
      facade.terminalAckOutput(request as JsonValue) as Promise<TResponse>,
    assertOperatorStatusEvent(frame: BridgeEventFrame): void {
      client.assertEvent('operatorStatus', frame);
    },
    assertGitSnapshotsEvent(frame: BridgeEventFrame): void {
      client.assertEvent('gitSnapshots', frame);
    },
    assertSessionSnapshotsEvent(frame: BridgeEventFrame): void {
      client.assertEvent('sessionSnapshots', frame);
    },
    assertTerminalOutputEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalOutput', frame);
    },
    assertTerminalReplayCompleteEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalReplayComplete', frame);
    },
    assertTerminalExitEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalExit', frame);
    },
    assertTerminalErrorEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalError', frame);
    },
    assertTerminalHeartbeatEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalHeartbeat', frame);
    },
    assertTerminalBackpressureEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalBackpressure', frame);
    },
    assertTerminalSessionStatusEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalSessionStatus', frame);
    },
    assertTerminalSessionListEvent(frame: BridgeEventFrame): void {
      client.assertEvent('terminalSessionList', frame);
    },
    assertAppAgentRunStateEvent(frame: BridgeEventFrame): void {
      client.assertEvent('appAgentRunState', frame);
    },
    assertAppAgentToolCallStateEvent(frame: BridgeEventFrame): void {
      client.assertEvent('appAgentToolCallState', frame);
    },
    assertCollaborationDeliveryEvent(frame: BridgeEventFrame): void {
      client.assertEvent('collaborationDelivery', frame);
    },
  };
}

export function parseReadySentinelLine(line: string): SidecarReadySentinel | null {
  if (!line.startsWith(DEN_DESKTOP_READY_PREFIX)) {
    return null;
  }

  const parsed = JSON.parse(line.slice(DEN_DESKTOP_READY_PREFIX.length)) as unknown;
  return assertReadySentinel(parsed);
}

export function assertReadySentinel(value: unknown): SidecarReadySentinel {
  const sentinel = expectRecord(value, 'sidecar ready sentinel');
  const result: SidecarReadySentinel = {
    port: expectInteger(sentinel.port, 'sentinel.port'),
    endpoint_path: expectString(sentinel.endpoint_path, 'sentinel.endpoint_path'),
    protocol_version: expectString(sentinel.protocol_version, 'sentinel.protocol_version'),
    schema_version: expectString(sentinel.schema_version, 'sentinel.schema_version'),
    schema_bundle_id: expectString(sentinel.schema_bundle_id, 'sentinel.schema_bundle_id'),
    app_id: expectString(sentinel.app_id, 'sentinel.app_id'),
    app_version: expectString(sentinel.app_version, 'sentinel.app_version'),
  };

  assertProtocolCompatibility(result);
  return result;
}

export function assertProtocolCompatibility(sentinel: SidecarReadySentinel, bundle?: BridgeSchemaBundle): void {
  if (sentinel.protocol_version !== DEN_DESKTOP_PROTOCOL_VERSION) {
    throw new Error(`Unsupported Den Desktop sidecar protocol '${sentinel.protocol_version}'.`);
  }

  if (sentinel.schema_version !== DEN_DESKTOP_SCHEMA_VERSION) {
    throw new Error(`Unsupported Den Desktop sidecar schema '${sentinel.schema_version}'.`);
  }

  if (sentinel.schema_bundle_id !== DEN_DESKTOP_SCHEMA_BUNDLE_ID) {
    throw new Error(`Unsupported Den Desktop sidecar schema bundle '${sentinel.schema_bundle_id}'.`);
  }

  if (bundle) {
    if (bundle.protocol_version !== sentinel.protocol_version) {
      throw new Error(`Sidecar protocol '${sentinel.protocol_version}' does not match bundled client '${bundle.protocol_version}'.`);
    }

    if (bundle.schema_version !== sentinel.schema_version) {
      throw new Error(`Sidecar schema '${sentinel.schema_version}' does not match bundled client '${bundle.schema_version}'.`);
    }

    if (bundle.bundle_id !== sentinel.schema_bundle_id) {
      throw new Error(`Sidecar bundle '${sentinel.schema_bundle_id}' does not match bundled client '${bundle.bundle_id}'.`);
    }
  }
}

function expectRecord(value: unknown, name: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new Error(`${name} must be an object.`);
  }

  return value as Record<string, unknown>;
}

function expectString(value: unknown, name: string): string {
  if (typeof value !== 'string') {
    throw new Error(`${name} must be a string.`);
  }

  return value;
}

function expectInteger(value: unknown, name: string): number {
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new Error(`${name} must be an integer.`);
  }

  return value;
}
