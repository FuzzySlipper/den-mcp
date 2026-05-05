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
 *
 * The subscribe IPC call returns a `subscriptionId` that must be used
 * to unsubscribe. We capture it from the response so that the
 * unsubscribe IPC call sends the correct ID to the main process.
 * If the subscription request fails, the unsubscribe is a no-op.
 */
function subscribeToEvent(eventName: string, listener: (payload: unknown) => void): () => void {
  const channel = `den-desktop:event:${eventName}`;

  // Register the IPC listener first
  const wrappedListener = (_event: Electron.IpcRendererEvent, payload: unknown) => {
    listener(payload);
  };
  ipcRenderer.on(channel, wrappedListener);

  // Then request subscription from main process and capture the subscriptionId.
  const subscriptionIdPromise = ipcRenderer.invoke(SIDECAR_SUBSCRIBE_CHANNEL, eventName)
    .then((result: { subscriptionId: string }) => result.subscriptionId)
    .catch(() => null);

  // Return unsubscribe function
  return () => {
    ipcRenderer.removeListener(channel, wrappedListener);
    // Use the captured subscriptionId (not the eventName) to unsubscribe.
    subscriptionIdPromise.then((subscriptionId) => {
      if (subscriptionId) {
        ipcRenderer.invoke(SIDECAR_UNSUBSCRIBE_CHANNEL, subscriptionId).catch(() => {
          // Cleanup is best-effort
        });
      }
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
  /**
   * Run a console command with per-request progress frame delivery via IPC.
   * The preload sets up an IPC listener for progress frames on a unique channel
   * before invoking the command. The main process forwards progress frames from
   * the bridge transport to that channel until the final response resolves.
   * The renderer passes an `onProgress` callback that receives structured
   * console command lines only; raw bridge progress frames stay behind the
   * preload boundary.
   */
  consoleRunCommandWithProgress: (request: unknown, onProgress: (line: unknown) => void) => {
    const progressChannel = `den-desktop:progress:${Date.now().toString(36)}_${Math.random().toString(36).slice(2)}`;

    // Set up IPC listener for progress frames from the main process.
    // This listener is registered before the invoke so the main process
    // can start forwarding progress frames immediately.
    const progressListener = (_event: Electron.IpcRendererEvent, frame: unknown) => {
      const progressFrame = frame as { payload?: { lines?: unknown[] } };
      for (const line of progressFrame.payload?.lines ?? []) {
        onProgress(line);
      }
    };
    ipcRenderer.on(progressChannel, progressListener);

    return ipcRenderer.invoke('den-desktop:console-run-command-with-progress', request, progressChannel)
      .finally(() => {
        ipcRenderer.removeListener(progressChannel, progressListener);
      });
  },

  // App agent
  appAgentBuildContext: (request?: unknown) => callSidecar('appAgentBuildContext', request ?? {}),
  appAgentListTools: (request?: unknown) => callSidecar('appAgentListTools', request ?? {}),
  appAgentInvokeTool: (request: unknown) => callSidecar('appAgentInvokeTool', request),
  appAgentCancelRequest: (request: unknown) => callSidecar('appAgentCancelRequest', request),

  // Collaboration live-delivery bridge (task #1074)
  collaborationSendCompiledResponse: (request: unknown) => callSidecar('collaborationSendCompiledResponse', request),

  // Tasks dashboard
  tasksGetDashboardSnapshot: (request: unknown) => callSidecar('tasksGetDashboardSnapshot', request),

  // Messages tab
  messagesGetSnapshot: (request: unknown) => callSidecar('messagesGetSnapshot', request),

  // Documents tab (task #1147)
  documentsList: (request: unknown) => callSidecar('documentsList', request),
  documentGet: (request: unknown) => callSidecar('documentGet', request),
  documentStore: (request: unknown) => callSidecar('documentStore', request),

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
  onCollaborationDelivery: (listener: (event: unknown) => void) => subscribeToEvent('collaborationDelivery', listener),
  onOperatorStatus: (listener: (event: unknown) => void) => subscribeToEvent('operatorStatus', listener),
  onGitSnapshots: (listener: (event: unknown) => void) => subscribeToEvent('gitSnapshots', listener),
  onSessionSnapshots: (listener: (event: unknown) => void) => subscribeToEvent('sessionSnapshots', listener),

  // Hotkey support
  // registerHotkeys is a compatibility no-op: hotkeys are handled window-local
  // in the renderer. The IPC handler in main.ts is intentionally empty (task #1166).
  registerHotkeys: (actions: Record<string, string>) => ipcRenderer.invoke('den-desktop:hotkeys-register', actions),
  // onHotkeyAction remains wired for app-command dispatches (e.g. Browser_Back / goBack).
  onHotkeyAction: (listener: (action: string) => void) => {
    const handler = (_event: Electron.IpcRendererEvent, action: string) => listener(action);
    ipcRenderer.on('den-desktop:hotkey-action', handler);
    return () => {
      ipcRenderer.removeListener('den-desktop:hotkey-action', handler);
    };
  },
});
