import { LocalGitSnapshot } from '../desktop/tauriApi';
import { calmStateLabel, freshnessLabel, snapshotKey, snapshotTitle } from '../snapshotView';

interface Props {
  snapshots: LocalGitSnapshot[];
  activeKey: string | null;
  onSelect: (snapshot: LocalGitSnapshot) => void;
}

export function WorkspaceSummaryPane({ snapshots, activeKey, onSelect }: Props) {
  const sorted = [...snapshots].sort((a, b) => snapshotTitle(a).localeCompare(snapshotTitle(b)));

  return (
    <section className="panel workspace-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Operator focus</p>
          <h2>Task/workspace cards</h2>
        </div>
        <span className="count-pill">{snapshots.length}</span>
      </div>
      {sorted.length === 0 ? (
        <p className="muted">No synced project or workspace snapshots yet.</p>
      ) : (
        <div className="workspace-card-list">
          {sorted.map((snapshot) => {
            const key = snapshotKey(snapshot);
            return (
              <button
                type="button"
                className={`workspace-card ${key === activeKey ? 'active' : ''}`}
                key={key}
                onClick={() => onSelect(snapshot)}
              >
                <span className="workspace-title">{snapshotTitle(snapshot)}</span>
                <span className="workspace-meta">
                  {snapshot.scope.taskId ? `task #${snapshot.scope.taskId}` : 'project'} · {snapshot.request.branch ?? 'no branch'}
                </span>
                <span className="workspace-meta">
                  {snapshot.request.dirty_counts.total} dirty · {freshnessLabel(snapshot)} · {calmStateLabel(snapshot.request.state)}
                </span>
              </button>
            );
          })}
        </div>
      )}
    </section>
  );
}
