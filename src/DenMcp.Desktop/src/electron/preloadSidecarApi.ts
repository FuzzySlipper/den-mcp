import type { BridgeEventFrame } from '../bridge/contract.ts';
import type {
  DesktopDiffSnapshotLatestResult,
  LatestDiffSnapshotRequest,
  LocalGitSnapshot,
  LocalSessionSnapshot,
  LocalSessionSnapshotList,
  LocalSnapshotList,
  OperatorSettings,
  OperatorStatus,
  SaveOperatorSettingsRequest,
} from '../desktop/sidecarBridgeApi.ts';
// Collaboration send-compiled-response bridge: re-introduced (task #1074) as
// a typed, allow-listed live-delivery path. The renderer saves to Den first
// (Den-post-first), then optionally delivers through this bridge command when
// running under Electron with a live session target.
import type { SidecarBridgeClient } from './sidecarProtocol.ts';
import { createSidecarBridgeFacade, type SidecarHealthResponse, type SidecarCapabilitiesResponse, type ConsoleCommandDefinition, type ConsoleCommandRunRequest, type ConsoleCommandRunResponse, type ConsoleCommandListResponse, type TerminalAckOutputRequest, type TerminalAttachRequest, type TerminalCreateSessionRequest, type TerminalDetachRequest, type TerminalListSessionsRequest, type TerminalReadActivityRequest, type TerminalReconnectRequest, type TerminalResizeRequest, type TerminalResponse, type TerminalEventPayload, type TerminalSendInputRequest, type TerminalTerminateRequest, type AppAgentBuildContextRequest, type AppAgentCancelRequest, type AppAgentInvokeToolRequest, type AppAgentListToolsRequest, type AppAgentResponse, type TasksDashboardSnapshotRequest, type TasksDashboardSnapshot, type TaskUpdateRequest, type TaskUpdateResponse, type MessagesSnapshotRequest, type MessagesSnapshot, type DocumentsListRequest, type DocumentsListResponse, type DocumentGetRequest, type DocumentGetResponse, type DocumentStoreRequest, type DocumentStoreResponse } from './sidecarProtocol.ts';

export interface ShellAppearanceSettings {
  theme: string;
  accent: string;
  density: string;
  bodyFont: string;
  railMode: string;
  consoleMode: string;
  activeTab: string;
}

export interface DenDesktopSidecarApi {
  getHealth(): Promise<SidecarHealthResponse>;
  getCapabilities(): Promise<SidecarCapabilitiesResponse>;
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
  /**
   * Run a console command with per-request progress frame delivery.
   * The `onProgress` callback receives each progress frame as it arrives
   * from the bridge transport, enabling incremental rendering before the
   * final response resolves.
   */
  consoleRunCommandWithProgress(request: ConsoleCommandRunRequest, onProgress: (frame: unknown) => void): Promise<ConsoleCommandRunResponse>;
  appAgentBuildContext(request?: AppAgentBuildContextRequest): Promise<AppAgentResponse>;
  appAgentListTools(request?: AppAgentListToolsRequest): Promise<AppAgentResponse>;
  appAgentInvokeTool(request: AppAgentInvokeToolRequest): Promise<AppAgentResponse>;
  appAgentCancelRequest(request: AppAgentCancelRequest): Promise<AppAgentResponse>;
  // Collaboration live-delivery bridge: typed path for delivering compiled
  // responses through the sidecar when running under Electron (task #1074).
  collaborationSendCompiledResponse(request: Record<string, unknown>): Promise<Record<string, unknown>>;
  tasksGetDashboardSnapshot(request: TasksDashboardSnapshotRequest): Promise<TasksDashboardSnapshot>;
  taskUpdate(request: TaskUpdateRequest): Promise<TaskUpdateResponse>;
  messagesGetSnapshot(request: MessagesSnapshotRequest): Promise<MessagesSnapshot>;
  documentsList(request: DocumentsListRequest): Promise<DocumentsListResponse>;
  documentGet(request: DocumentGetRequest): Promise<DocumentGetResponse>;
  documentStore(request: DocumentStoreRequest): Promise<DocumentStoreResponse>;
  terminalCreateSession(request: TerminalCreateSessionRequest): Promise<TerminalResponse>;
  terminalListSessions(request?: TerminalListSessionsRequest): Promise<TerminalResponse>;
  terminalReadActivity(request: TerminalReadActivityRequest): Promise<TerminalResponse>;
  terminalAttach(request: TerminalAttachRequest): Promise<TerminalResponse>;
  terminalDetach(request: TerminalDetachRequest): Promise<TerminalResponse>;
  terminalSendInput(request: TerminalSendInputRequest): Promise<TerminalResponse>;
  terminalResize(request: TerminalResizeRequest): Promise<TerminalResponse>;
  terminalTerminate(request: TerminalTerminateRequest): Promise<TerminalResponse>;
  terminalReconnect(request: TerminalReconnectRequest): Promise<TerminalResponse>;
  terminalAckOutput(request: TerminalAckOutputRequest): Promise<TerminalResponse>;
  onTerminalOutput(listener: (event: TerminalEventPayload) => void): () => void;
  onTerminalStatus(listener: (event: TerminalEventPayload) => void): () => void;
  onTerminalLifecycle(listener: (event: TerminalEventPayload) => void): () => void;
  onTerminalBackpressure(listener: (event: TerminalEventPayload) => void): () => void;
  onTerminalSessionList(listener: (event: TerminalResponse) => void): () => void;
  onAppAgentRunState(listener: (event: AppAgentResponse) => void): () => void;
  onAppAgentToolCallState(listener: (event: AppAgentResponse) => void): () => void;
  onCollaborationDelivery(listener: (event: Record<string, unknown>) => void): () => void;
  onOperatorStatus(listener: (status: OperatorStatus) => void): () => void;
  onGitSnapshots(listener: (snapshots: LocalGitSnapshot[]) => void): () => void;
  onSessionSnapshots(listener: (snapshots: LocalSessionSnapshot[]) => void): () => void;
}

