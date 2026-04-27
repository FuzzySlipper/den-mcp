import { ConnectionPanel } from './components/ConnectionPanel';
import { DiagnosticsPane } from './components/DiagnosticsPane';
import { GitSnapshotPane } from './components/GitSnapshotPane';
import { useOperatorRuntime } from './desktop/useOperatorRuntime';
import './styles/index.css';

export function App() {
  const runtime = useOperatorRuntime();

  return (
    <main className="app-shell">
      <header className="hero">
        <div>
          <p className="eyebrow">Den Desktop</p>
          <h1>Operator app</h1>
          <p>
            Local bundled Tauri UI for Den connection health, local git/worktree observation,
            snapshot publication, and future terminal/session controls.
          </p>
        </div>
        <div className="hero-status">
          <span className={`status-pill status-${runtime.status?.denConnection.state ?? 'unknown'}`}>
            {runtime.status?.denConnection.state ?? (runtime.loading ? 'loading' : 'unknown')}
          </span>
          <span>{runtime.status?.phase ?? 'starting'}</span>
        </div>
      </header>

      <div className="content-grid">
        <ConnectionPanel
          status={runtime.status}
          settings={runtime.settings}
          onRefresh={runtime.refresh}
          onSaveSettings={runtime.saveSettings}
        />
        <DiagnosticsPane
          diagnostics={runtime.status?.diagnostics ?? []}
          observers={runtime.status?.observerStatuses ?? []}
          error={runtime.error}
        />
      </div>

      <GitSnapshotPane snapshots={runtime.snapshots} />
    </main>
  );
}
