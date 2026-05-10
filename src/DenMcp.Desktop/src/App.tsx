import { ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { AgentPane } from './components/AgentPane';
import { CollaborationPane } from './components/CollaborationPane';
import { TasksDashboardPane } from './components/TasksDashboardPane';
import { MessagesPane } from './components/MessagesPane';
import { DocsPane } from './components/DocsPane';
import { useCollaborationState } from './desktop/useCollaborationState';
import { AppShell } from './components/AppShell';
import { ConnectionPanel } from './components/ConnectionPanel';
import { DiagnosticsPane } from './components/DiagnosticsPane';
import { DiffPane } from './components/DiffPane';
import { GitSnapshotPane } from './components/GitSnapshotPane';
import { SessionPane } from './components/SessionPane';
import { WorkspaceSummaryPane } from './components/WorkspaceSummaryPane';
import { SpacesPane } from './components/SpacesPane';
import { getLatestDiffSnapshot } from './desktop/sidecarBridgeApi';
import type { DesktopDiffSnapshotLatestResult, GitFileStatus, LocalGitSnapshot, ShellAppearanceSettings } from './desktop/sidecarBridgeApi';
import { useOperatorRuntime } from './desktop/useOperatorRuntime';
import { applyShellDataAttributes, defaultShellState, loadShellState, parseShellState, saveShellState, ShellState, ShellTabId } from './shellState';
import { GLOBAL_PROJECT_ID } from './railView';
import { type TaskStatusFilter } from './tasksDashboardView';
import { buildLatestDiffSnapshotRequest, snapshotKey } from './snapshotView';
import './styles/index.css';

function bridgeAppearanceToShellState(appearance: ShellAppearanceSettings | null, fallback: ShellState): ShellState {
  if (!appearance) return fallback;
  return parseShellState({
    theme: appearance.theme,
    accent: appearance.accent,
    density: appearance.density,
    bodyFont: appearance.bodyFont,
    railMode: appearance.railMode,
    consoleMode: appearance.consoleMode,
    activeTab: appearance.activeTab,
  }, fallback);
}

export function App() {
  const runtime = useOperatorRuntime();
  const [shellState, setShellState] = useState<ShellState>(() => {
    // Bootstrap from localStorage for immediate render, then bridge durable settings take over on mount.
    return loadShellState(typeof window === 'undefined' ? null : window.localStorage);
  });

  // Once bridge appearance settings load, reconcile: durable bridge source wins over localStorage bootstrap.
  const bridgeAppliedRef = useRef(false);
  useEffect(() => {
    if (runtime.appearanceSettings && !bridgeAppliedRef.current) {
      bridgeAppliedRef.current = true;
      setShellState((current) => {
        const bridge = bridgeAppearanceToShellState(runtime.appearanceSettings, current);
        return bridge;
      });
    }
  }, [runtime.appearanceSettings]);
  // selectedProjectId is managed in shellState but we also track it locally
  // so it can drive tab filtering independently of the active snapshot.
  const selectedProjectId = shellState.selectedProjectId;
  const [activeSnapshotKey, setActiveSnapshotKey] = useState<string | null>(null);
  const [selectedFile, setSelectedFile] = useState<GitFileStatus | null>(null);
  const [taskStatusFilterOverride, setTaskStatusFilterOverride] = useState<TaskStatusFilter | null>(null);
  const [diff, setDiff] = useState<DesktopDiffSnapshotLatestResult | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);

  useEffect(() => {
    if (typeof document !== 'undefined') {
      applyShellDataAttributes(document.documentElement, shellState);
    }
    // Durable storage: save through the bridge (sidecar .NET runtime).
    // Bootstrap/local fallback: localStorage for immediate render on next cold start.
    runtime.saveAppearanceSettings({
      theme: shellState.theme,
      accent: shellState.accent,
      density: shellState.density,
      bodyFont: shellState.bodyFont,
      railMode: shellState.railMode,
      consoleMode: shellState.consoleMode,
      activeTab: shellState.activeTab,
    }).catch(() => {
      // Bridge save is best-effort; localStorage bootstrap preserves the last known value.
    });
    if (typeof window !== 'undefined') {
      saveShellState(window.localStorage, shellState);
    }
  }, [shellState, runtime.saveAppearanceSettings]);

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
      const result = await getLatestDiffSnapshot(buildLatestDiffSnapshotRequest(snapshot, file));
      setDiff(result);
    } catch (err) {
      setDiffError(err instanceof Error ? err.message : String(err));
    } finally {
      setDiffLoading(false);
    }
  };

  const selectProject = (projectId: string) => {
    if (projectId === GLOBAL_PROJECT_ID) {
      // Global: set the project filter to '_global', keep current snapshot for Git diff
      setShellState((current) => ({ ...current, selectedProjectId: GLOBAL_PROJECT_ID }));
      return;
    }
    // Specific project: update the shell state and also find matching snapshot for Git diffs
    setShellState((current) => ({ ...current, selectedProjectId: projectId }));
    const nextSnapshot = runtime.snapshots.find((snapshot) => snapshot.scope.projectId === projectId) ?? null;
    if (nextSnapshot) {
      selectSnapshot(nextSnapshot);
    }
  };

  const handleSelectSnapshot = (snapshot: LocalGitSnapshot) => {
    selectSnapshot(snapshot);
  };

  // Effective space filter: '_global' or a specific space ID, or null for 'no selection'.
  // The persisted field keeps the historical selectedProjectId name for localStorage compatibility.
  const effectiveProjectFilter = selectedProjectId;

  // Filter snapshots by selected space/project for tabs that need repo-backed filtering.
  // When '_global' is selected (or no snapshots yet), show all snapshots.
  const filteredSnapshots = useMemo(() => {
    if (!effectiveProjectFilter || effectiveProjectFilter === GLOBAL_PROJECT_ID) {
      return runtime.snapshots;
    }
    return runtime.snapshots.filter((s) => s.scope.projectId === effectiveProjectFilter);
  }, [runtime.snapshots, effectiveProjectFilter]);

  const activeContextSnapshot = useMemo(() => {
    if (!effectiveProjectFilter || effectiveProjectFilter === GLOBAL_PROJECT_ID) {
      return activeSnapshot;
    }
    return activeSnapshot?.scope.projectId === effectiveProjectFilter ? activeSnapshot : null;
  }, [activeSnapshot, effectiveProjectFilter]);

  useEffect(() => {
    if (activeSnapshot && !activeContextSnapshot) {
      setSelectedFile(null);
      setDiff(null);
      setDiffError(null);
    }
  }, [activeContextSnapshot, activeSnapshot]);

  // Filter session snapshots by selected space/project.
  const filteredSessionSnapshots = useMemo(() => {
    if (!effectiveProjectFilter || effectiveProjectFilter === GLOBAL_PROJECT_ID) {
      return runtime.sessionSnapshots;
    }
    return runtime.sessionSnapshots.filter((s) => s.projectId === effectiveProjectFilter);
  }, [runtime.sessionSnapshots, effectiveProjectFilter]);

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

      <WorkspaceSummaryPane snapshots={filteredSnapshots} activeKey={activeContextSnapshot ? snapshotKey(activeContextSnapshot) : null} onSelect={selectSnapshot} />
      <SpacesPane spaces={runtime.status?.spaces ?? []} activeSpaceId={effectiveProjectFilter} onSelectSpace={selectProject} />
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
          snapshots={filteredSnapshots}
          activeSnapshotKey={activeContextSnapshot ? snapshotKey(activeContextSnapshot) : null}
          selectedFilePath={selectedFile?.path ?? null}
          onSelectSnapshot={selectSnapshot}
          onSelectFile={selectFile}
        />
        <DiffPane snapshot={activeContextSnapshot} file={selectedFile} diff={diff} loading={diffLoading} error={diffError} />
      </div>
    </div>
  );

  const terminalsTab = (
    <div className="terminals-tab tab-stack">
      <section className="tab-intro panel surface-panel">
        <p className="eyebrow">Terminals</p>
        <h2>Session overview and attach workflow</h2>
        <p className="muted">
          Browse direct PTY, tmux-backed, and observed-only sessions; attach inline only when raw-stream capabilities are reported by the sidecar.
        </p>
      </section>
      <SessionPane snapshots={filteredSessionSnapshots} workspaces={filteredSnapshots} />
    </div>
  );

  // Collaboration tab state — derive project/task context from the effective project filter
  const collabProjectId = effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (effectiveProjectFilter ?? null);
  const collabTaskId = activeContextSnapshot?.scope.taskId ?? null;
  const collaborationState = useCollaborationState(
    runtime.status?.denBaseUrl ?? null,
    collabProjectId,
    collabTaskId,
  );

  const collaborationTab = (
    <CollaborationPane
      state={collaborationState}
      actions={collaborationState}
    />
  );

  const runtimeSettingsTab = (
    <ConnectionPanel
      status={runtime.status}
      settings={runtime.settings}
      onRefresh={runtime.refresh}
      onSaveSettings={runtime.saveSettings}
    />
  );

  const agentSelection = useMemo(() => {
    if (!activeContextSnapshot) return null;
    return {
      project_id: activeContextSnapshot.scope.projectId,
      task_id: activeContextSnapshot.scope.taskId,
      workspace_id: activeContextSnapshot.scope.workspaceId,
      current_tab: shellState.activeTab,
      selected_file_path: selectedFile?.path ?? null,
    };
  }, [activeContextSnapshot, shellState.activeTab, selectedFile]);

  const tabContent: Record<ShellTabId, ReactNode> = {
    operator: operatorTab,
    agent: <AgentPane selection={agentSelection} />,
    tasks: <TasksDashboardPane projectId={effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (effectiveProjectFilter ?? null)} parentTaskId={effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (activeContextSnapshot?.scope.taskId ?? null)} statusFilterOverride={taskStatusFilterOverride} onNavigateToMessagesTab={(taskId) => setShellState((current) => ({ ...current, activeTab: 'messages' as ShellTabId }))} onNavigateToDocsTab={() => setShellState((current) => ({ ...current, activeTab: 'docs' as ShellTabId }))} />,
    messages: <MessagesPane projectId={effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (effectiveProjectFilter ?? null)} taskId={effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (activeContextSnapshot?.scope.taskId ?? null)} />,
    docs: <DocsPane projectId={effectiveProjectFilter === GLOBAL_PROJECT_ID ? null : (effectiveProjectFilter ?? null)} />,
    git: gitTab,
    compare: <StubSurface eyebrow="Compare" title="Multi-worktree compare" description="Routed surface reserved for pinned worktree panes and side-by-side terminal/output comparison without making renderer state authoritative." />,
    terminals: terminalsTab,
    collaboration: collaborationTab,
    settings: runtimeSettingsTab,
  };

  const runConsoleCommand = useCallback(async (command: string) => {
    try {
      await runtime.runConsoleCommand(command);
    } catch (err) {
      // Error is already recorded in the runtime state / history
    }
  }, [runtime]);

  return (
    <AppShell
      state={shellState}
      onStateChange={setShellState}
      status={runtime.status}
      snapshots={runtime.snapshots}
      sessionSnapshots={runtime.sessionSnapshots}
      spaces={runtime.status?.spaces ?? []}
      diagnostics={runtime.status?.diagnostics ?? []}
      ipcHealth={runtime.ipcHealth}
      activeProjectId={selectedProjectId}
      activeSnapshotKey={activeContextSnapshot ? snapshotKey(activeContextSnapshot) : null}
      onSelectProject={selectProject}
      onSelectSnapshot={handleSelectSnapshot}
      onRunConsoleCommand={runConsoleCommand}
      consoleCommands={runtime.consoleCommands}
      consoleCommandHistory={runtime.consoleCommandHistory}
      activeProgressLines={runtime.activeProgressLines}
      activeProgressCommand={runtime.activeProgressCommand ?? undefined}
      taskStatusFilterOverride={taskStatusFilterOverride}
      onTaskStatusFilterOverride={setTaskStatusFilterOverride}
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