export interface BridgeEventSource {
  subscribe(listener: (frame: BridgeEventFrame) => void): () => void;
}

export function createDenDesktopSidecarApi(
  client: SidecarBridgeClient,
  events: BridgeEventSource,
): DenDesktopSidecarApi {
  const facade: ReturnType<typeof createSidecarBridgeFacade> = createSidecarBridgeFacade(client);
  return Object.freeze({
    getHealth: facade.getHealth,
    getCapabilities: facade.getCapabilities,
    getOperatorStatus: () => facade.getOperatorStatus<OperatorStatus>(),
    getSettings: () => facade.getSettings<OperatorSettings>(),
    saveOperatorSettings: (request: SaveOperatorSettingsRequest) =>
      facade.saveOperatorSettings<SaveOperatorSettingsRequest, OperatorSettings>(request),
    getAppearanceSettings: <T>() => facade.getAppearanceSettings<T>(),
    saveAppearanceSettings: <TRequest, TResponse>(request: TRequest) =>
      facade.saveAppearanceSettings<TRequest, TResponse>(request),
    refreshNow: facade.refreshNow,
    listLocalSnapshots: () => facade.listLocalSnapshots<LocalSnapshotList>(),
    listLocalSessionSnapshots: () => facade.listLocalSessionSnapshots<LocalSessionSnapshotList>(),
    getLatestDiffSnapshot: (request: LatestDiffSnapshotRequest) =>
      facade.getLatestDiffSnapshot<LatestDiffSnapshotRequest, DesktopDiffSnapshotLatestResult>(request),
    consoleListCommands: () => facade.consoleListCommands<ConsoleCommandListResponse>(),
    consoleRunCommand: (request: ConsoleCommandRunRequest) =>
      facade.consoleRunCommand<ConsoleCommandRunRequest, ConsoleCommandRunResponse>(request),
    consoleRunCommandWithProgress: (request: ConsoleCommandRunRequest, onProgress?: (frame: unknown) => void) =>
      facade.consoleRunCommand<ConsoleCommandRunRequest, ConsoleCommandRunResponse>(request, { expectsProgress: true, onProgress }),
    appAgentBuildContext: (request?: AppAgentBuildContextRequest) => facade.appAgentBuildContext(request ?? {}),
    appAgentListTools: (request?: AppAgentListToolsRequest) => facade.appAgentListTools(request ?? {}),
    appAgentInvokeTool: (request: AppAgentInvokeToolRequest) => facade.appAgentInvokeTool(request),
    appAgentCancelRequest: (request: AppAgentCancelRequest) => facade.appAgentCancelRequest(request),
    collaborationSendCompiledResponse: (request: Record<string, unknown>) =>
      facade.collaborationSendCompiledResponse(request as Record<string, import('../bridge/contract.ts').JsonValue>) as Promise<Record<string, unknown>>,
    // collaborationSendCompiledResponse: typed live-delivery path (task #1074).
    tasksGetDashboardSnapshot: (request: TasksDashboardSnapshotRequest) => facade.tasksGetDashboardSnapshot(request),
    taskUpdate: (request: TaskUpdateRequest) => facade.taskUpdate(request),
    messagesGetSnapshot: (request: MessagesSnapshotRequest) => facade.messagesGetSnapshot(request),
    documentsList: (request: DocumentsListRequest) => facade.documentsList(request),
    documentGet: (request: DocumentGetRequest) => facade.documentGet(request),
    documentStore: (request: DocumentStoreRequest) => facade.documentStore(request),
    terminalCreateSession: (request: TerminalCreateSessionRequest) => facade.terminalCreateSession(request),
    terminalListSessions: (request?: TerminalListSessionsRequest) => facade.terminalListSessions(request ?? {}),
    terminalReadActivity: (request: TerminalReadActivityRequest) => facade.terminalReadActivity(request),
    terminalAttach: (request: TerminalAttachRequest) => facade.terminalAttach(request),
    terminalDetach: (request: TerminalDetachRequest) => facade.terminalDetach(request),
    terminalSendInput: (request: TerminalSendInputRequest) => facade.terminalSendInput(request),
    terminalResize: (request: TerminalResizeRequest) => facade.terminalResize(request),
    terminalTerminate: (request: TerminalTerminateRequest) => facade.terminalTerminate(request),
    terminalReconnect: (request: TerminalReconnectRequest) => facade.terminalReconnect(request),
    terminalAckOutput: (request: TerminalAckOutputRequest) => facade.terminalAckOutput(request),
    onTerminalOutput(listener: (event: TerminalEventPayload) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.terminal.output') return;
        facade.assertTerminalOutputEvent(frame);
        listener(frame.payload as unknown as TerminalEventPayload);
      });
    },
    onTerminalStatus(listener: (event: TerminalEventPayload) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.terminal.session_status_changed') return;
        facade.assertTerminalSessionStatusEvent(frame);
        listener(frame.payload as unknown as TerminalEventPayload);
      });
    },
    onTerminalLifecycle(listener: (event: TerminalEventPayload) => void) {
      return events.subscribe((frame) => {
        if (frame.event === 'den.terminal.exit') {
          facade.assertTerminalExitEvent(frame);
          listener({ ...(frame.payload as unknown as TerminalEventPayload), event: frame.event });
        } else if (frame.event === 'den.terminal.error') {
          facade.assertTerminalErrorEvent(frame);
          listener({ ...(frame.payload as unknown as TerminalEventPayload), event: frame.event });
        } else if (frame.event === 'den.terminal.heartbeat') {
          facade.assertTerminalHeartbeatEvent(frame);
          listener({ ...(frame.payload as unknown as TerminalEventPayload), event: frame.event });
        } else if (frame.event === 'den.terminal.replay_complete') {
          facade.assertTerminalReplayCompleteEvent(frame);
          listener({ ...(frame.payload as unknown as TerminalEventPayload), event: frame.event });
        }
      });
    },
    onTerminalBackpressure(listener: (event: TerminalEventPayload) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.terminal.backpressure') return;
        facade.assertTerminalBackpressureEvent(frame);
        listener(frame.payload as unknown as TerminalEventPayload);
      });
    },
    onTerminalSessionList(listener: (event: TerminalResponse) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.terminal.session_list_updated') return;
        facade.assertTerminalSessionListEvent(frame);
        listener(frame.payload as unknown as TerminalResponse);
      });
    },
    onAppAgentRunState(listener: (event: AppAgentResponse) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.app_agent.run_state_changed') return;
        facade.assertAppAgentRunStateEvent(frame);
        listener(frame.payload as unknown as AppAgentResponse);
      });
    },
    onAppAgentToolCallState(listener: (event: AppAgentResponse) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.app_agent.tool_call_state_changed') return;
        facade.assertAppAgentToolCallStateEvent(frame);
        listener(frame.payload as unknown as AppAgentResponse);
      });
    },
    onCollaborationDelivery(listener: (event: Record<string, unknown>) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den.collaboration.delivery_state_changed') return;
        facade.assertCollaborationDeliveryEvent(frame);
        listener(frame.payload as unknown as Record<string, unknown>);
      });
    },
    onOperatorStatus(listener: (status: OperatorStatus) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den://operator-status') return;
        facade.assertOperatorStatusEvent(frame);
        listener(frame.payload as unknown as OperatorStatus);
      });
    },
    onGitSnapshots(listener: (snapshots: LocalGitSnapshot[]) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den://git-snapshot-updated') return;
        facade.assertGitSnapshotsEvent(frame);
        listener(frame.payload as unknown as LocalGitSnapshot[]);
      });
    },
    onSessionSnapshots(listener: (snapshots: LocalSessionSnapshot[]) => void) {
      return events.subscribe((frame) => {
        if (frame.event !== 'den://session-snapshot-updated') return;
        facade.assertSessionSnapshotsEvent(frame);
        listener(frame.payload as unknown as LocalSessionSnapshot[]);
      });
    },
  });
}
