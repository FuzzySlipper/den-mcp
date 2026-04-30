import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  TasksDashboardSnapshot,
  TasksDashboardTaskRow,
} from '../electron/sidecarProtocol.ts';
import {
  tasksGetDashboardSnapshot,
  type TasksDashboardGetSnapshotRequest,
} from '../desktop/tauriApi.ts';
import {
  buildDashboardView,
  copyToClipboard,
  formatCost,
  formatTokenCount,
  progressStageLabel,
  relativeTimeLabel,
  taskStatusLabel,
  type DashboardView,
  type HeaderView,
  type LaneView,
  type PacketSummary,
  type SessionChipView,
  type StatusPanelSection,
  type TaskRowView,
  type WaveView,
} from '../tasksDashboardView.ts';

interface Props {
  projectId: string | null;
  parentTaskId?: number | null;
}

const REFRESH_INTERVAL_MS = 30_000;

export function TasksDashboardPane({ projectId, parentTaskId }: Props) {
  const [snapshot, setSnapshot] = useState<TasksDashboardSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [focusedTaskId, setFocusedTaskId] = useState<number | null>(null);
  const [lastRefreshAt, setLastRefreshAt] = useState<string | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => { mountedRef.current = true; return () => { mountedRef.current = false; }; }, []);

  const fetchSnapshot = useCallback(async () => {
    if (!projectId) return;
    setLoading(true);
    setError(null);
    try {
      const request: TasksDashboardGetSnapshotRequest = {
        project_id: projectId,
        parent_task_id: parentTaskId ?? null,
        focused_task_id: focusedTaskId,
      };
      const result = await tasksGetDashboardSnapshot(request);
      if (mountedRef.current) {
        setSnapshot(result);
        setLastRefreshAt(new Date().toISOString());
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, [projectId, parentTaskId, focusedTaskId]);

  // Initial load and periodic refresh
  useEffect(() => {
    void fetchSnapshot();
    const interval = window.setInterval(() => void fetchSnapshot(), REFRESH_INTERVAL_MS);
    return () => window.clearInterval(interval);
  }, [fetchSnapshot]);

  const view = useMemo(() => buildDashboardView(snapshot, focusedTaskId), [snapshot, focusedTaskId]);

  // No project selected
  if (!projectId) {
    return (
      <section className="panel surface-panel tasks-dashboard">
        <p className="eyebrow">Tasks</p>
        <h2>Orchestrator dashboard</h2>
        <div className="empty-state">
          <strong>No project selected.</strong>
          <p>Select a project from the left rail to load the tasks dashboard.</p>
        </div>
      </section>
    );
  }

  return (
    <section className="panel tasks-dashboard">
      <TasksDashboardHeader
        projectId={projectId}
        parentTaskId={parentTaskId}
        view={view}
        loading={loading}
        error={error}
        lastRefreshAt={lastRefreshAt}
        onRefresh={() => void fetchSnapshot()}
      />
      {snapshot ? (
        <>
          <WaveStrip waves={view.waves} tasks={view.tasks} />
          <div className="tasks-workbench-grid">
            <div className="tasks-lane-list">
              {view.tasks.length === 0 ? (
                <div className="empty-state">
                  <strong>No tasks found.</strong>
                  <p>Tasks will appear here once they are created in Den for this project.</p>
                </div>
              ) : view.tasks.map((task) => (
                <TaskRowCard
                  key={task.id}
                  task={task}
                  focused={task.isFocused}
                  onSelect={() => setFocusedTaskId(task.isFocused ? null : task.id)}
                />
              ))}
            </div>
            <aside className="tasks-status-panel panel surface-panel">
              <StatusPanel sections={view.statusPanel} />
              <FreshnessPanel view={view.freshness} />
            </aside>
          </div>
          {view.lanes.length > 0 && (
            <LaneOverview lanes={view.lanes} />
          )}
        </>
      ) : (
        <div className="empty-state">
          <strong>{loading ? 'Loading tasks dashboard…' : 'No snapshot available.'}</strong>
          <p>{loading ? 'Fetching the latest task data from the Den Desktop bridge.' : 'The sidecar has not returned a tasks dashboard snapshot yet. Check the bridge connection or try refreshing.'}</p>
        </div>
      )}
    </section>
  );
}

// ── Sub-components ──────────────────────────────────────────────

function TasksDashboardHeader({
  projectId,
  parentTaskId,
  view,
  loading,
  error,
  lastRefreshAt,
  onRefresh,
}: {
  projectId: string;
  parentTaskId?: number | null;
  view: DashboardView;
  loading: boolean;
  error: string | null;
  lastRefreshAt: string | null;
  onRefresh: () => void;
}) {
  const h = view.header;
  return (
    <div className="tasks-header">
      <div className="tasks-header-title">
        <p className="eyebrow">Tasks · {projectId}{parentTaskId != null ? ` · #${parentTaskId}` : ''}</p>
        <h2>Orchestrator dashboard</h2>
      </div>
      <div className="tasks-header-metrics">
        <div className="tasks-header-state">
          <span className={`status-pill status-${h.stateTone}`}>{h.stateLabel}</span>
          <span className="tasks-completion">{h.completionPercent}%</span>
        </div>
        <div className="tasks-header-counts">
          <span className="tasks-count-item"><strong>{h.taskCount}</strong> tasks</span>
          <span className="tasks-count-item ok"><strong>{h.doneCount}</strong> done</span>
          <span className="tasks-count-item running"><strong>{h.activeCount}</strong> active</span>
          <span className="tasks-count-item accent"><strong>{h.reviewCount}</strong> review</span>
          {h.blockedCount > 0 && <span className="tasks-count-item err"><strong>{h.blockedCount}</strong> blocked</span>}
        </div>
        <div className="tasks-header-cost">
          <span className="tasks-cost-item"><strong>{formatTokenCount(h.totalTokens)}</strong> tokens</span>
          <span className="tasks-cost-item"><strong>{formatCost(h.totalCost, h.currency)}</strong></span>
          <span className="tasks-cost-item">updated {h.lastUpdatedLabel ?? '—'}</span>
        </div>
      </div>
      <div className="tasks-header-actions">
        <button type="button" onClick={onRefresh} disabled={loading}>{loading ? 'Refreshing…' : 'Refresh'}</button>
        {error ? <span className="tasks-error">{error}</span> : null}
      </div>
    </div>
  );
}

function WaveStrip({ waves, tasks }: { waves: WaveView[]; tasks: TaskRowView[] }) {
  if (waves.length === 0) return null;
  return (
    <div className="tasks-wave-strip" aria-label="Computed waves">
      {waves.map((wave) => (
        <div key={wave.index} className={`tasks-wave tasks-wave-${wave.tone}`} title={wave.summary ?? wave.label}>
          <span className="tasks-wave-label">{wave.label}</span>
          <span className="tasks-wave-count">{wave.taskIds.length} tasks</span>
          <span className={`tasks-wave-state status-pill status-${wave.tone}`}>{wave.state}</span>
        </div>
      ))}
    </div>
  );
}

function TaskRowCard({ task, focused, onSelect }: { task: TaskRowView; focused: boolean; onSelect: () => void }) {
  return (
    <article
      className={`tasks-row-card ${focused ? 'focused' : ''} tasks-row-${task.displayTone}`}
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onSelect(); } }}
      aria-pressed={focused}
      aria-label={`Task #${task.id} ${task.title}, ${task.status}, ${task.progressStageLabel}`}
    >
      <div className="tasks-row-topline">
        <div className="tasks-row-id-title">
          <span className="tasks-row-id">#{task.id}</span>
          <strong className="tasks-row-title">{task.title}</strong>
        </div>
        <div className="tasks-row-pills">
          <span className={`status-pill status-${task.displayTone}`}>{taskStatusLabel(task.status)}</span>
          {task.reviewState && <span className="chip">{task.reviewState.replaceAll('_', ' ')}</span>}
          {task.isFocused && <span className="chip accent">focused</span>}
        </div>
      </div>

      <ProgressStrip stage={task.progressStage} index={task.progressIndex} />

      <div className="tasks-row-meta">
        {task.branch && <span className="tasks-row-branch">⎇ {task.branch}</span>}
        {task.runElapsed && <span>elapsed <strong>{task.runElapsed}</strong></span>}
        <span>tokens <strong>{formatTokenCount(task.runTokens)}</strong></span>
        <span>cost <strong>{formatCost(task.runCost, task.runCurrency)}</strong></span>
        {task.reviewFindingsOpen > 0 && <span className="tasks-findings-count">{task.reviewFindingsOpen} open findings</span>}
      </div>

      {task.latestPacket && (
        <div className="tasks-row-latest-packet">
          <span className="tasks-packet-label">{task.latestPacket.label}</span>
          {task.latestPacket.details && <span className="tasks-packet-details">{task.latestPacket.details}</span>}
          {task.latestPacket.timestamp && <span className="tasks-packet-time">{relativeTimeLabel(task.latestPacket.timestamp)}</span>}
        </div>
      )}

      {task.sessionChips.length > 0 && (
        <div className="tasks-row-sessions">
          {task.sessionChips.map((chip) => (
            <SessionAttachChip key={chip.key} chip={chip} />
          ))}
        </div>
      )}

      {task.worktreePath && <p className="tasks-row-worktree">{task.worktreePath}</p>}
    </article>
  );
}

