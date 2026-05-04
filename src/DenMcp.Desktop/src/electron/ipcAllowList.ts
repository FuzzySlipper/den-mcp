export const allowedSidecarCallMethods = Object.freeze([
  'appAgentBuildContext',
  'appAgentCancelRequest',
  'appAgentInvokeTool',
  'appAgentListTools',
  'collaborationSendCompiledResponse',
  'consoleListCommands',
  'consoleRunCommand',
  'getAppearanceSettings',
  'getCapabilities',
  'getHealth',
  'getLatestDiffSnapshot',
  'getOperatorStatus',
  'getSettings',
  'listLocalSessionSnapshots',
  'listLocalSnapshots',
  'refreshNow',
  'saveAppearanceSettings',
  'saveOperatorSettings',
  'tasksGetDashboardSnapshot',
  'taskUpdate',
  'messagesGetSnapshot',
  'documentsList',
  'documentGet',
  'documentStore',
  'terminalAckOutput',
  'terminalAttach',
  'terminalCreateSession',
  'terminalDetach',
  'terminalListSessions',
  'terminalReadActivity',
  'terminalReconnect',
  'terminalResize',
  'terminalSendInput',
  'terminalTerminate',
] as const);

export const allowedSidecarSubscriptionEvents = Object.freeze([
  'appAgentRunState',
  'appAgentToolCallState',
  'collaborationDelivery',
  'gitSnapshots',
  'operatorStatus',
  'sessionSnapshots',
  'terminalBackpressure',
  'terminalLifecycle',
  'terminalOutput',
  'terminalSessionList',
  'terminalStatus',
] as const);

const sidecarCallMethodSet = new Set<string>(allowedSidecarCallMethods);
const sidecarSubscriptionEventSet = new Set<string>(allowedSidecarSubscriptionEvents);

export type AllowedSidecarCallMethod = (typeof allowedSidecarCallMethods)[number];
export type AllowedSidecarSubscriptionEvent = (typeof allowedSidecarSubscriptionEvents)[number];

export function assertAllowedSidecarCallMethod(method: unknown): AllowedSidecarCallMethod {
  if (typeof method !== 'string' || !sidecarCallMethodSet.has(method)) {
    throw new Error(`Unknown sidecar method '${String(method)}'.`);
  }

  return method as AllowedSidecarCallMethod;
}

export function assertAllowedSidecarSubscriptionEvent(eventName: unknown): AllowedSidecarSubscriptionEvent {
  if (typeof eventName !== 'string' || !sidecarSubscriptionEventSet.has(eventName)) {
    throw new Error(`Unknown sidecar event subscription '${String(eventName)}'.`);
  }

  return eventName as AllowedSidecarSubscriptionEvent;
}
