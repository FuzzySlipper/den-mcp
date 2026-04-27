import { DiagnosticEntry, ObserverStatus } from '../desktop/tauriApi';

interface Props {
  diagnostics: DiagnosticEntry[];
  observers: ObserverStatus[];
  error: string | null;
}

export function DiagnosticsPane({ diagnostics, observers, error }: Props) {
  const latest = diagnostics.slice(-20).reverse();

  return (
    <section className="panel diagnostics-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Runtime</p>
          <h2>Observers & diagnostics</h2>
        </div>
      </div>

      {error && <p className="error-note">{error}</p>}

      <div className="observer-list">
        {observers.length === 0 ? (
          <p className="muted">No observers registered yet.</p>
        ) : (
          observers.map((observer) => (
            <div className="observer-card" key={observer.kind}>
              <span className={`status-dot status-${observer.state}`} />
              <strong>{observer.kind}</strong>
              <span>{observer.state}</span>
              <span>{observer.scopesScanned} scopes</span>
              <span>{observer.warningCount} warnings</span>
            </div>
          ))
        )}
      </div>

      {latest.length === 0 ? (
        <p className="muted">No diagnostics recorded.</p>
      ) : (
        <ul className="diagnostic-list">
          {latest.map((entry, index) => (
            <li key={`${entry.observedAt}:${index}`} className={`diagnostic-${entry.level}`}>
              <span>{new Date(entry.observedAt).toLocaleTimeString()}</span>
              <strong>{entry.source}</strong>
              <p>{entry.message}</p>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
