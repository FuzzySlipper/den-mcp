import { FormEvent, useEffect, useState } from 'react';
import { OperatorSettings, OperatorStatus, SaveOperatorSettingsRequest } from '../desktop/sidecarBridgeApi';

interface Props {
  status: OperatorStatus | null;
  settings: OperatorSettings | null;
  onRefresh: () => Promise<void>;
  onSaveSettings: (request: SaveOperatorSettingsRequest) => Promise<void>;
  showSettingsForm?: boolean;
}

export function ConnectionPanel({ status, settings, onRefresh, onSaveSettings, showSettingsForm = true }: Props) {
  const [denBaseUrl, setDenBaseUrl] = useState('http://localhost:5199');
  const [displayName, setDisplayName] = useState('');
  const [pollInterval, setPollInterval] = useState(30);
  const [includeHiddenSpaces, setIncludeHiddenSpaces] = useState(true);
  const [includeArchivedSpaces, setIncludeArchivedSpaces] = useState(true);
  const [saving, setSaving] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  useEffect(() => {
    if (!settings) return;
    setDenBaseUrl(settings.denBaseUrl);
    setDisplayName(settings.sourceDisplayName ?? '');
    setPollInterval(settings.pollIntervalSeconds);
    setIncludeHiddenSpaces(settings.includeHiddenSpaces);
    setIncludeArchivedSpaces(settings.includeArchivedSpaces);
  }, [settings]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setSaving(true);
    try {
      await onSaveSettings({
        denBaseUrl,
        sourceDisplayName: displayName || null,
        pollIntervalSeconds: pollInterval,
        includeHiddenSpaces,
        includeArchivedSpaces,
      });
    } finally {
      setSaving(false);
    }
  };

  const refresh = async () => {
    setRefreshing(true);
    try {
      await onRefresh();
    } finally {
      setRefreshing(false);
    }
  };

  const connection = status?.denConnection;

  return (
    <section className="panel connection-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Local operator loop</p>
          <h2>Den connection</h2>
        </div>
        <span className={`status-pill status-${connection?.state ?? 'unknown'}`}>{connection?.state ?? 'unknown'}</span>
      </div>

      <p className="muted">{connection?.message ?? 'Waiting for the desktop runtime to report connection state.'}</p>

      <div className="metric-grid">
        <Metric label="Projects" value={status?.projectCount ?? '—'} />
        <Metric label="Workspaces" value={status?.workspaceCount ?? '—'} />
        <Metric label="Local snapshots" value={status?.localSnapshotCount ?? '—'} />
        <Metric label="Last publish" value={formatRelative(status?.lastPublishAt)} />
      </div>

      {showSettingsForm ? (
        <form className="settings-form" onSubmit={submit}>
          <label>
            <span>Den server URL</span>
            <input value={denBaseUrl} onChange={(event) => setDenBaseUrl(event.target.value)} />
          </label>
          <label>
            <span>Source display name</span>
            <input value={displayName} onChange={(event) => setDisplayName(event.target.value)} placeholder="Den Desktop" />
          </label>
          <label>
            <span>Poll interval seconds</span>
            <input
              type="number"
              min={5}
              max={3600}
              value={pollInterval}
              onChange={(event) => setPollInterval(Number(event.target.value))}
            />
          </label>
          <label className="checkbox-label">
            <input
              type="checkbox"
              checked={includeHiddenSpaces}
              onChange={(event) => setIncludeHiddenSpaces(event.target.checked)}
            />
            <span>Include hidden spaces in switchers</span>
          </label>
          <label className="checkbox-label">
            <input
              type="checkbox"
              checked={includeArchivedSpaces}
              onChange={(event) => setIncludeArchivedSpaces(event.target.checked)}
            />
            <span>Include archived spaces in switchers</span>
          </label>
          <p className="form-hint">Space visibility inclusion is an explicit operator policy; returned hidden/archived spaces are labeled in the switcher.</p>
          <div className="button-row">
            <button type="submit" disabled={saving}>{saving ? 'Saving…' : 'Save settings'}</button>
            <button type="button" className="secondary" disabled={refreshing} onClick={refresh}>
              {refreshing ? 'Refreshing…' : 'Refresh now'}
            </button>
          </div>
        </form>
      ) : (
        <div className="button-row connection-actions">
          <button type="button" className="secondary" disabled={refreshing} onClick={refresh}>
            {refreshing ? 'Refreshing…' : 'Refresh now'}
          </button>
        </div>
      )}

      <dl className="compact-list">
        <div>
          <dt>Source instance</dt>
          <dd>{status?.sourceInstanceId ?? settings?.sourceInstanceId ?? '—'}</dd>
        </div>
        <div>
          <dt>Last sync</dt>
          <dd>{formatDate(status?.lastSyncAt)}</dd>
        </div>
        <div>
          <dt>Next retry/run</dt>
          <dd>{formatDate(connection?.nextRetryAt ?? status?.observerStatuses[0]?.nextRunAt)}</dd>
        </div>
      </dl>
    </section>
  );
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="metric-card">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function formatDate(value: string | null | undefined) {
  if (!value) return '—';
  return new Date(value).toLocaleString();
}

function formatRelative(value: string | null | undefined) {
  if (!value) return '—';
  const seconds = Math.round((Date.now() - new Date(value).getTime()) / 1000);
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  return formatDate(value);
}
