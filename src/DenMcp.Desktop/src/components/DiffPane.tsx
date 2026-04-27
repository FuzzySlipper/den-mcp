import { DesktopDiffSnapshotLatestResult, GitFileStatus, LocalGitSnapshot } from '../desktop/tauriApi';
import { diffStatusMessage } from '../snapshotView';

interface Props {
  snapshot: LocalGitSnapshot | null;
  file: GitFileStatus | null;
  diff: DesktopDiffSnapshotLatestResult | null;
  loading: boolean;
  error: string | null;
}

export function DiffPane({ snapshot, file, diff, loading, error }: Props) {
  const message = diffStatusMessage(diff, file?.path ?? null);

  return (
    <section className="panel diff-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Bounded diff</p>
          <h2>{file?.path ?? 'No file selected'}</h2>
        </div>
        {diff && <span className={`status-pill status-${diff.state}`}>{diff.freshness_status}</span>}
      </div>

      {!snapshot && <p className="muted">Select a task/workspace snapshot to inspect changed files.</p>}
      {snapshot && !file && <p className="muted">Select a changed file from the snapshot to request the latest bounded diff published by this source.</p>}
      {loading && <p className="muted">Checking Den for a bounded diff snapshot…</p>}
      {error && <p className="error-note">{error}</p>}
      {!loading && !error && message && <p className="empty-state">{message}</p>}

      {diff?.snapshot?.diff && (
        <div className="diff-content">
          <div className="snapshot-meta">
            <span>max <strong>{diff.snapshot.max_bytes} bytes</strong></span>
            <span>{diff.snapshot.binary ? 'binary' : 'text'}</span>
            <span>{diff.snapshot.truncated ? 'truncated' : 'complete'}</span>
            <span>observed <strong>{new Date(diff.snapshot.observed_at).toLocaleString()}</strong></span>
          </div>
          {diff.snapshot.warnings.length > 0 && (
            <ul className="warning-list">
              {diff.snapshot.warnings.map((warning) => <li key={warning}>{warning}</li>)}
            </ul>
          )}
          <pre>{diff.snapshot.diff}</pre>
        </div>
      )}
    </section>
  );
}
