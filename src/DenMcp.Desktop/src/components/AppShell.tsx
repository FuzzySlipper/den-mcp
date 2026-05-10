import { ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ShellAccent,
  ShellBodyFont,
  ShellDensity,
  ShellRailMode,
  ShellState,
  ShellTabId,
  ShellTheme,
  acceleratorMatchesEvent,
  defaultHotkeys,
  hotkeyActions,
  nextConsoleMode,
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
import { DenSpace, DiagnosticEntry, LocalGitSnapshot, LocalSessionSnapshot, OperatorStatus } from '../desktop/sidecarBridgeApi';
import { IpcHealth } from '../desktop/ipcHealth';
import { buildConsoleLines, ConsoleCommandHistoryEntry, ConsoleCommandLine } from '../consoleLines';
import { type TaskStatusFilter } from '../tasksDashboardView';
import { ConsoleDock } from './ConsoleDock';
import { CommandPalette, type CommandPaletteCallbacks } from './CommandPalette';
import { GLOBAL_PROJECT_ID, globalRailRow, isMultiWorkspaceProject, projectRowTitle, spaceRows, workspaceRowLabel, workspaceRowsForProject, workspaceToggleLabel } from '../railView';

interface AppShellProps {
  state: ShellState;
  onStateChange: (state: ShellState) => void;
  status: OperatorStatus | null;
  snapshots: LocalGitSnapshot[];
  sessionSnapshots: LocalSessionSnapshot[];
  spaces: DenSpace[];
  diagnostics: DiagnosticEntry[];
  ipcHealth: IpcHealth | null;
  children: Record<ShellTabId, ReactNode>;
  activeProjectId?: string | null;
  /** Active snapshot key used to highlight the selected workspace in multi-workspace projects. */
  activeSnapshotKey?: string | null;
  onSelectProject?: (projectId: string) => void;
  onSelectSnapshot?: (snapshot: LocalGitSnapshot) => void;
  onRunConsoleCommand?: (command: string) => Promise<void>;
  consoleCommands?: { name: string; displayName: string; description: string; needsTarget: boolean }[];
  consoleCommandHistory?: ConsoleCommandHistoryEntry[];
  activeProgressLines?: ConsoleCommandLine[];
  /** Name of the currently running command for the in-flight progress header. */
  activeProgressCommand?: string;
  /** External task-filter override driven by command palette; null = no override. */
  taskStatusFilterOverride?: TaskStatusFilter | null;
  onTaskStatusFilterOverride?: (filter: TaskStatusFilter | null) => void;
}

export function AppShell({ state, onStateChange, status, snapshots, sessionSnapshots, spaces, diagnostics, ipcHealth, children, activeProjectId, activeSnapshotKey, onSelectProject, onSelectSnapshot, onRunConsoleCommand, consoleCommands, consoleCommandHistory, activeProgressLines, activeProgressCommand, taskStatusFilterOverride, onTaskStatusFilterOverride }: AppShellProps) {
  const [paletteOpen, setPaletteOpen] = useState(false);
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

  const paletteCallbacks: CommandPaletteCallbacks = useMemo(
    () => ({
      onNavigate: (tab) => setState({ activeTab: tab }),
      onFilterTasks: (filter) => {
        setState({ activeTab: 'tasks' });
        onTaskStatusFilterOverride?.(filter);
      },
      onCycleTheme: () => setState({ theme: nextTheme(state.theme) }),
      onToggleConsole: () => setState({ consoleMode: nextConsoleMode(state.consoleMode) }),
      onClose: () => setPaletteOpen(false),
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [state.theme, state.consoleMode, onTaskStatusFilterOverride],
  );

  // Global Ctrl+K shortcut to open palette
  const handleGlobalKeyDown = useCallback((e: KeyboardEvent) => {
    if (!paletteOpen && (e.ctrlKey || e.metaKey) && e.key === 'k') {
      e.preventDefault();
      setPaletteOpen(true);
    }
  }, [paletteOpen]);

  useEffect(() => {
    window.addEventListener('keydown', handleGlobalKeyDown);
    return () => window.removeEventListener('keydown', handleGlobalKeyDown);
  }, [handleGlobalKeyDown]);

  // Hotkey handling: window-local keydown matching for configured accelerators.
  // Browser_Back is handled via app-command in the main process and delivered
  // through onHotkeyAction below.
  const hotkeyActionsRef = useRef(state.hotkeys);
  hotkeyActionsRef.current = state.hotkeys;

  useEffect(() => {
    const handleKeyDown = (e: KeyboardEvent) => {
      for (const [action, accelerator] of Object.entries(hotkeyActionsRef.current)) {
        if (accelerator === 'Browser_Back') continue;
        if (acceleratorMatchesEvent(accelerator, e)) {
          e.preventDefault();
          switch (action) {
            case 'cycleTabForward': {
              const tabs = shellTabs.map((t) => t.id);
              const currentIndex = tabs.indexOf(activeTab);
              const nextTab = tabs[(currentIndex + 1) % tabs.length];
              setState({ activeTab: nextTab });
              break;
            }
            case 'goBack':
              window.history.back();
              break;
            case 'focusConsole':
              // Ensure console is at least preview mode when focusing
              if (state.consoleMode === 'collapsed') {
                setState({ consoleMode: 'preview' });
              }
              // Focus the console input element
              setTimeout(() => {
                const input = document.querySelector<HTMLInputElement>('.console-prompt input');
                input?.focus();
              }, 0);
              break;
          }
          break; // handle first match only
        }
      }
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => window.removeEventListener('keydown', handleKeyDown);
  }, [activeTab, state.consoleMode, setState]);

  // Compatibility no-op: the IPC handler in main.ts is intentionally empty
  // after task #1166. This call is kept so the renderer does not break if
  // the preload still exposes registerHotkeys.
  useEffect(() => {
    const api = (window as any).denDesktopSidecar as Record<string, unknown> | undefined;
    if (api && typeof api.registerHotkeys === 'function') {
      api.registerHotkeys(state.hotkeys).catch(() => {
        // Registration best-effort in Electron; safe to ignore in browser
      });
    }
  }, [state.hotkeys]);

  // App-command dispatches (e.g. Browser_Back / goBack) still arrive via IPC.
  useEffect(() => {
    const api = (window as any).denDesktopSidecar as Record<string, unknown> | undefined;
    if (!api || typeof api.onHotkeyAction !== 'function') return;

    const unsub = api.onHotkeyAction((action: string) => {
      switch (action) {
        case 'cycleTabForward': {
          const tabs = shellTabs.map((t) => t.id);
          const currentIndex = tabs.indexOf(activeTab);
          const nextTab = tabs[(currentIndex + 1) % tabs.length];
          setState({ activeTab: nextTab });
          break;
        }
        case 'goBack':
          window.history.back();
          break;
        case 'focusConsole':
          // Ensure console is at least preview mode when focusing
          if (state.consoleMode === 'collapsed') {
            setState({ consoleMode: 'preview' });
          }
          // Focus the console input element
          setTimeout(() => {
            const input = document.querySelector<HTMLInputElement>('.console-prompt input');
            input?.focus();
          }, 0);
          break;
      }
    });

    return () => {
      if (typeof unsub === 'function') unsub();
    };
  }, [activeTab, state.consoleMode, setState]);

  return (
    <div className="desktop-shell" {...dataAttributes}>
      <Titlebar
        activeTabTitle={activeTabTitle}
        status={status}
        theme={state.theme}
        accent={state.accent}
        onCycleTheme={() => setState({ theme: nextTheme(state.theme) })}
        onOpenSettings={() => setState({ activeTab: 'settings' })}
        onOpenSearch={() => setPaletteOpen(true)}
      />
      <CommandPalette open={paletteOpen} callbacks={paletteCallbacks} />
      <TabBar activeTab={activeTab} onSelect={(tab) => setState({ activeTab: tab })} />
      <div className="shell-main">
        <LeftRail spaces={spaces} snapshots={snapshots} activeProjectId={activeProjectId} activeSnapshotKey={activeSnapshotKey} mode={state.railMode} onModeChange={(railMode) => setState({ railMode })} onSelectProject={onSelectProject} onSelectSnapshot={onSelectSnapshot} />
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
        activeProgressCommand={activeProgressCommand}
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
        <button type="button" className="icon-button" title="Command palette (Ctrl+K)" onClick={onOpenSearch}>⌕</button>
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
  spaces,
  snapshots,
  activeProjectId,
  activeSnapshotKey,
  mode,
  onModeChange,
  onSelectProject,
  onSelectSnapshot,
}: {
  spaces: DenSpace[];
  snapshots: LocalGitSnapshot[];
  activeProjectId?: string | null;
  activeSnapshotKey?: string | null;
  mode: ShellRailMode;
  onModeChange: (mode: ShellRailMode) => void;
  onSelectProject?: (projectId: string) => void;
  onSelectSnapshot?: (snapshot: LocalGitSnapshot) => void;
}) {
  const isGlobalActive = activeProjectId === GLOBAL_PROJECT_ID;
  const rows = spaceRows(spaces, snapshots, activeProjectId ?? null);
  const allRows = [globalRailRow(isGlobalActive), ...rows];
  const collapsed = mode === 'collapsed';
  // Track which multi-workspace project is expanded in the rail.
  // This is transient UI state, not domain state.
  const [expandedProjectId, setExpandedProjectId] = useState<string | null>(null);

  const handleProjectClick = (projectId: string) => {
    // Row clicks always select the space/project. Multi-workspace expansion is
    // intentionally handled by a separate adjacent control to avoid hidden
    // select-and-expand side effects.
    onSelectProject?.(projectId);
    if (!isMultiWorkspaceProject(snapshots, projectId)) {
      setExpandedProjectId(null);
    }
  };

  const handleProjectToggle = (projectId: string) => {
    setExpandedProjectId((prev) => prev === projectId ? null : projectId);
  };

  const handleWorkspaceClick = (snapshot: LocalGitSnapshot) => {
    onSelectProject?.(snapshot.scope.projectId);
    onSelectSnapshot?.(snapshot);
    setExpandedProjectId(null);
  };

  return (
    <aside className="left-rail" aria-label="Space rail">
      <div className="rail-header">
        <span>Spaces · {rows.length}</span>
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
        {allRows.map((row) => {
          const multi = row.kind === 'project' && row.workspaceCount > 1;
          const expanded = expandedProjectId === row.id;
          const workspaces = multi && expanded ? workspaceRowsForProject(snapshots, row.id) : [];
          const workspaceListId = `rail-workspaces-${row.id.replace(/[^A-Za-z0-9_-]/g, '-')}`;
          return (
            <div key={row.id} className="rail-project-group">
              <div className={`rail-project-row ${multi ? 'multi-workspace' : ''}`}>
                <button
                  type="button"
                  className={`rail-project ${row.active ? 'active' : ''} ${row.id === GLOBAL_PROJECT_ID ? 'global' : ''} ${multi ? 'multi-workspace' : ''}`}
                  title={projectRowTitle(row, multi)}
                  aria-pressed={row.active}
                  onClick={() => handleProjectClick(row.id)}
                >
                  <span className={`rail-dot ${row.id === GLOBAL_PROJECT_ID ? 'global' : row.state}`} aria-hidden="true" />
                  <span className="rail-project-body">
                    <strong>{row.name}</strong>
                    <span>{row.subtitle}</span>
                  </span>
                  {row.id === GLOBAL_PROJECT_ID ? (
                    <span className="rail-global-badge" aria-hidden="true">◈</span>
                  ) : row.kind !== 'project' && !collapsed ? (
                    <span className="rail-space-badge">{row.visibility && row.visibility !== 'normal' ? row.visibility : row.kind}</span>
                  ) : !multi ? (
                    <span className="rail-delta">{row.delta}</span>
                  ) : null}
                </button>
                {multi && !collapsed ? (
                  <button
                    type="button"
                    className={`rail-expand-button ${expanded ? 'expanded' : ''}`}
                    title={workspaceToggleLabel(row.name, expanded)}
                    aria-label={workspaceToggleLabel(row.name, expanded)}
                    aria-expanded={expanded}
                    aria-controls={workspaceListId}
                    onClick={() => handleProjectToggle(row.id)}
                  >
                    <span className={`rail-expand-indicator ${expanded ? 'expanded' : ''}`} aria-hidden="true">{expanded ? '▾' : '▸'}</span>
                  </button>
                ) : null}
              </div>
              {expanded && workspaces.length > 0 && (
                <div id={workspaceListId} className="rail-workspace-list" role="listbox" aria-label={`${row.name} workspaces`}>
                  {workspaces.map((ws) => (
                    <button
                      key={ws.snapshotKey}
                      type="button"
                      role="option"
                      className={`rail-workspace ${ws.snapshotKey === activeSnapshotKey ? 'active' : ''}`}
                      aria-selected={ws.snapshotKey === activeSnapshotKey}
                      onClick={() => handleWorkspaceClick(snapshots.find((s) => {
                        const key = [s.scope.projectId, s.scope.workspaceId ?? 'project', s.scope.taskId ?? 'none', s.request.root_path].join('::');
                        return key === ws.snapshotKey;
                      })!)}
                    >
                      <span className={`rail-dot ${ws.state}`} aria-hidden="true" />
                      <span className="rail-workspace-body">
                        <strong>{workspaceRowLabel(ws)}</strong>
                        <span>{ws.branch ?? 'no branch'} · {ws.dirty > 0 ? `±${ws.dirty}` : 'clean'}</span>
                      </span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          );
        })}
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
        <span><b>{rows.length}</b> spaces listed</span>
        <span><b>{snapshots.length}</b> repo/workspace snapshots observed</span>
        <span><b>{snapshots.reduce((sum, snapshot) => sum + snapshot.request.dirty_counts.total, 0)}</b> dirty files</span>
        <span>Git/terminal controls apply only to project or root-backed spaces.</span>
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

  const handleResetHotkeys = () => {
    patch({ hotkeys: { ...defaultHotkeys } });
  };

  const handleHotkeyChange = (action: string, value: string) => {
    patch({ hotkeys: { ...state.hotkeys, [action]: value } });
  };

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

      <div className="hotkey-section">
        <div className="panel-heading" style={{ marginTop: 'var(--sp-6)' }}>
          <div>
            <p className="eyebrow">Keyboard shortcuts</p>
            <h2>Hotkeys</h2>
          </div>
          <span className="chip accent">local UI state</span>
        </div>
        <p className="muted">Configure keyboard shortcuts. Shortcuts are active only while the Den Desktop window is focused. Changes take effect immediately. The "Reset to defaults" button restores the initial bindings.</p>
        <div className="hotkey-grid">
          {hotkeyActions.map(({ action, label, description }) => (
            <HotkeyRow
              key={action}
              action={action}
              label={label}
              description={description}
              value={state.hotkeys[action] ?? ''}
              onChange={handleHotkeyChange}
            />
          ))}
        </div>
        <div className="button-row" style={{ marginTop: 'var(--sp-3)' }}>
          <button type="button" onClick={handleResetHotkeys}>Reset to defaults</button>
        </div>
      </div>
    </section>
  );
}

function HotkeyRow({ action, label, description, value, onChange }: { action: string; label: string; description: string; value: string; onChange: (action: string, value: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [buffer, setBuffer] = useState(value);
  const inputRef = useRef<HTMLInputElement>(null);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    e.preventDefault();
    e.stopPropagation();

    const parts: string[] = [];
    if (e.ctrlKey || e.metaKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');

    // Map key names to Electron accelerator format
    const keyMap: Record<string, string> = {
      'Tab': 'Tab',
      '`': '`',
      '~': '`',
      'Escape': 'Escape',
      'Enter': 'Enter',
      ' ': 'Space',
      'Backspace': 'Backspace',
      'Delete': 'Delete',
      'ArrowUp': 'Up',
      'ArrowDown': 'Down',
      'ArrowLeft': 'Left',
      'ArrowRight': 'Right',
      'Home': 'Home',
      'End': 'End',
      'PageUp': 'PageUp',
      'PageDown': 'PageDown',
    };

    let key = e.key;
    if (keyMap[key]) key = keyMap[key];

    // Ignore modifier-only keydowns
    if (['Control', 'Shift', 'Alt', 'Meta'].includes(key)) return;

    if (parts.length > 0 && key.length === 1 && key !== 'Tab' && key !== '`') {
      key = key.toUpperCase();
    }

    const accelerator = [...parts, key].join('+');
    setBuffer(accelerator);
    setEditing(false);
    onChange(action, accelerator);
  }, [action, onChange]);

  // Start editing on click
  const handleStartEdit = () => {
    setEditing(true);
    setBuffer('');
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const handleBlur = () => {
    if (editing && buffer === '') {
      // If user clicked away without pressing anything, revert
      setEditing(false);
    }
  };

  return (
    <div className="hotkey-field">
      <div className="hotkey-field-label">
        <span className="hotkey-action-label">{label}</span>
        <span className="hotkey-action-desc">{description}</span>
      </div>
      {editing ? (
        <input
          ref={inputRef}
          type="text"
          className="hotkey-input-capture"
          value={buffer}
          readOnly
          onKeyDown={handleKeyDown}
          onBlur={handleBlur}
          placeholder="Press keys..."
          autoFocus
        />
      ) : (
        <button type="button" className="hotkey-badge" onClick={handleStartEdit} title="Click to change">
          <kbd>{value || 'none'}</kbd>
        </button>
      )}
    </div>
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

// projectRows moved to railView.ts

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
