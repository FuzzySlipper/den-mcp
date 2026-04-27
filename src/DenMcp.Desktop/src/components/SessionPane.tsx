import { LocalSessionSnapshot } from '../desktop/tauriApi';
import { capabilitySummary, isSessionIdle, phaseLabel, recentActivityItems, sessionKey, sessionTitle } from '../sessionView';

interface Props {
  snapshots: LocalSessionSnapshot[];
}

export function SessionPane({ snapshots }: Props) {
  const sorted = [...snapshots].sort((a, b) => b.request.observed_at.localeCompare(a.request.observed_at));

  return (
    <section className="panel session-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Terminal/session spike</p>
          <h2>Pi session snapshots</h2>
        </div>
        <span className="count-pill">{snapshots.length}</span>
      </div>
      <p className="muted">
        Prototype observer mode: reads local Pi/sub-agent run artifacts, publishes structured session snapshots to Den, and keeps raw terminal streaming and controls disabled.
      </p>
      {sorted.length === 0 ? (
        <div className="empty-state">
          <strong>No Pi session artifacts observed yet.</strong>
          <p>Run records under the local Pi artifact directory will appear here when their project root matches synced Den projects.</p>
        </div>
      ) : (
        <div className="session-card-list">
          {sorted.slice(0, 12).map((snapshot) => <SessionCard key={sessionKey(snapshot)} snapshot={snapshot} />)}
        </div>
      )}
    </section>
  );
}

function SessionCard({ snapshot }: { snapshot: LocalSessionSnapshot }) {
  const activity = recentActivityItems(snapshot);
  const idle = isSessionIdle(snapshot);
  return (
    <article className={`session-card ${idle ? 'session-idle' : ''}`}>
      <div className="snapshot-topline">
        <div>
          <h3>{sessionTitle(snapshot)}</h3>
          <p className="path-line">{snapshot.artifactRoot ?? 'artifact root unknown'}</p>
        </div>
        <div className="pill-stack">
          <span className={`status-pill status-${snapshot.request.current_phase ?? 'observed'}`}>{phaseLabel(snapshot.request.current_phase)}</span>
          <span className={`publish-pill publish-${snapshot.lastPublishStatus}`}>{snapshot.lastPublishStatus}</span>
        </div>
      </div>

      <div className="snapshot-meta">
        <span>project <strong>{snapshot.projectId}</strong></span>
        <span>workspace <strong>{snapshot.request.workspace_id ?? '—'}</strong></span>
        <span>agent <strong>{snapshot.request.agent_identity ?? '—'}</strong></span>
        <span>command <strong>{snapshot.request.current_command ?? '—'}</strong></span>
        <span>capabilities <strong>{capabilitySummary(snapshot)}</strong></span>
        <span>observed <strong>{new Date(snapshot.request.observed_at).toLocaleTimeString()}</strong></span>
      </div>

      {snapshot.request.warnings.length > 0 && (
        <ul className="warning-list">
          {snapshot.request.warnings.map((warning, index) => <li key={`${warning}:${index}`}>{warning}</li>)}
        </ul>
      )}
      {snapshot.lastPublishError && <p className="error-note">{snapshot.lastPublishError}</p>}

      {activity.length > 0 && (
        <ol className="activity-list">
          {activity.map((item, index) => (
            <li key={`${item.timestamp ?? index}:${item.summary ?? item.tool ?? item.kind}`}>
              <span>{item.kind ?? item.role ?? 'activity'}</span>
              <p>{item.tool ? `${item.tool}: ` : ''}{item.summary ?? 'activity observed'}</p>
            </li>
          ))}
        </ol>
      )}
    </article>
  );
}
