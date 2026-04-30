/**
 * Electron preload script for Den Desktop.
 *
 * This script runs in the renderer's sandboxed context with access to
 * Node.js APIs, but before the renderer's own JavaScript loads. It uses
 * contextBridge.exposeInMainWorld to expose only the allow-listed
 * DenDesktopSidecarApi to the renderer.
 *
 * Security boundary: the renderer receives no raw token, endpoint URL,
 * Node APIs, shell access, or generic dispatch. All calls are forwarded
 * through IPC to the main process which owns the sidecar connection.
 */

import { contextBridge, ipcRenderer } from 'electron';

const SIDECAR_CALL_CHANNEL = 'den-desktop:sidecar-call';
const SIDECAR_SUBSCRIBE_CHANNEL = 'den-desktop:sidecar-subscribe';
const SIDECAR_UNSUBSCRIBE_CHANNEL = 'den-desktop:sidecar-unsubscribe';

/**
 * Helper to call a sidecar method through IPC.
 * Arguments are serialized; results are returned asynchronously.
 */
async function callSidecar(method: string, ...args: unknown[]): Promise<unknown> {
  return ipcRenderer.invoke(SIDECAR_CALL_CHANNEL, method, ...args);
}

/**
 * Helper to subscribe to a sidecar event through IPC.
 * Returns an unsubscribe function that the renderer can call to clean up.
 */
function subscribeToEvent(eventName: string, listener: (payload: unknown) => void): () => void {
  const channel = `den-desktop:event:${eventName}`;

  // Register the IPC listener first
  const wrappedListener = (_event: Electron.IpcRendererEvent, payload: unknown) => {
    listener(payload);
  };
  ipcRenderer.on(channel, wrappedListener);

  // Then request subscription from main process
  ipcRenderer.invoke(SIDECAR_SUBSCRIBE_CHANNEL, eventName).catch(() => {
    // Subscription failed; listener is harmless
  });

  // Return unsubscribe function
  return () => {
    ipcRenderer.removeListener(channel, wrappedListener);
    ipcRenderer.invoke(SIDECAR_UNSUBSCRIBE_CHANNEL, eventName).catch(() => {
      // Cleanup is best-effort
    });
  };
}

/**
 * Expose the typed DenDesktopSidecarApi to the renderer.
 *
 * Each method is a thin IPC bridge to the main process sidecar connection.
 * No generic dispatch, raw token, endpoint, Node, or shell access is exposed.
 */
contextBridge.exposeInMainWorld('denDesktopSidecar', {
  // Health and capabilities
  getHealth: () => callSidecar('getHealth'),
  getCapabilities: () => callSidecar('getCapabilities'),

  // Operator status and settings
  getOperatorStatus: () => callSidecar('getOperatorStatus'),
  getSettings: () => callSidecar('getSettings'),
  saveOperatorSettings: (request: unknown) => callSidecar('saveOperatorSettings', request),
  getAppearanceSettings: () => callSidecar('getAppearanceSettings'),
  saveAppearanceSettings: (request: unknown) => callSidecar('saveAppearanceSettings', request),
  refreshNow: () => callSidecar('refreshNow'),

  // Snapshot access
  listLocalSnapshots: () => callSidecar('listLocalSnapshots'),
  listLocalSessionSnapshots: () => callSidecar('listLocalSessionSnapshots'),
  getLatestDiffSnapshot: (request: unknown) => callSidecar('getLatestDiffSnapshot', request),

  // Console commands
  consoleListCommands: () => callSidecar('consoleListCommands'),
  consoleRunCommand: (request: unknown) => callSidecar('consoleRunCommand', request),

  // App agent
  appAgentBuildContext: (request?: unknown) => callSidecar('appAgentBuildContext', request ?? {}),
  appAgentListTools: (request?: unknown) => callSidecar('appAgentListTools', request ?? {}),
  appAgentInvokeTool: (request: unknown) => callSidecar('appAgentInvokeTool', request),
  appAgentCancelRequest: (request: unknown) => callSidecar('appAgentCancelRequest', request),

  // Tasks dashboard
  tasksGetDashboardSnapshot: (request: unknown) => callSidecar('tasksGetDashboardSnapshot', request),

  // Terminal commands
  terminalCreateSession: (request: unknown) => callSidecar('terminalCreateSession', request),
  terminalListSessions: (request?: unknown) => callSidecar('terminalListSessions', request ?? {}),
  terminalReadActivity: (request: unknown) => callSidecar('terminalReadActivity', request),
  terminalAttach: (request: unknown) => callSidecar('terminalAttach', request),
  terminalDetach: (request: unknown) => callSidecar('terminalDetach', request),
  terminalSendInput: (request: unknown) => callSidecar('terminalSendInput', request),
  terminalResize: (request: unknown) => callSidecar('terminalResize', request),
  terminalTerminate: (request: unknown) => callSidecar('terminalTerminate', request),
  terminalReconnect: (request: unknown) => callSidecar('terminalReconnect', request),
  terminalAckOutput: (request: unknown) => callSidecar('terminalAckOutput', request),

  // Event subscriptions
  onTerminalOutput: (listener: (event: unknown) => void) => subscribeToEvent('terminalOutput', listener),
  onTerminalStatus: (listener: (event: unknown) => void) => subscribeToEvent('terminalStatus', listener),
  onTerminalLifecycle: (listener: (event: unknown) => void) => subscribeToEvent('terminalLifecycle', listener),
  onTerminalBackpressure: (listener: (event: unknown) => void) => subscribeToEvent('terminalBackpressure', listener),
  onTerminalSessionList: (listener: (event: unknown) => void) => subscribeToEvent('terminalSessionList', listener),
  onAppAgentRunState: (listener: (event: unknown) => void) => subscribeToEvent('appAgentRunState', listener),
  onAppAgentToolCallState: (listener: (event: unknown) => void) => subscribeToEvent('appAgentToolCallState', listener),
  onOperatorStatus: (listener: (event: unknown) => void) => subscribeToEvent('operatorStatus', listener),
  onGitSnapshots: (listener: (event: unknown) => void) => subscribeToEvent('gitSnapshots', listener),
  onSessionSnapshots: (listener: (event: unknown) => void) => subscribeToEvent('sessionSnapshots', listener),
});
