import { useEffect, useMemo, useState } from 'react';
import { ConnectionPanel } from './components/ConnectionPanel';
import { DiagnosticsPane } from './components/DiagnosticsPane';
import { DiffPane } from './components/DiffPane';
import { GitSnapshotPane } from './components/GitSnapshotPane';
import { SessionPane } from './components/SessionPane';
import { WorkspaceSummaryPane } from './components/WorkspaceSummaryPane';
import { DesktopDiffSnapshotLatestResult, getLatestDiffSnapshot, GitFileStatus, LocalGitSnapshot } from './desktop/tauriApi';
import { useOperatorRuntime } from './desktop/useOperatorRuntime';
import { snapshotKey } from './snapshotView';
import './styles/index.css';

export function App() {
  const runtime = useOperatorRuntime();
  const [activeSnapshotKey, setActiveSnapshotKey] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<GitFileStatus | null>(null);
  const [diff, setDiff] = useState<DesktopDiffSnapshotLatestResult | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);

  const activeSnapshot = useMemo(
    () => runtime.snapshots.find((snapshot) => snapshotKey(snapshot) === activeSnapshotKey) ?? runtime.snapshots[0] ?? null,
    [activeSnapshotKey, runtime.snapshots],
  );

  useEffect(() => {
    if (!activeSnapshot && activeSnapshotKey) {
      setActiveSnapshotKey(null);
      setSelectedFile(null);
      setDiff(null);
    } else if (activeSnapshot && !activeSnapshotKey) {
      setActiveSnapshotKey(snapshotKey(activeSnapshot));
    }
  }, [activeSnapshot, activeSnapshotKey]);

  const selectSnapshot = (snapshot: LocalGitSnapshot) => {
    const key = snapshotKey(snapshot);
    setActiveSnapshotKey(key);
    if (key !== activeSnapshotKey) {
      setSelectedFile(null);
      setDiff(null);
      setDiffError(null);
    }
  };

  const selectFile = async (snapshot: LocalGitSnapshot, file: GitFileStatus) => {
    setActiveSnapshotKey(snapshotKey(snapshot));
    setSelectedFile(file);
    setDiff(null);
    setDiffError(null);
    setDiffLoading(true);
    try {
      const result = await getLatestDiffSnapshot({
        projectId: snapshot.scope.projectId,
        taskId: snapshot.scope.taskId,
        workspaceId: snapshot.scope.workspaceId,
        rootPath: snapshot.request.root_path,
        path: file.path,
        sourceInstanceId: snapshot.request.source_instance_id,
      });
      setDiff(result);
    } catch (err) {
      setDiffError(err instanceof Error ? err.message : String(err));
    } finally {
      setDiffLoading(false);
    }
  };

  return (
    <main className="app-shell">
      <header className="hero">
        <div>
          <p className="eyebrow">Den Desktop</p>
          <h1>Operator app</h1>
          <p>
            Local bundled Tauri UI for Den connection health, local git/worktree observation,
            snapshot publication, bounded diff lookup, and future terminal/session controls.
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

      <div className="operator-grid">
        <WorkspaceSummaryPane snapshots={runtime.snapshots} activeKey={activeSnapshot ? snapshotKey(activeSnapshot) : null} onSelect={selectSnapshot} />
        <DiffPane snapshot={activeSnapshot} file={selectedFile} diff={diff} loading={diffLoading} error={diffError} />
      </div>

      <SessionPane snapshots={runtime.sessionSnapshots} />

      <GitSnapshotPane
        snapshots={runtime.snapshots}
        activeSnapshotKey={activeSnapshot ? snapshotKey(activeSnapshot) : null}
        selectedFilePath={selectedFile?.path ?? null}
        onSelectSnapshot={selectSnapshot}
        onSelectFile={selectFile}
      />
    </main>
  );
}
