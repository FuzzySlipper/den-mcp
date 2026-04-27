import { useCallback, useEffect, useMemo, useState } from 'react';
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

export interface RuntimeState {
  status: OperatorStatus | null;
  settings: OperatorSettings | null;
  snapshots: LocalGitSnapshot[];
  sessionSnapshots: LocalSessionSnapshot[];
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  saveSettings: (request: SaveOperatorSettingsRequest) => Promise<void>;
}

export function useOperatorRuntime(): RuntimeState {
  const [status, setStatus] = useState<OperatorStatus | null>(null);
  const [settings, setSettings] = useState<OperatorSettings | null>(null);
  const [snapshots, setSnapshots] = useState<LocalGitSnapshot[]>([]);
  const [sessionSnapshots, setSessionSnapshots] = useState<LocalSessionSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [nextStatus, nextSettings, snapshotList, sessionSnapshotList] = await Promise.all([
        getOperatorStatus(),
        getSettings(),
        listLocalSnapshots(),
        listLocalSessionSnapshots(),
      ]);
      setStatus(nextStatus);
      setSettings(nextSettings);
      setSnapshots(snapshotList.snapshots);
      setSessionSnapshots(sessionSnapshotList.snapshots);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
    let disposeStatus: (() => void) | null = null;
    let disposeSnapshots: (() => void) | null = null;
    let disposeSessionSnapshots: (() => void) | null = null;

    void onOperatorStatus((next) => setStatus(next)).then((dispose) => {
      disposeStatus = dispose;
    });
    void onGitSnapshots((next) => setSnapshots(next)).then((dispose) => {
      disposeSnapshots = dispose;
    });
    void onSessionSnapshots((next) => setSessionSnapshots(next)).then((dispose) => {
      disposeSessionSnapshots = dispose;
    });

    return () => {
      disposeStatus?.();
      disposeSnapshots?.();
      disposeSessionSnapshots?.();
    };
  }, [load]);

  const refresh = useCallback(async () => {
    setError(null);
    try {
      await refreshNow();
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [load]);

  const saveSettings = useCallback(
    async (request: SaveOperatorSettingsRequest) => {
      setError(null);
      try {
        const next = await saveOperatorSettings(request);
        setSettings(next);
        await load();
      } catch (err) {
        setError(err instanceof Error ? err.message : String(err));
      }
    },
    [load],
  );

  return useMemo(
    () => ({ status, settings, snapshots, sessionSnapshots, loading, error, refresh, saveSettings }),
    [status, settings, snapshots, sessionSnapshots, loading, error, refresh, saveSettings],
  );
}
