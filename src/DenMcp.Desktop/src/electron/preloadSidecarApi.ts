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
} from '../desktop/tauriApi.ts';
import type { SidecarBridgeClient } from './sidecarProtocol.ts';
import { createSidecarBridgeFacade, type SidecarHealthResponse, type SidecarCapabilitiesResponse, type ConsoleCommandDefinition, type ConsoleCommandRunRequest, type ConsoleCommandRunResponse, type ConsoleCommandListResponse, type TerminalAckOutputRequest, type TerminalAttachRequest, type TerminalCreateSessionRequest, type TerminalDetachRequest, type TerminalListSessionsRequest, type TerminalReadActivityRequest, type TerminalReconnectRequest, type TerminalResizeRequest, type TerminalResponse, type TerminalEventPayload, type TerminalSendInputRequest, type TerminalTerminateRequest, type AppAgentBuildContextRequest, type AppAgentCancelRequest, type AppAgentInvokeToolRequest, type AppAgentListToolsRequest, type AppAgentResponse, type TasksDashboardSnapshotRequest, type TasksDashboardSnapshot } from './sidecarProtocol.ts';

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
  appAgentBuildContext(request?: AppAgentBuildContextRequest): Promise<AppAgentResponse>;
  appAgentListTools(request?: AppAgentListToolsRequest): Promise<AppAgentResponse>;
  appAgentInvokeTool(request: AppAgentInvokeToolRequest): Promise<AppAgentResponse>;
  appAgentCancelRequest(request: AppAgentCancelRequest): Promise<AppAgentResponse>;
  tasksGetDashboardSnapshot(request: TasksDashboardSnapshotRequest): Promise<TasksDashboardSnapshot>;
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
    appAgentBuildContext: (request?: AppAgentBuildContextRequest) => facade.appAgentBuildContext(request ?? {}),
    appAgentListTools: (request?: AppAgentListToolsRequest) => facade.appAgentListTools(request ?? {}),
    appAgentInvokeTool: (request: AppAgentInvokeToolRequest) => facade.appAgentInvokeTool(request),
    appAgentCancelRequest: (request: AppAgentCancelRequest) => facade.appAgentCancelRequest(request),
    tasksGetDashboardSnapshot: (request: TasksDashboardSnapshotRequest) => facade.tasksGetDashboardSnapshot(request),
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
