export type IpcHealthState = 'unknown' | 'ok' | 'degraded';

export interface IpcHealth {
  state: IpcHealthState;
  message: string | null;
  consecutiveFailures: number;
  pendingInvokes: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastHeartbeatAt: string | null;
  lastEventAt: string | null;
  listenerFailures: string[];
}

export const STALE_IPC_AFTER_MS = 45_000;

export function initialIpcHealth(): IpcHealth {
  return {
    state: 'unknown',
    message: 'Waiting for desktop IPC heartbeat.',
    consecutiveFailures: 0,
    pendingInvokes: 0,
    lastSuccessAt: null,
    lastFailureAt: null,
    lastHeartbeatAt: null,
    lastEventAt: null,
    listenerFailures: [],
  };
}

export function ipcHealthSummary(health: IpcHealth, nowMs = Date.now()): string {
  if (health.listenerFailures.length > 0) {
    return `Desktop event listener degraded: ${health.listenerFailures[health.listenerFailures.length - 1]}`;
  }

  if (health.lastSuccessAt) {
    const ageMs = Math.max(0, nowMs - Date.parse(health.lastSuccessAt));
    if (ageMs > STALE_IPC_AFTER_MS) {
      return `No successful desktop IPC response for ${formatDuration(ageMs)}; showing last known local state.`;
    }
  }

  if (health.pendingInvokes > 0) {
    return `${health.pendingInvokes} desktop IPC request${health.pendingInvokes === 1 ? '' : 's'} in flight.`;
  }

  return health.message ?? 'Desktop IPC is waiting for a heartbeat.';
}

export function ipcHealthState(health: IpcHealth, nowMs = Date.now()): IpcHealthState {
  if (health.consecutiveFailures > 0 || health.listenerFailures.length > 0) {
    return 'degraded';
  }

  if (health.lastSuccessAt && nowMs - Date.parse(health.lastSuccessAt) > STALE_IPC_AFTER_MS) {
    return 'degraded';
  }

  return health.state;
}

export function formatDuration(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000));
  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;
  if (remainder === 0) {
    return `${minutes}m`;
  }

  return `${minutes}m ${remainder}s`;
}
