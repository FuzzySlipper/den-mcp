import { LocalGitSnapshot } from '../desktop/tauriApi';

interface Props {
  snapshots: LocalGitSnapshot[];
}

export function GitSnapshotPane({ snapshots }: Props) {
  const sorted = [...snapshots].sort((a, b) => a.scope.projectId.localeCompare(b.scope.projectId));

  return (
    <section className="panel git-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Local observer</p>
          <h2>Git/worktree snapshots</h2>
        </div>
        <span className="count-pill">{snapshots.length}</span>
      </div>

      {sorted.length === 0 ? (
        <div className="empty-state">
          <strong>No local snapshots yet.</strong>
          <p>When Den projects or agent workspaces sync, the desktop app scans locally visible paths and publishes snapshots back to Den.</p>
        </div>
      ) : (
        <div className="snapshot-list">
          {sorted.map((snapshot) => (
            <SnapshotCard key={`${snapshot.scope.projectId}:${snapshot.scope.workspaceId ?? 'root'}:${snapshot.scope.rootPath}`} snapshot={snapshot} />
          ))}
        </div>
      )}
    </section>
  );
}

function SnapshotCard({ snapshot }: { snapshot: LocalGitSnapshot }) {
  const request = snapshot.request;
  const dirty = request.dirty_counts;
  const title = snapshot.scope.workspaceId
    ? `${snapshot.scope.projectId} · workspace ${snapshot.scope.workspaceId}`
    : `${snapshot.scope.projectId} · project root`;

  return (
    <article className="snapshot-card">
      <div className="snapshot-topline">
        <div>
          <h3>{title}</h3>
          <p className="path-line">{request.root_path}</p>
        </div>
        <div className="pill-stack">
          <span className={`status-pill status-${request.state}`}>{labelState(request.state)}</span>
          <span className={`publish-pill publish-${snapshot.lastPublishStatus}`}>{snapshot.lastPublishStatus}</span>
        </div>
      </div>

      <div className="snapshot-meta">
        <span>branch <strong>{request.branch ?? (request.is_detached ? 'detached' : '—')}</strong></span>
        <span>head <strong>{shortSha(request.head_sha)}</strong></span>
        <span>upstream <strong>{request.upstream ?? '—'}</strong></span>
        <span>ahead/behind <strong>{formatAheadBehind(request.ahead, request.behind)}</strong></span>
        <span>observed <strong>{formatDate(request.observed_at)}</strong></span>
      </div>

      <div className="dirty-grid">
        <Dirty label="total" value={dirty.total} />
        <Dirty label="staged" value={dirty.staged} />
        <Dirty label="unstaged" value={dirty.unstaged} />
        <Dirty label="untracked" value={dirty.untracked} />
        <Dirty label="modified" value={dirty.modified} />
        <Dirty label="added" value={dirty.added} />
        <Dirty label="deleted" value={dirty.deleted} />
        <Dirty label="renamed" value={dirty.renamed} />
      </div>

      {request.warnings.length > 0 && (
        <ul className="warning-list">
          {request.warnings.map((warning, index) => (
            <li key={`${warning}:${index}`}>{warning}</li>
          ))}
        </ul>
      )}

      {snapshot.lastPublishError && <p className="error-note">{snapshot.lastPublishError}</p>}

      {request.changed_files.length > 0 && (
        <details className="file-details">
          <summary>{request.changed_files.length}{request.truncated ? '+' : ''} changed files</summary>
          <ul>
            {request.changed_files.slice(0, 80).map((file) => (
              <li key={`${file.path}:${file.index_status}:${file.worktree_status}`}>
                <code>{file.category}</code> {file.path}
              </li>
            ))}
          </ul>
        </details>
      )}
    </article>
  );
}

function Dirty({ label, value }: { label: string; value: number }) {
  return (
    <div className="dirty-count">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function labelState(state: string) {
  return state.replaceAll('_', ' ');
}

function shortSha(value: string | null) {
  return value ? value.slice(0, 10) : '—';
}

function formatAheadBehind(ahead: number | null, behind: number | null) {
  if (ahead == null && behind == null) return '—';
  return `+${ahead ?? 0}/-${behind ?? 0}`;
}

function formatDate(value: string | null | undefined) {
  if (!value) return '—';
  return new Date(value).toLocaleTimeString();
}
