import { ReactNode, useMemo } from 'react';
import {
  ShellAccent,
  ShellBodyFont,
  ShellDensity,
  ShellRailMode,
  ShellState,
  ShellTabId,
  ShellTheme,
  nextTheme,
  shellAccents,
  shellBodyFonts,
  shellConsoleModes,
  shellDensities,
  shellRailModes,
  shellStateToDataAttributes,
  shellTabs,
  shellThemes,
} from '../shellState';
import { DiagnosticEntry, LocalGitSnapshot, LocalSessionSnapshot, OperatorStatus } from '../desktop/tauriApi';
import { IpcHealth } from '../desktop/ipcHealth';
import { buildConsoleLines, ConsoleCommandHistoryEntry, ConsoleCommandOutputLine } from '../consoleLines';
import { ConsoleDock } from './ConsoleDock';

interface AppShellProps {
  state: ShellState;
  onStateChange: (state: ShellState) => void;
  status: OperatorStatus | null;
  snapshots: LocalGitSnapshot[];
  sessionSnapshots: LocalSessionSnapshot[];
  diagnostics: DiagnosticEntry[];
  ipcHealth: IpcHealth | null;
  children: Record<ShellTabId, ReactNode>;
  activeProjectId?: string | null;
  onSelectProject?: (projectId: string) => void;
  onRunConsoleCommand?: (command: string) => Promise<void>;
  consoleCommands?: { name: string; displayName: string; description: string; needsTarget: boolean }[];
  consoleCommandHistory?: ConsoleCommandHistoryEntry[];
  activeProgressLines?: ConsoleCommandOutputLine[];
}

export function AppShell({ state, onStateChange, status, snapshots, sessionSnapshots, diagnostics, ipcHealth, children, activeProjectId, onSelectProject, onRunConsoleCommand, consoleCommands, consoleCommandHistory, activeProgressLines }: AppShellProps) {
  const setState = (patch: Partial<ShellState>) => onStateChange({ ...state, ...patch });
  const activeTab = shellTabs.some((tab) => tab.id === state.activeTab) ? state.activeTab : 'operator';
  const activeTabTitle = shellTabs.find((tab) => tab.id === activeTab)?.label ?? 'operator';
  const dataAttributes = shellStateToDataAttributes(state);
  const consoleLines = useMemo(
    () => buildConsoleLines({
      diagnostics,
      ipcHealth,
      denConnection: status?.denConnection ?? null,
      observerStatuses: status?.observerStatuses ?? [],
      lastSyncAt: status?.lastSyncAt ?? null,
    }),
    [diagnostics, ipcHealth, status?.denConnection, status?.observerStatuses, status?.lastSyncAt],
  );

  return (
    <div className="desktop-shell" {...dataAttributes}>
      <Titlebar
        activeTabTitle={activeTabTitle}
        status={status}
        theme={state.theme}
        accent={state.accent}
        onCycleTheme={() => setState({ theme: nextTheme(state.theme) })}
        onOpenSettings={() => setState({ activeTab: 'settings' })}
        onOpenSearch={() => setState({ activeTab: 'tasks' })}
      />
      <TabBar activeTab={activeTab} onSelect={(tab) => setState({ activeTab: tab })} />
      <div className="shell-main">
        <LeftRail snapshots={snapshots} activeProjectId={activeProjectId} mode={state.railMode} onModeChange={(railMode) => setState({ railMode })} onSelectProject={onSelectProject} />
        <section className="tab-canvas" aria-label={`${activeTabTitle} tab content`}>
          {activeTab === 'settings' ? (
            <div className="settings-tab tab-stack">
              <SettingsSurface state={state} onStateChange={onStateChange} />
              {children.settings}
            </div>
          ) : children[activeTab]}
        </section>
      </div>
      <ConsoleDock
        mode={state.consoleMode}
        onModeChange={(consoleMode) => setState({ consoleMode })}
        lines={consoleLines}
        onRunCommand={onRunConsoleCommand}
        consoleCommands={consoleCommands}
        consoleCommandHistory={consoleCommandHistory}
        activeProgressLines={activeProgressLines}
      />
      <StatusBar status={status} snapshots={snapshots} sessionSnapshots={sessionSnapshots} state={state} />
    </div>
  );
}

