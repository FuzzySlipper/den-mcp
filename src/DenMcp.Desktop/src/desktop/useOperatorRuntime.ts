import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  getOperatorStatus,
  getSettings,
  listLocalSnapshots,
  LocalGitSnapshot,
  onGitSnapshots,
  onOperatorStatus,
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
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  saveSettings: (request: SaveOperatorSettingsRequest) => Promise<void>;
}

export function useOperatorRuntime(): RuntimeState {
  const [status, setStatus] = useState<OperatorStatus | null>(null);
  const [settings, setSettings] = useState<OperatorSettings | null>(null);
  const [snapshots, setSnapshots] = useState<LocalGitSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [nextStatus, nextSettings, snapshotList] = await Promise.all([
        getOperatorStatus(),
        getSettings(),
        listLocalSnapshots(),
      ]);
      setStatus(nextStatus);
      setSettings(nextSettings);
      setSnapshots(snapshotList.snapshots);
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

    void onOperatorStatus((next) => setStatus(next)).then((dispose) => {
      disposeStatus = dispose;
    });
    void onGitSnapshots((next) => setSnapshots(next)).then((dispose) => {
      disposeSnapshots = dispose;
    });

    return () => {
      disposeStatus?.();
      disposeSnapshots?.();
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
    () => ({ status, settings, snapshots, loading, error, refresh, saveSettings }),
    [status, settings, snapshots, loading, error, refresh, saveSettings],
  );
}