function ProgressStrip({ stage, index }: { stage: string; index: number }) {
  const stages = ['planned', 'context', 'coder', 'impl', 'validate', 'drift', 'review', 'approved', 'merged'];
  const stageNames = ['Planned', 'Context', 'Coder', 'Impl', 'Validate', 'Drift', 'Review', 'Approved', 'Merged'];
  const currentIndex = Math.min(index, stages.length - 1);

  return (
    <div className="tasks-progress-strip" aria-label={`Progress: ${progressStageLabel(stage as any)}`}>
      {stages.map((s, i) => (
        <div
          key={s}
          className={`tasks-progress-step ${i < currentIndex ? 'completed' : ''} ${i === currentIndex ? 'active' : ''}`}
          title={stageNames[i]}
        >
          <span className="tasks-progress-dot" />
          {i === currentIndex && <span className="tasks-progress-current">{progressStageLabel(stage as any)}</span>}
        </div>
      ))}
    </div>
  );
}

function SessionAttachChip({ chip }: { chip: SessionChipView }) {
  const handleCopy = useCallback(() => {
    if (chip.attachCommand) {
      copyToClipboard(chip.attachCommand);
    }
  }, [chip.attachCommand]);

  return (
    <span className="tasks-session-chip" title={chip.label}>
      <span className="tasks-session-chip-label">{chip.label}</span>
      {chip.backend && <span className="tasks-session-chip-backend">{chip.backend}</span>}
      {chip.canAttach && chip.attachCommand && (
        <button type="button" className="tasks-attach-btn" onClick={handleCopy} title="Copy attach command">
          Copy
        </button>
      )}
    </span>
  );
}