function Titlebar({
  activeTabTitle,
  status,
  theme,
  accent,
  onCycleTheme,
  onOpenSettings,
  onOpenSearch,
}: {
  activeTabTitle: string;
  status: OperatorStatus | null;
  theme: ShellTheme;
  accent: ShellAccent;
  onCycleTheme: () => void;
  onOpenSettings: () => void;
  onOpenSearch: () => void;
}) {
  const connection = status?.denConnection;
  const state = connection?.state ?? 'unknown';
  const syncId = status?.lastSyncAt ? compactTimestamp(status.lastSyncAt) : 'awaiting-sync';

  return (
    <header className="titlebar">
      <div className="titlebar-left">
        <div className="titlebar-mark">
          <span className="mark-glyph" aria-hidden="true">◈</span>
          <span>DEN DESKTOP</span>
          <span className="titlebar-muted">·</span>
          <strong>{activeTabTitle}</strong>
        </div>
      </div>
      <div className="titlebar-center">
        <span className={`pill ${statusClass(state)}`}><span className="pill-dot" />{state}</span>
        <span className="titlebar-muted">·</span>
        <span>{status?.denBaseUrl ?? connection?.message ?? 'bridge hydrating'}</span>
        <span className="titlebar-run"><span>sync</span>{syncId}</span>
      </div>
      <div className="titlebar-actions">
        <button type="button" className="icon-button" title="Open Tasks search context" onClick={onOpenSearch}>⌕</button>
        <button type="button" className="icon-button" title="Notifications are not wired yet" disabled>▽</button>
        <button type="button" className="icon-button" title={`Cycle theme (${theme})`} onClick={onCycleTheme}>◐</button>
        <button type="button" className="icon-button" title={`Settings · ${accent}`} onClick={onOpenSettings}>⚙</button>
      </div>
    </header>
  );
}

function TabBar({ activeTab, onSelect }: { activeTab: ShellTabId; onSelect: (tab: ShellTabId) => void }) {
  return (
    <nav className="tabbar" role="tablist" aria-label="Den Desktop tabs">
      {shellTabs.map((tab) => (
        <button
          key={tab.id}
          type="button"
          role="tab"
          aria-selected={activeTab === tab.id}
          className={`tab-button ${activeTab === tab.id ? 'active' : ''}`}
          onClick={() => onSelect(tab.id)}
        >
          <span className="tab-icon" aria-hidden="true">{tab.icon}</span>
          <span>{tab.label}</span>
          {tab.badge != null && <span className="tab-badge">{tab.badge}</span>}
        </button>
      ))}
    </nav>
  );
}

