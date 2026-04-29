import { ReactNode, useEffect, useMemo, useState } from 'react';
import { AppShell } from './components/AppShell';
import { ConnectionPanel } from './components/ConnectionPanel';
import { DiagnosticsPane } from './components/DiagnosticsPane';
import { DiffPane } from './components/DiffPane';
import { GitSnapshotPane } from './components/GitSnapshotPane';
import { SessionPane } from './components/SessionPane';
import { WorkspaceSummaryPane } from './components/WorkspaceSummaryPane';
import { DesktopDiffSnapshotLatestResult, getLatestDiffSnapshot, GitFileStatus, LocalGitSnapshot } from './desktop/tauriApi';
import { useOperatorRuntime } from './desktop/useOperatorRuntime';
import { applyShellDataAttributes, loadShellState, nextConsoleMode, saveShellState, ShellState, ShellTabId } from './shellState';
import { snapshotKey } from './snapshotView';
import './styles/index.css';

export function App() {
  const runtime = useOperatorRuntime();
  const [shellState, setShellState] = useState<ShellState>(() => loadShellState(typeof window === 'undefined' ? null : window.localStorage));
  const [activeSnapshotKey, setActiveSnapshotKey] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<GitFileStatus | null>(null);
  const [diff, setDiff] = useState<DesktopDiffSnapshotLatestResult | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);

  useEffect(() => {
    if (typeof document !== 'undefined') {
      applyShellDataAttributes(document.documentElement, shellState);
    }
    if (typeof window !== 'undefined') {
      saveShellState(window.localStorage, shellState);
    }
  }, [shellState]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key === '`') {
        event.preventDefault();
        setShellState((current) => ({ ...current, consoleMode: nextConsoleMode(current.consoleMode) }));
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

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

  const operatorTab = (
    <div className="operator-tab tab-stack">
      <section className="operator-hero panel surface-panel">
        <div>
          <p className="eyebrow">Den Desktop</p>
          <h1>Operator control plane</h1>
          <p className="muted">
            Bridge-safe renderer shell for Den connection health, local git/worktree observation,
            bounded diff lookup, and future terminal/session controls.
          </p>
        </div>
        <div className="hero-status">
          <span className={`status-pill status-${runtime.status?.denConnection.state ?? 'unknown'}`}>
            {runtime.status?.denConnection.state ?? (runtime.loading ? 'loading' : 'unknown')}
          </span>
          <span>{runtime.status?.phase ?? 'starting'}</span>
        </div>
      </section>

      <div className="content-grid">
        <ConnectionPanel
          status={runtime.status}
          settings={runtime.settings}
          onRefresh={runtime.refresh}
          onSaveSettings={runtime.saveSettings}
          showSettingsForm={false}
        />
        <DiagnosticsPane
          diagnostics={runtime.status?.diagnostics ?? []}
          observers={runtime.status?.observerStatuses ?? []}
          ipcHealth={runtime.ipcHealth}
          error={runtime.error}
        />
      </div>

      <WorkspaceSummaryPane snapshots={runtime.snapshots} activeKey={activeSnapshot ? snapshotKey(activeSnapshot) : null} onSelect={selectSnapshot} />
    </div>
  );

  const gitTab = (
    <div className="git-tab tab-stack">
      <section className="tab-intro panel surface-panel">
        <p className="eyebrow">Git</p>
        <h2>Workspace snapshots and bounded diffs</h2>
        <p className="muted">
          Existing local observer snapshots and Den-published bounded diffs, rehomed into the Git tab without changing the runtime bridge flow.
        </p>
      </section>
      <div className="git-workbench-grid">
        <GitSnapshotPane
          snapshots={runtime.snapshots}
          activeSnapshotKey={activeSnapshot ? snapshotKey(activeSnapshot) : null}
          selectedFilePath={selectedFile?.path ?? null}
          onSelectSnapshot={selectSnapshot}
          onSelectFile={selectFile}
        />
        <DiffPane snapshot={activeSnapshot} file={selectedFile} diff={diff} loading={diffLoading} error={diffError} />
      </div>
    </div>
  );

  const terminalsTab = (
    <div className="terminals-tab tab-stack">
      <section className="tab-intro panel surface-panel">
        <p className="eyebrow">Terminals</p>
        <h2>Observed Pi sessions</h2>
        <p className="muted">
          Read-only session cards from existing Pi artifact snapshots. Attach, input, and terminal control remain deferred to later backend-neutral terminal tasks.
        </p>
      </section>
      <SessionPane snapshots={runtime.sessionSnapshots} />
    </div>
  );

  const runtimeSettingsTab = (
    <ConnectionPanel
      status={runtime.status}
      settings={runtime.settings}
      onRefresh={runtime.refresh}
      onSaveSettings={runtime.saveSettings}
    />
  );

  const tabContent: Record<ShellTabId, ReactNode> = {
    operator: operatorTab,
    tasks: <StubSurface eyebrow="Tasks" title="Delegated workflow dashboard" description="Routed surface reserved for normalized Den task packets, coder/reviewer lanes, and worktree execution state once bridge snapshots expose them." />,
    git: gitTab,
    compare: <StubSurface eyebrow="Compare" title="Multi-worktree compare" description="Routed surface reserved for pinned worktree panes and side-by-side terminal/output comparison without making renderer state authoritative." />,
    terminals: terminalsTab,
    collaboration: <StubSurface eyebrow="Collaboration" title="Annotations and compiled responses" description="Routed surface reserved for Den-backed annotation sessions and compiled response review flows. No mock collaboration data is copied into production state." />,
    settings: runtimeSettingsTab,
  };

  return (
    <AppShell
      state={shellState}
      onStateChange={setShellState}
      status={runtime.status}
      snapshots={runtime.snapshots}
      sessionSnapshots={runtime.sessionSnapshots}
      diagnostics={runtime.status?.diagnostics ?? []}
      ipcHealth={runtime.ipcHealth}
    >
      {tabContent}
    </AppShell>
  );
}

function StubSurface({ eyebrow, title, description }: { eyebrow: string; title: string; description: string }) {
  return (
    <section className="panel surface-panel stub-surface">
      <p className="eyebrow">{eyebrow}</p>
      <h2>{title}</h2>
      <p className="muted">{description}</p>
      <div className="empty-state">
        <strong>Route is present; backend ownership is intentionally deferred.</strong>
        <p>This shell foundation exposes the surface without inventing runtime/domain data outside the bridge boundary.</p>
      </div>
    </section>
  );
}