function StatusPanel({ sections }: { sections: StatusPanelSection[] }) {
  if (sections.length === 0) {
    return (
      <div>
        <p className="eyebrow">Task status</p>
        <h3>Status</h3>
        <p className="muted">No status data available.</p>
      </div>
    );
  }

  return (
    <div className="tasks-status-content">
      {sections.map((section, i) => (
        <div key={i} className="tasks-status-section">
          <p className="tasks-status-heading">{section.heading}</p>
          <dl className="tasks-status-list">
            {section.entries.map((entry, j) => (
              <div key={j} className="tasks-status-entry">
                <dt>{entry.label}</dt>
                <dd className={`tasks-status-value tone-${entry.tone}`}>{entry.value}</dd>
              </div>
            ))}
          </dl>
        </div>
      ))}
    </div>
  );
}

function FreshnessPanel({ view }: { view: DashboardView['freshness'] }) {
  if (!view.isStale && view.warnings.length === 0 && view.errors.length === 0) {
    return null;
  }

  return (
    <div className="tasks-freshness-panel">
      {view.isStale && (
        <div className="tasks-freshness-stale">
          <strong>Snapshot may be stale</strong>
          <p>Data source: {view.source}. Refresh to get the latest state.</p>
        </div>
      )}
      {view.warnings.length > 0 && (
        <ul className="warning-list">
          {view.warnings.map((w, i) => <li key={i}>{w}</li>)}
        </ul>
      )}
      {view.errors.length > 0 && (
        <div className="error-note">
          {view.errors.map((e, i) => <p key={i}>{e}</p>)}
        </div>
      )}
    </div>
  );
}

function LaneOverview({ lanes }: { lanes: LaneView[] }) {
  return (
    <div className="tasks-lanes-overview">
      <p className="eyebrow">Lanes</p>
      <div className="tasks-lanes-grid">
        {lanes.map((lane) => (
          <div key={lane.key} className={`tasks-lane-card tasks-lane-${lane.tone}`}>
            <div className="tasks-lane-topline">
              <strong>{lane.label}</strong>
              <span className={`status-pill status-${lane.tone}`}>{lane.state}</span>
              {lane.online && <span className="tasks-lane-online">online</span>}
            </div>
            {lane.role && <span className="tasks-lane-role">{lane.role}</span>}
            {lane.branch && <span className="tasks-lane-branch">⎇ {lane.branch}</span>}
            {lane.worktreePath && <span className="tasks-lane-worktree">{lane.worktreePath}</span>}
            {lane.sessionChips.length > 0 && (
              <div className="tasks-lane-sessions">
                {lane.sessionChips.map((chip) => (
                  <SessionAttachChip key={chip.key} chip={chip} />
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
