import { GitFileStatus, LocalGitSnapshot } from '../desktop/tauriApi';
import { calmStateLabel, freshnessLabel, groupChangedFiles, snapshotKey, snapshotTitle } from '../snapshotView';

interface Props {
  snapshots: LocalGitSnapshot[];
  activeSnapshotKey: string | null;
  selectedFilePath: string | null;
  onSelectSnapshot: (snapshot: LocalGitSnapshot) => void;
  onSelectFile: (snapshot: LocalGitSnapshot, file: GitFileStatus) => void;
}

export function GitSnapshotPane({ snapshots, activeSnapshotKey, selectedFilePath, onSelectSnapshot, onSelectFile }: Props) {
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
          {sorted.map((snapshot) => {
            const key = snapshotKey(snapshot);
            return (
              <SnapshotCard
                key={key}
                snapshot={snapshot}
                active={key === activeSnapshotKey}
                selectedFilePath={key === activeSnapshotKey ? selectedFilePath : null}
                onSelectSnapshot={onSelectSnapshot}
                onSelectFile={onSelectFile}
              />
            );
          })}
        </div>
      )}
    </section>
  );
}

function SnapshotCard({
  snapshot,
  active,
  selectedFilePath,
  onSelectSnapshot,
  onSelectFile,
}: {
  snapshot: LocalGitSnapshot;
  active: boolean;
  selectedFilePath: string | null;
  onSelectSnapshot: (snapshot: LocalGitSnapshot) => void;
  onSelectFile: (snapshot: LocalGitSnapshot, file: GitFileStatus) => void;
}) {
  const request = snapshot.request;
  const dirty = request.dirty_counts;
  const groups = groupChangedFiles(request.changed_files);

  return (
    <article className={`snapshot-card ${active ? 'active' : ''}`} onFocus={() => onSelectSnapshot(snapshot)}>
      <div className="snapshot-topline">
        <div>
          <h3>{snapshotTitle(snapshot)}</h3>
          <p className="path-line">{request.root_path}</p>
        </div>
        <div className="pill-stack">
          <span className={`status-pill status-${request.state}`}>{calmStateLabel(request.state)}</span>
          <span className={`publish-pill publish-${snapshot.lastPublishStatus}`}>{snapshot.lastPublishStatus}</span>
        </div>
      </div>

      <div className="snapshot-meta">
        <span>source <strong>{request.source_display_name ?? request.source_instance_id}</strong></span>
        <span>freshness <strong>{freshnessLabel(snapshot)}</strong></span>
        <span>branch <strong>{request.branch ?? (request.is_detached ? 'detached' : '—')}</strong></span>
        <span>head <strong>{shortSha(request.head_sha)}</strong></span>
        <span>upstream <strong>{request.upstream ?? '—'}</strong></span>
        <span>ahead/behind <strong>{formatAheadBehind(request.ahead, request.behind)}</strong></span>
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

      {groups.length > 0 && (
        <div className="file-groups">
          {groups.map((group) => (
            <div className="file-group" key={group.category}>
              <h4>{group.category} · {group.files.length}</h4>
              <ul>
                {group.files.slice(0, 80).map((file) => (
                  <li key={`${file.path}:${file.index_status}:${file.worktree_status}`}>
                    <button
                      type="button"
                      className={`file-button ${selectedFilePath === file.path ? 'active' : ''}`}
                      onClick={() => onSelectFile(snapshot, file)}
                    >
                      <code>{file.index_status ?? '.'}{file.worktree_status ?? '.'}</code>
                      <span>{file.path}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
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

function shortSha(value: string | null) {
  return value ? value.slice(0, 10) : '—';
}

function formatAheadBehind(ahead: number | null, behind: number | null) {
  if (ahead == null && behind == null) return '—';
  return `+${ahead ?? 0}/-${behind ?? 0}`;
}
