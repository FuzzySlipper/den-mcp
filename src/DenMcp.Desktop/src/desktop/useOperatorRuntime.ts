import { useCallback, useEffect, useMemo, useState } from 'react';
import { IpcHealth, initialIpcHealth, ipcHealthState } from './ipcHealth';
import {
  getOperatorStatus,
  getSettings,
  listLocalSessionSnapshots,
  listLocalSnapshots,
  LocalGitSnapshot,
  LocalSessionSnapshot,
  onGitSnapshots,
  onOperatorStatus,
  onSessionSnapshots,
  OperatorSettings,
  OperatorStatus,
  refreshNow,
  saveOperatorSettings,
  SaveOperatorSettingsRequest,
} from './tauriApi';

const HEARTBEAT_INTERVAL_MS = 15_000;
const WATCHDOG_INTERVAL_MS = 5_000;

export interface RuntimeState {
  status: OperatorStatus | null;
  settings: OperatorSettings | null;
  snapshots: LocalGitSnapshot[];
  sessionSnapshots: LocalSessionSnapshot[];
  ipcHealth: IpcHealth;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  saveSettings: (request: SaveOperatorSettingsRequest) => Promise<void>;
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

export function useOperatorRuntime(): RuntimeState {
  const [status, setStatus] = useState<OperatorStatus | null>(null);
  const [settings, setSettings] = useState<OperatorSettings | null>(null);
  const [snapshots, setSnapshots] = useState<LocalGitSnapshot[]>([]);
  const [sessionSnapshots, setSessionSnapshots] = useState<LocalSessionSnapshot[]>([]);
  const [ipcHealth, setIpcHealth] = useState<IpcHealth>(() => initialIpcHealth());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const callIpc = useCallback(async <T,>(label: string, operation: () => Promise<T>, heartbeat = false): Promise<T> => {
    setIpcHealth((prev) => ({ ...prev, pendingInvokes: prev.pendingInvokes + 1 }));
    try {
      const result = await operation();
      const now = new Date().toISOString();
      setIpcHealth((prev) => ({
        ...prev,
        state: 'ok',
        message: `Desktop IPC ${label} responded.`,
        consecutiveFailures: 0,
        pendingInvokes: Math.max(0, prev.pendingInvokes - 1),
        lastSuccessAt: now,
        lastHeartbeatAt: heartbeat ? now : prev.lastHeartbeatAt,
      }));
      return result;
    } catch (err) {
      const now = new Date().toISOString();
      const message = `${label}: ${errorMessage(err)}`;
      setIpcHealth((prev) => ({
        ...prev,
        state: 'degraded',
        message,
        consecutiveFailures: prev.consecutiveFailures + 1,
        pendingInvokes: Math.max(0, prev.pendingInvokes - 1),
        lastFailureAt: now,
        lastHeartbeatAt: heartbeat ? now : prev.lastHeartbeatAt,
      }));
      throw err;
    }
  }, []);

  const recordEvent = useCallback((label: string) => {
    const now = new Date().toISOString();
    setIpcHealth((prev) => ({
      ...prev,
      state: prev.consecutiveFailures > 0 ? prev.state : 'ok',
      message: `Received desktop event ${label}.`,
      lastEventAt: now,
    }));
  }, []);

  const recordListenerFailure = useCallback((label: string, err: unknown) => {
    const now = new Date().toISOString();
    const message = `${label}: ${errorMessage(err)}`;
    setIpcHealth((prev) => ({
      ...prev,
      state: 'degraded',
      message,
      lastFailureAt: now,
      listenerFailures: [...prev.listenerFailures.slice(-4), message],
    }));
  }, []);

  const load = useCallback(async () => {
    const [statusResult, settingsResult, snapshotResult, sessionSnapshotResult] = await Promise.allSettled([
      callIpc('status load', getOperatorStatus),
      callIpc('settings load', getSettings),
      callIpc('git snapshot load', listLocalSnapshots),
      callIpc('session snapshot load', listLocalSessionSnapshots),
    ]);

    const failures: string[] = [];
    if (statusResult.status === 'fulfilled') {
      setStatus(statusResult.value);
    } else {
      failures.push(errorMessage(statusResult.reason));
    }

    if (settingsResult.status === 'fulfilled') {
      setSettings(settingsResult.value);
    } else {
      failures.push(errorMessage(settingsResult.reason));
    }

    if (snapshotResult.status === 'fulfilled') {
      setSnapshots(snapshotResult.value.snapshots);
    } else {
      failures.push(errorMessage(snapshotResult.reason));
    }

    if (sessionSnapshotResult.status === 'fulfilled') {
      setSessionSnapshots(sessionSnapshotResult.value.snapshots);
    } else {
      failures.push(errorMessage(sessionSnapshotResult.reason));
    }

    setError(failures.length > 0 ? `Some desktop IPC calls failed; showing last known local state. ${failures.join(' ')}` : null);
    setLoading(false);
  }, [callIpc]);

  useEffect(() => {
    void load();
    let disposed = false;
    let disposeStatus: (() => void) | null = null;
    let disposeSnapshots: (() => void) | null = null;
    let disposeSessionSnapshots: (() => void) | null = null;

    void onOperatorStatus((next) => {
      setStatus(next);
      recordEvent('operator status');
    }).then((dispose) => {
      if (disposed) {
        dispose();
      } else {
        disposeStatus = dispose;
      }
    }).catch((err) => recordListenerFailure('operator status listener', err));

    void onGitSnapshots((next) => {
      setSnapshots(next);
      recordEvent('git snapshots');
    }).then((dispose) => {
      if (disposed) {
        dispose();
      } else {
        disposeSnapshots = dispose;
      }
    }).catch((err) => recordListenerFailure('git snapshot listener', err));

    void onSessionSnapshots((next) => {
      setSessionSnapshots(next);
      recordEvent('session snapshots');
    }).then((dispose) => {
      if (disposed) {
        dispose();
      } else {
        disposeSessionSnapshots = dispose;
      }
    }).catch((err) => recordListenerFailure('session snapshot listener', err));

    return () => {
      disposed = true;
      disposeStatus?.();
      disposeSnapshots?.();
      disposeSessionSnapshots?.();
    };
  }, [load, recordEvent, recordListenerFailure]);

  useEffect(() => {
    let disposed = false;
    const heartbeat = async () => {
      try {
        const next = await callIpc('status heartbeat', getOperatorStatus, true);
        if (!disposed) {
          setStatus(next);
        }
      } catch {
        if (!disposed) {
          setError('Desktop IPC heartbeat is degraded; showing last known local state.');
        }
      }
    };

    const intervalId = window.setInterval(() => void heartbeat(), HEARTBEAT_INTERVAL_MS);
    return () => {
      disposed = true;
      window.clearInterval(intervalId);
    };
  }, [callIpc]);

  useEffect(() => {
    const intervalId = window.setInterval(() => {
      setIpcHealth((prev) => {
        const nextState = ipcHealthState(prev);
        return nextState === prev.state ? prev : { ...prev, state: nextState };
      });
    }, WATCHDOG_INTERVAL_MS);

    return () => window.clearInterval(intervalId);
  }, []);

  const refresh = useCallback(async () => {
    setError(null);
    try {
      await callIpc('manual refresh', refreshNow);
      await load();
    } catch (err) {
      setError(errorMessage(err));
    }
  }, [callIpc, load]);

  const saveSettings = useCallback(
    async (request: SaveOperatorSettingsRequest) => {
      setError(null);
      try {
        const next = await callIpc('settings save', () => saveOperatorSettings(request));
        setSettings(next);
        await load();
      } catch (err) {
        setError(errorMessage(err));
      }
    },
    [callIpc, load],
  );

  return useMemo(
    () => ({ status, settings, snapshots, sessionSnapshots, ipcHealth, loading, error, refresh, saveSettings }),
    [status, settings, snapshots, sessionSnapshots, ipcHealth, loading, error, refresh, saveSettings],
  );
}