function LeftRail({
  snapshots,
  activeProjectId,
  mode,
  onModeChange,
  onSelectProject,
}: {
  snapshots: LocalGitSnapshot[];
  activeProjectId?: string | null;
  mode: ShellRailMode;
  onModeChange: (mode: ShellRailMode) => void;
  onSelectProject?: (projectId: string) => void;
}) {
  const rows = projectRows(snapshots, activeProjectId ?? null);
  const collapsed = mode === 'collapsed';

  return (
    <aside className="left-rail" aria-label="Project rail">
      <div className="rail-header">
        <span>Projects · {rows.length}</span>
        <button
          type="button"
          className="rail-toggle"
          title={collapsed ? 'Expand project sidebar' : 'Collapse project sidebar'}
          aria-label={collapsed ? 'Expand project sidebar' : 'Collapse project sidebar'}
          onClick={() => onModeChange(collapsed ? 'expanded' : 'collapsed')}
        >
          {collapsed ? '›' : '‹'}
        </button>
      </div>
      <div className="rail-list">
        {rows.map((row) => (
          <button
            key={row.id}
            type="button"
            className={`rail-project ${row.active ? 'active' : ''}`}
            title={collapsed ? `${row.name} · ${row.subtitle}` : undefined}
            aria-pressed={row.active}
            onClick={() => onSelectProject?.(row.id)}
          >
            <span className={`rail-dot ${row.state}`} aria-hidden="true" />
            <span className="rail-project-body">
              <strong>{row.name}</strong>
              <span>{row.subtitle}</span>
            </span>
            <span className="rail-delta">{row.delta}</span>
          </button>
        ))}
      </div>
      <div className="rail-section-title">Shell</div>
      <div className="rail-mode-controls" aria-label="Rail mode">
        {shellRailModes.map((option) => (
          <button key={option} type="button" className={mode === option ? 'active' : ''} onClick={() => onModeChange(option)}>
            {option}
          </button>
        ))}
      </div>
      <button type="button" className="rail-action" disabled title="Task creation is not wired in this desktop slice yet">+ New Task</button>
      <div className="rail-card">
        <span className="rail-card-label">Today</span>
        <span><b>{snapshots.length}</b> workspaces observed</span>
        <span><b>{snapshots.reduce((sum, snapshot) => sum + snapshot.request.dirty_counts.total, 0)}</b> dirty files</span>
      </div>
      <div className="rail-tip">
        <span>Tips</span>
        <p><kbd>⌘K</kbd> palette · <kbd>⌘`</kbd> console</p>
      </div>
    </aside>
  );
}

function StatusBar({
  status,
  snapshots,
  sessionSnapshots,
  state,
}: {
  status: OperatorStatus | null;
  snapshots: LocalGitSnapshot[];
  sessionSnapshots: LocalSessionSnapshot[];
  state: ShellState;
}) {
  return (
    <footer className="statusbar">
      <span className="status-segment"><span className={`status-light ${statusClass(status?.denConnection.state ?? 'unknown')}`} />operator-loop {status?.phase ?? 'starting'}</span>
      <span className="status-segment">{snapshots.length} workspaces</span>
      <span className="status-segment">{sessionSnapshots.length} sessions observed</span>
      <span className="status-segment">last sync {status?.lastSyncAt ? new Date(status.lastSyncAt).toLocaleTimeString() : 'waiting'}</span>
      <span className="status-spacer" />
      <span className="status-segment">density {state.density}</span>
      <span className="status-segment">console {state.consoleMode}</span>
      <span className="status-segment accent">{state.theme} · {state.accent}</span>
    </footer>
  );
}

function SettingsSurface({ state, onStateChange }: { state: ShellState; onStateChange: (state: ShellState) => void }) {
  const patch = (next: Partial<ShellState>) => onStateChange({ ...state, ...next });
  return (
    <section className="shell-settings panel surface-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Presentation settings</p>
          <h2>Shell appearance</h2>
        </div>
        <span className="chip accent">local UI state</span>
      </div>
      <p className="muted">These settings are presentation-only and serialize to local shell state. Runtime/domain settings remain behind the Den Desktop bridge boundary.</p>
      <div className="settings-grid">
        <Select label="Theme" value={state.theme} options={shellThemes} onChange={(theme) => patch({ theme })} />
        <Select label="Accent" value={state.accent} options={shellAccents} onChange={(accent) => patch({ accent })} />
        <Select label="Density" value={state.density} options={shellDensities} onChange={(density) => patch({ density })} />
        <Select label="Body font" value={state.bodyFont} options={shellBodyFonts} onChange={(bodyFont) => patch({ bodyFont })} />
        <Select label="Rail" value={state.railMode} options={shellRailModes} onChange={(railMode) => patch({ railMode })} />
        <Select label="Console" value={state.consoleMode} options={shellConsoleModes} onChange={(consoleMode) => patch({ consoleMode })} />
      </div>
    </section>
  );
}

function Select<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: readonly T[]; onChange: (value: T) => void }) {
  return (
    <label className="shell-setting-field">
      <span>{label}</span>
      <select value={value} onChange={(event) => onChange(event.target.value as T)}>
        {options.map((option) => <option key={option} value={option}>{option}</option>)}
      </select>
    </label>
  );
}

export function projectRows(snapshots: LocalGitSnapshot[], activeProjectId: string | null = null) {
  if (snapshots.length === 0) {
    return [{ id: 'den-mcp', name: 'den-mcp', subtitle: 'awaiting bridge snapshot', delta: '—', state: 'idle', active: true }];
  }

  const byProject = new Map<string, { dirty: number; workspaces: number; warning: boolean }>();
  for (const snapshot of snapshots) {
    const id = snapshot.scope.projectId;
    const current = byProject.get(id) ?? { dirty: 0, workspaces: 0, warning: false };
    current.dirty += snapshot.request.dirty_counts.total;
    current.workspaces += 1;
    current.warning ||= snapshot.request.warnings.length > 0 || snapshot.request.state !== 'ok';
    byProject.set(id, current);
  }

  const sorted = [...byProject.entries()].sort(([a], [b]) => a.localeCompare(b));
  const activeId = activeProjectId && byProject.has(activeProjectId) ? activeProjectId : sorted[0]?.[0] ?? null;
  return sorted.map(([id, item]) => ({
    id,
    name: id,
    subtitle: `${item.workspaces} workspace${item.workspaces === 1 ? '' : 's'}`,
    delta: item.dirty > 0 ? `±${item.dirty}` : 'clean',
    state: item.warning ? 'warn' : item.dirty > 0 ? 'running' : 'ok',
    active: id === activeId,
  }));
}

function compactTimestamp(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
}


function statusClass(state: string): string {
  if (state === 'connected' || state === 'ok' || state === 'ready') return 'ok';
  if (state === 'degraded' || state === 'path_not_visible' || state === 'not_git_repository') return 'warn';
  if (state === 'offline' || state === 'misconfigured' || state === 'git_error' || state === 'failed') return 'err';
  if (state === 'running' || state === 'loading') return 'running';
  return 'idle';
}
