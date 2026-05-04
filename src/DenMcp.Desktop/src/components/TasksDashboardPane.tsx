import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  TasksDashboardSnapshot,
  TasksDashboardTaskRow,
  TaskUpdateResponse,
} from '../electron/sidecarProtocol.ts';
import {
  tasksGetDashboardSnapshot,
  taskUpdate,
  messagesGetSnapshot,
  documentsList,
  type TasksDashboardGetSnapshotRequest,
} from '../desktop/sidecarBridgeApi.ts';
import {
  buildDashboardView,
  copyToClipboard,
  filterTasksByStatus,
  formatCost,
  formatTokenCount,
  priorityLabel,
  priorityTone,
  progressStageLabel,
  PROGRESS_STAGES,
  PROGRESS_STAGE_SHORT_LABELS,
  relativeTimeLabel,
  sortTasks,
  taskStatusLabel,
  truncateText,
  type DashboardView,
  type HeaderView,
  type LaneView,
  type PacketSummary,
  type RecentMessageView,
  type SessionChipView,
  type StatusPanelSection,
  type TaskRowView,
  type TaskSortMode,
  type TaskStatusFilter,
  type WaveView,
} from '../tasksDashboardView.ts';

interface Props {
  projectId: string | null;
  parentTaskId?: number | null;
  /** External filter override from command palette; takes precedence when set. */
  statusFilterOverride?: TaskStatusFilter | null;
  /** Callback to switch to the Messages tab with a task pre-filtered. */
  onNavigateToMessagesTab?: (taskId: number) => void;
  /** Callback to switch to the Docs tab. */
  onNavigateToDocsTab?: () => void;
}

const REFRESH_INTERVAL_MS = 30_000;
const STATUS_FILTERS: TaskStatusFilter[] = ['all', 'in_progress', 'review', 'blocked', 'planned', 'done', 'cancelled'];
const SORT_MODES: TaskSortMode[] = ['priority', 'status', 'id', 'title', 'updated'];

export function TasksDashboardPane({ projectId, parentTaskId, statusFilterOverride, onNavigateToMessagesTab, onNavigateToDocsTab }: Props) {
  const [snapshot, setSnapshot] = useState<TasksDashboardSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [focusedTaskId, setFocusedTaskId] = useState<number | null>(null);
  const [lastRefreshAt, setLastRefreshAt] = useState<string | null>(null);
  const [statusFilter, setStatusFilter] = useState<TaskStatusFilter>('all');
  const [sortMode, setSortMode] = useState<TaskSortMode>('priority');
  const [navigatedParentTaskId, setNavigatedParentTaskId] = useState<number | null>(null);
  const [detailOverlayTaskId, setDetailOverlayTaskId] = useState<number | null>(null);
  const mountedRef = useRef(true);

  // Reset internal drill-down when external parentTaskId changes
  useEffect(() => {
    setNavigatedParentTaskId(null);
  }, [parentTaskId]);

  // Navigation callback for subtask drill-down
  const handleNavigateToParent = useCallback((parentId: number) => {
    setFocusedTaskId(null);
    setNavigatedParentTaskId(parentId);
  }, []);

  // Detail overlay handlers
  const handleOpenDetail = useCallback((taskId: number) => {
    setDetailOverlayTaskId(taskId);
    setFocusedTaskId(taskId);
  }, []);

  const handleCloseDetail = useCallback(() => {
    setDetailOverlayTaskId(null);
  }, []);

  // Navigate to subtask view from overlay: close overlay and drill into parent
  const handleOverlayNavigateSubtask = useCallback((parentId: number) => {
    setDetailOverlayTaskId(null);
    setNavigatedParentTaskId(parentId);
  }, []);

  // Effective parent task: internal drill-down takes precedence over external prop
  const effectiveParentTaskId = navigatedParentTaskId ?? parentTaskId ?? null;

  useEffect(() => { mountedRef.current = true; return () => { mountedRef.current = false; }; }, []);

  const fetchSnapshot = useCallback(async () => {
    if (!projectId) return;
    setLoading(true);
    setError(null);
    try {
      const request: TasksDashboardGetSnapshotRequest = {
        project_id: projectId,
        parent_task_id: navigatedParentTaskId ?? parentTaskId ?? null,
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
  }, [projectId, parentTaskId, navigatedParentTaskId, focusedTaskId]);

  // Initial load and periodic refresh
  useEffect(() => {
    void fetchSnapshot();
    const interval = window.setInterval(() => void fetchSnapshot(), REFRESH_INTERVAL_MS);
    return () => window.clearInterval(interval);
  }, [fetchSnapshot]);

  const view = useMemo(() => buildDashboardView(snapshot, focusedTaskId), [snapshot, focusedTaskId]);

  // Escape key closes the detail overlay
  useEffect(() => {
    if (!detailOverlayTaskId) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        setDetailOverlayTaskId(null);
      }
    };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [detailOverlayTaskId]);

  // Look up the overlay task from view data
  const overlayTask = useMemo(() => {
    if (detailOverlayTaskId == null) return null;
    return view.tasks.find(t => t.id === detailOverlayTaskId) ?? null;
  }, [detailOverlayTaskId, view.tasks]);

  // Parent task record for breadcrumb display (found in the full task list)
  const parentViewTask = useMemo(() => {
    if (effectiveParentTaskId == null || !snapshot) return null;
    return snapshot.tasks.find(t => t.id === effectiveParentTaskId) ?? null;
  }, [effectiveParentTaskId, snapshot]);

  // When command palette overrides the filter, apply it once then clear the override
  const effectiveStatusFilter: TaskStatusFilter = statusFilterOverride ?? statusFilter;

  const displayedTasks = useMemo(() => {
    const filtered = filterTasksByStatus(view.tasks, effectiveStatusFilter);
    return sortTasks(filtered, sortMode);
  }, [view.tasks, effectiveStatusFilter, sortMode]);

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
        parentTaskId={effectiveParentTaskId}
        view={view}
        loading={loading}
        error={error}
        lastRefreshAt={lastRefreshAt}
        onRefresh={() => void fetchSnapshot()}
      />
      {snapshot ? (
        <>
          {effectiveParentTaskId != null && (
            <BreadcrumbBar
              projectId={projectId}
              parentTaskId={effectiveParentTaskId}
              parentTitle={parentViewTask?.title ?? null}
              onNavigateToRoot={() => setNavigatedParentTaskId(null)}
            />
          )}
          <TasksFilterBar
            statusFilter={effectiveStatusFilter}
            sortMode={sortMode}
            onStatusFilterChange={setStatusFilter}
            onSortModeChange={setSortMode}
            filteredCount={displayedTasks.length}
            totalCount={view.tasks.length}
          />
          <WaveStrip waves={view.waves} tasks={view.tasks} />
          <div className="tasks-workbench-grid">
            <div className="tasks-lane-list">
              {displayedTasks.length === 0 ? (
                <div className="empty-state">
                  <strong>No tasks found.</strong>
                  <p>Tasks will appear here once they are created in Den for this project.</p>
                </div>
              ) : displayedTasks.map((task) => (
                <TaskRowCard
                  key={task.id}
                  task={task}
                  focused={task.isFocused}
                  onSelect={() => handleOpenDetail(task.id)}
                  onNavigateToParent={task.subtaskCount > 0 ? () => handleNavigateToParent(task.id) : undefined}
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

      {detailOverlayTaskId != null && overlayTask && (
        <TaskDetailOverlay
          task={overlayTask}
          snapshot={snapshot}
          projectId={projectId}
          onClose={handleCloseDetail}
          onNavigateToSubtask={handleOverlayNavigateSubtask}
          onNavigateToMessagesTab={onNavigateToMessagesTab}
          onNavigateToDocsTab={onNavigateToDocsTab}
          onTaskUpdated={() => void fetchSnapshot()}
        />
      )}
    </section>
  );
}

// ── Sub-components ──────────────────────────────────────────────

function TasksFilterBar({
  statusFilter,
  sortMode,
  onStatusFilterChange,
  onSortModeChange,
  filteredCount,
  totalCount,
}: {
  statusFilter: TaskStatusFilter;
  sortMode: TaskSortMode;
  onStatusFilterChange: (f: TaskStatusFilter) => void;
  onSortModeChange: (m: TaskSortMode) => void;
  filteredCount: number;
  totalCount: number;
}) {
  return (
    <div className="tasks-filter-bar">
      <div className="tasks-filter-status">
        {STATUS_FILTERS.map((f) => (
          <button
            key={f}
            type="button"
            className={`tasks-filter-btn ${f === statusFilter ? 'active' : ''}`}
            onClick={() => onStatusFilterChange(f)}
          >
            {f === 'all' ? 'All' : taskStatusLabel(f)}
          </button>
        ))}
      </div>
      <div className="tasks-filter-sort">
        <label className="tasks-sort-label">Sort:</label>
        <select
          className="tasks-sort-select"
          value={sortMode}
          onChange={(e) => onSortModeChange(e.target.value as TaskSortMode)}
        >
          {SORT_MODES.map((m) => (
            <option key={m} value={m}>{m === 'priority' ? 'Priority' : m === 'status' ? 'Status' : m === 'id' ? 'ID' : m === 'title' ? 'Title' : 'Last updated'}</option>
          ))}
        </select>
      </div>
      <span className="tasks-filter-count">
        {filteredCount === totalCount ? `${totalCount} tasks` : `${filteredCount} of ${totalCount}`}
      </span>
    </div>
  );
}

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

function TaskRowCard({ task, focused, onSelect, onNavigateToParent }: { task: TaskRowView; focused: boolean; onSelect: () => void; onNavigateToParent?: () => void }) {
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
          <span className={`tasks-priority-chip tasks-priority-${task.priority}`}>{priorityLabel(task.priority)}</span>
          {task.reviewState && <span className="chip">{task.reviewState.replaceAll('_', ' ')}</span>}
          {task.isFocused && <span className="chip accent">focused</span>}
        </div>
      </div>

      {/* Assignee + tags + hierarchy row */}
      <div className="tasks-row-attrs">
        {task.assignedTo && <span className="tasks-row-assignee">👤 {task.assignedTo}</span>}
        {task.tags.length > 0 && (
          <span className="tasks-row-tags">
            {task.tags.map((tag) => <span key={tag} className="tasks-tag-chip">{tag}</span>)}
          </span>
        )}
        {task.dependencyCount > 0 && <span className="tasks-row-dep-count">↗ {task.dependencyCount} dep{task.dependencyCount !== 1 ? 's' : ''}</span>}
        {task.subtaskCount > 0 && (
          onNavigateToParent ? (
            <button
              type="button"
              className="tasks-row-sub-count tasks-subnav-btn"
              onClick={(e) => { e.stopPropagation(); onNavigateToParent(); }}
              title={`View ${task.subtaskCount} subtask${task.subtaskCount !== 1 ? 's' : ''} of task #${task.id}`}
            >
              ▾ {task.subtaskCount} sub{task.subtaskCount !== 1 ? 's' : ''}
            </button>
          ) : (
            <span className="tasks-row-sub-count">▾ {task.subtaskCount} sub{task.subtaskCount !== 1 ? 's' : ''}</span>
          )
        )}
        {task.messageCount > 0 && <span className="tasks-row-msg-count">💬 {task.messageCount}</span>}
        {task.createdAt && <span className="tasks-row-created">created {relativeTimeLabel(task.createdAt)}</span>}
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

      {/* Expanded detail section when focused */}
      {focused && <TaskDetailSection task={task} />}
    </article>
  );
}

function TaskDetailSection({ task }: { task: TaskRowView }) {
  return (
    <div className="tasks-detail-section" onClick={(e) => e.stopPropagation()}>
      {/* Description */}
      {task.description && (
        <div className="tasks-detail-block">
          <p className="tasks-detail-heading">Description</p>
          <p className="tasks-detail-description">{truncateText(task.description, 400)}</p>
        </div>
      )}

      {/* Dependencies */}
      {task.dependencyCount > 0 && (
        <div className="tasks-detail-block">
          <p className="tasks-detail-heading">Dependencies</p>
          <p className="tasks-detail-text">{task.dependencyCount} task{task.dependencyCount !== 1 ? 's' : ''} — see Den web for full dependency navigation.</p>
        </div>
      )}

      {/* Subtasks */}
      {task.subtaskCount > 0 && (
        <div className="tasks-detail-block">
          <p className="tasks-detail-heading">Subtasks</p>
          <div className="tasks-detail-subtasks">
            {task.subtaskIds.map((subId) => (
              <span key={subId} className="tasks-detail-subtask-ref">#{subId}</span>
            ))}
            <span className="tasks-detail-text">({task.subtaskCount} subtask{task.subtaskCount !== 1 ? 's' : ''})</span>
          </div>
        </div>
      )}

      {/* Recent messages */}
      {task.recentMessages.length > 0 && (
        <div className="tasks-detail-block">
          <p className="tasks-detail-heading">Recent messages ({task.messageCount})</p>
          <div className="tasks-detail-messages">
            {task.recentMessages.map((msg) => (
              <div key={msg.id} className="tasks-detail-message-row">
                <span className="tasks-detail-msg-sender">{msg.sender}</span>
                {msg.metadataType && <span className="tasks-detail-msg-type">{msg.metadataType.replaceAll('_', ' ')}</span>}
                <span className="tasks-detail-msg-summary">{truncateText(msg.contentSummary, 120)}</span>
                {msg.createdAt && <span className="tasks-detail-msg-time">{relativeTimeLabel(msg.createdAt)}</span>}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Review context */}
      {(task.reviewState || task.reviewFindingsOpen > 0) && (
        <div className="tasks-detail-block">
          <p className="tasks-detail-heading">Review</p>
          <div className="tasks-detail-text">
            {task.reviewState && <span>State: <strong>{task.reviewState.replaceAll('_', ' ')}</strong></span>}
            {task.reviewFindingsOpen > 0 && <span> · {task.reviewFindingsOpen} open finding{task.reviewFindingsOpen !== 1 ? 's' : ''}</span>}
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Full-screen detail overlay for a task, shown when a row is clicked.
 * Provides close affordances (X button, backdrop click, Escape key)
 * and displays all available task information.
 */
function TaskDetailOverlay({
  task,
  snapshot,
  projectId,
  onClose,
  onNavigateToSubtask,
  onNavigateToMessagesTab,
  onNavigateToDocsTab,
  onTaskUpdated,
}: {
  task: TaskRowView;
  snapshot: TasksDashboardSnapshot | null;
  projectId: string | null;
  onClose: () => void;
  onNavigateToSubtask: (parentId: number) => void;
  onNavigateToMessagesTab?: (taskId: number) => void;
  onNavigateToDocsTab?: () => void;
  onTaskUpdated: () => void;
}) {
  const overlayRef = useRef<HTMLDivElement>(null);
  const closeBtnRef = useRef<HTMLButtonElement>(null);
  const [editMode, setEditMode] = useState(false);
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);

  // Edit form state — initialized from task on mount
  const [editTitle, setEditTitle] = useState(task.title);
  const [editDescription, setEditDescription] = useState(task.description ?? '');
  const [editStatus, setEditStatus] = useState(task.status);
  const [editPriority, setEditPriority] = useState(task.priority);
  const [editAssignedTo, setEditAssignedTo] = useState(task.assignedTo ?? '');

  // Reset form when task changes
  useEffect(() => {
    setEditTitle(task.title);
    setEditDescription(task.description ?? '');
    setEditStatus(task.status);
    setEditPriority(task.priority);
    setEditAssignedTo(task.assignedTo ?? '');
    setEditMode(false);
    setSaveError(null);
  }, [task.id, task.title, task.description, task.status, task.priority, task.assignedTo]);

  // Focus close button on mount and when task changes
  useEffect(() => {
    closeBtnRef.current?.focus();
  }, [task.id]);

  // Backdrop click handler: only close if clicking the backdrop itself
  const handleBackdropClick = useCallback((e: React.MouseEvent) => {
    if (e.target === e.currentTarget) {
      if (editMode) {
        setEditMode(false);
        setSaveError(null);
      } else {
        onClose();
      }
    }
  }, [onClose, editMode]);

  const handleEditToggle = useCallback(() => {
    setEditMode(true);
    // Reset form fields to current task values
    setEditTitle(task.title);
    setEditDescription(task.description ?? '');
    setEditStatus(task.status);
    setEditPriority(task.priority);
    setEditAssignedTo(task.assignedTo ?? '');
    setSaveError(null);
  }, [task]);

  const handleCancelEdit = useCallback(() => {
    setEditMode(false);
    setSaveError(null);
    // Reset to original values
    setEditTitle(task.title);
    setEditDescription(task.description ?? '');
    setEditStatus(task.status);
    setEditPriority(task.priority);
    setEditAssignedTo(task.assignedTo ?? '');
  }, [task]);

  const handleSave = useCallback(async () => {
    if (!projectId) return;
    setSaving(true);
    setSaveError(null);
    try {
      await taskUpdate({
        project_id: projectId,
        task_id: task.id,
        agent: 'desktop',
        title: editTitle !== task.title ? editTitle : null,
        description: editDescription !== (task.description ?? '') ? editDescription : null,
        status: editStatus !== task.status ? editStatus : null,
        priority: editPriority !== task.priority ? editPriority : null,
        assigned_to: editAssignedTo !== (task.assignedTo ?? '') ? editAssignedTo || null : null,
      });
      setEditMode(false);
      onTaskUpdated();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }, [projectId, task, editTitle, editDescription, editStatus, editPriority, editAssignedTo, onTaskUpdated]);

  // Build subtask rows from snapshot
  const subtaskRows = useMemo(() => {
    if (!snapshot || task.subtaskIds.length === 0) return [];
    return snapshot.tasks.filter((t) => task.subtaskIds.includes(t.id));
  }, [snapshot, task.subtaskIds]);

  // Docs section: use project docs from a simple fetch
  const [docs, setDocs] = useState<Array<{ slug: string; title: string }> | null>(null);
  const [docsLoading, setDocsLoading] = useState(false);

  useEffect(() => {
    if (!projectId) return;
    let cancelled = false;
    setDocsLoading(true);
    documentsList({ project_id: projectId })
      .then((result) => {
        if (!cancelled) {
          setDocs(result.documents.slice(0, 10));
          setDocsLoading(false);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setDocs([]);
          setDocsLoading(false);
        }
      });
    return () => { cancelled = true; };
  }, [projectId]);

  return (
    <div
      className="task-detail-overlay"
      onClick={handleBackdropClick}
      ref={overlayRef}
    >
      <div
        className="task-detail-panel"
        role="dialog"
        aria-modal="true"
        aria-label={`Task #${task.id} details`}
      >
        {/* ── Header ── */}
        <div className="task-detail-header">
          <div className="task-detail-header-info">
            <div className="task-detail-header-topline">
              <span className="task-detail-header-id">#{task.id}</span>
              <span className={`status-pill status-${task.displayTone}`}>{taskStatusLabel(task.status)}</span>
              <span className={`tasks-priority-chip tasks-priority-${task.priority}`}>{priorityLabel(task.priority)}</span>
              {task.reviewState && <span className="chip">{task.reviewState.replaceAll('_', ' ')}</span>}
            </div>
            {editMode ? (
              <input
                type="text"
                className="task-edit-input task-edit-title-input"
                value={editTitle}
                onChange={(e) => setEditTitle(e.target.value)}
                placeholder="Task title"
                aria-label="Edit title"
              />
            ) : (
              <h3 className="task-detail-header-title">{task.title}</h3>
            )}
          </div>
          <div className="task-detail-header-actions">
            {!editMode ? (
              <button
                type="button"
                className="task-detail-edit-btn"
                onClick={handleEditToggle}
                title="Edit task"
                aria-label="Edit task"
              >
                ✏️ Edit
              </button>
            ) : (
              <>
                <button
                  type="button"
                  className="task-detail-save-btn"
                  onClick={handleSave}
                  disabled={saving}
                  title="Save changes"
                >
                  {saving ? 'Saving…' : '💾 Save'}
                </button>
                <button
                  type="button"
                  className="task-detail-cancel-btn"
                  onClick={handleCancelEdit}
                  disabled={saving}
                  title="Discard changes"
                >
                  ✕ Cancel
                </button>
              </>
            )}
            <button
              ref={closeBtnRef}
              type="button"
              className="task-detail-close-btn"
              onClick={onClose}
              aria-label="Close task details"
              title="Close (Esc)"
            >
              ✕
            </button>
          </div>
        </div>

        {/* Save error */}
        {saveError && (
          <div className="task-detail-save-error">
            <strong>Save failed:</strong> {saveError}
          </div>
        )}

        {/* ── Body ── */}
        <div className="task-detail-body">
          {/* Description */}
          <div className="task-detail-section">
            <h4 className="task-detail-section-heading">Description</h4>
            {editMode ? (
              <textarea
                className="task-edit-textarea"
                value={editDescription}
                onChange={(e) => setEditDescription(e.target.value)}
                placeholder="Task description"
                rows={4}
                aria-label="Edit description"
              />
            ) : (
              task.description && <p className="task-detail-description">{task.description}</p>
            )}
          </div>

          {/* Editable Metadata */}
          <div className="task-detail-section">
            <h4 className="task-detail-section-heading">Metadata</h4>
            {editMode ? (
              <div className="task-edit-grid">
                <div className="task-edit-field">
                  <label className="task-edit-label">Status</label>
                  <select
                    className="task-edit-select"
                    value={editStatus}
                    onChange={(e) => setEditStatus(e.target.value)}
                    aria-label="Edit status"
                  >
                    {['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'].map((s) => (
                      <option key={s} value={s}>{taskStatusLabel(s)}</option>
                    ))}
                  </select>
                </div>
                <div className="task-edit-field">
                  <label className="task-edit-label">Priority</label>
                  <select
                    className="task-edit-select"
                    value={editPriority}
                    onChange={(e) => setEditPriority(Number(e.target.value))}
                    aria-label="Edit priority"
                  >
                    {[1, 2, 3, 4, 5].map((p) => (
                      <option key={p} value={p}>{priorityLabel(p)}</option>
                    ))}
                  </select>
                </div>
                <div className="task-edit-field">
                  <label className="task-edit-label">Assignee</label>
                  <input
                    type="text"
                    className="task-edit-input"
                    value={editAssignedTo}
                    onChange={(e) => setEditAssignedTo(e.target.value)}
                    placeholder="e.g. pi"
                    aria-label="Edit assignee"
                  />
                </div>
                {task.tags.length > 0 && (
                  <div className="task-edit-field">
                    <label className="task-edit-label">Tags</label>
                    <div className="task-edit-tags-readonly">
                      {task.tags.map((tag) => <span key={tag} className="tasks-tag-chip">{tag}</span>)}
                    </div>
                  </div>
                )}
              </div>
            ) : (
              <div className="task-detail-meta-grid">
                {task.assignedTo && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Assignee</span>
                    <span className="task-detail-meta-value">{task.assignedTo}</span>
                  </div>
                )}
                {task.tags.length > 0 && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Tags</span>
                    <span className="task-detail-meta-value">
                      {task.tags.map((tag) => <span key={tag} className="tasks-tag-chip">{tag}</span>)}
                    </span>
                  </div>
                )}
                {task.createdAt && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Created</span>
                    <span className="task-detail-meta-value">{relativeTimeLabel(task.createdAt)}</span>
                  </div>
                )}
                {task.branch && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Branch</span>
                    <span className="task-detail-meta-value task-detail-branch">{task.branch}</span>
                  </div>
                )}
                {task.worktreePath && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Worktree</span>
                    <span className="task-detail-meta-value">{task.worktreePath}</span>
                  </div>
                )}
                {task.parentId != null && (
                  <div className="task-detail-meta-item">
                    <span className="task-detail-meta-label">Parent</span>
                    <button
                      type="button"
                      className="tasks-subnav-btn"
                      onClick={() => onNavigateToSubtask(task.parentId!)}
                      title={`View parent task #${task.parentId}`}
                    >
                      #{task.parentId}
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>

          {/* Progress */}
          <div className="task-detail-section">
            <h4 className="task-detail-section-heading">Progress</h4>
            <ProgressStrip stage={task.progressStage} index={task.progressIndex} />
          </div>

          {/* Run summary */}
          {(task.runElapsed || task.runTokens != null || task.runCost != null) && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Run Summary</h4>
              <div className="task-detail-run-grid">
                {task.runElapsed && (
                  <span className="task-detail-run-item">Elapsed <strong>{task.runElapsed}</strong></span>
                )}
                {task.runTokens != null && (
                  <span className="task-detail-run-item">Tokens <strong>{formatTokenCount(task.runTokens)}</strong></span>
                )}
                {task.runCost != null && (
                  <span className="task-detail-run-item">Cost <strong>{formatCost(task.runCost, task.runCurrency)}</strong></span>
                )}
              </div>
            </div>
          )}

          {/* Dependencies */}
          {task.dependencyCount > 0 && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Dependencies</h4>
              <p className="task-detail-text">{task.dependencyCount} task{task.dependencyCount !== 1 ? 's' : ''} — see Den web for full dependency navigation.</p>
            </div>
          )}

          {/* Subtasks — enhanced with titles */}
          {task.subtaskCount > 0 && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Subtasks ({task.subtaskCount})</h4>
              <div className="task-detail-subtasks-enhanced">
                {subtaskRows.map((sub) => (
                  <button
                    key={sub.id}
                    type="button"
                    className="task-detail-subtask-row"
                    onClick={() => onNavigateToSubtask(sub.id)}
                    title={`Open subtask #${sub.id}: ${sub.title}`}
                  >
                    <span className="task-detail-subtask-id">#{sub.id}</span>
                    <span className="task-detail-subtask-title">{sub.title}</span>
                    <span className={`status-pill tasks-subtask-status status-${sub.computed_state || sub.status}`}>{taskStatusLabel(sub.status)}</span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* Review */}
          {(task.reviewState || task.reviewFindingsOpen > 0) && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Review</h4>
              <p className="task-detail-text">
                {task.reviewState && <span>State: <strong>{task.reviewState.replaceAll('_', ' ')}</strong></span>}
                {task.reviewFindingsOpen > 0 && <span> · {task.reviewFindingsOpen} open finding{task.reviewFindingsOpen !== 1 ? 's' : ''}</span>}
              </p>
            </div>
          )}

          {/* Messages — with click-through */}
          <div className="task-detail-section">
            <h4 className="task-detail-section-heading">Messages ({task.messageCount})</h4>
            {task.recentMessages.length > 0 ? (
              <div className="task-detail-messages-list">
                {task.recentMessages.map((msg) => (
                  <div key={msg.id} className="task-detail-message-row">
                    <span className="task-detail-msg-sender">{msg.sender}</span>
                    {msg.metadataType && <span className="task-detail-msg-type">{msg.metadataType.replaceAll('_', ' ')}</span>}
                    <span className="task-detail-msg-summary">{truncateText(msg.contentSummary, 150)}</span>
                    {msg.createdAt && <span className="task-detail-msg-time">{relativeTimeLabel(msg.createdAt)}</span>}
                  </div>
                ))}
              </div>
            ) : (
              <p className="task-detail-text">No messages yet.</p>
            )}
            {onNavigateToMessagesTab && task.messageCount > 0 && (
              <button
                type="button"
                className="task-detail-nav-btn"
                onClick={() => onNavigateToMessagesTab(task.id)}
              >
                View all messages →
              </button>
            )}
          </div>

          {/* Documents section */}
          <div className="task-detail-section">
            <h4 className="task-detail-section-heading">Documents</h4>
            {docsLoading ? (
              <p className="task-detail-text">Loading documents…</p>
            ) : docs && docs.length > 0 ? (
              <div className="task-detail-docs-list">
                {docs.map((doc) => (
                  <div key={doc.slug} className="task-detail-doc-row">
                    <span className="task-detail-doc-title">{doc.title}</span>
                    <span className="task-detail-doc-slug">{doc.slug}</span>
                  </div>
                ))}
              </div>
            ) : (
              <p className="task-detail-text">No project documents found.</p>
            )}
            {onNavigateToDocsTab && (
              <button
                type="button"
                className="task-detail-nav-btn"
                onClick={() => onNavigateToDocsTab()}
              >
                Browse documents →
              </button>
            )}
          </div>

          {/* Session chips */}
          {task.sessionChips.length > 0 && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Sessions</h4>
              <div className="task-detail-sessions">
                {task.sessionChips.map((chip) => (
                  <SessionAttachChip key={chip.key} chip={chip} />
                ))}
              </div>
            </div>
          )}

          {/* Latest packet */}
          {task.latestPacket && (
            <div className="task-detail-section">
              <h4 className="task-detail-section-heading">Latest Packet</h4>
              <p className="task-detail-text">
                <strong>{task.latestPacket.label}</strong>
                {task.latestPacket.details && <span> — {task.latestPacket.details}</span>}
                {task.latestPacket.timestamp && <span> ({relativeTimeLabel(task.latestPacket.timestamp)})</span>}
              </p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function ProgressStrip({ stage, index }: { stage: string; index: number }) {
  const stages = PROGRESS_STAGES;
  const currentIndex = Math.min(index, stages.length - 1);
  const isDone = stage === 'done';

  return (
    <div className="tasks-progress-strip" aria-label={`Progress: ${progressStageLabel(stage as any)}`}>
      {stages.map((s, i) => (
        <div
          key={s}
          className={`tasks-progress-step ${i < currentIndex || isDone ? 'completed' : ''} ${i === currentIndex && !isDone ? 'active' : ''}`}
          title={PROGRESS_STAGE_SHORT_LABELS[s]}
        >
          <span className="tasks-progress-dot" />
          {i === currentIndex && !isDone && <span className="tasks-progress-current">{progressStageLabel(stage as any)}</span>}
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

function BreadcrumbBar({
  projectId,
  parentTaskId,
  parentTitle,
  onNavigateToRoot,
}: {
  projectId: string;
  parentTaskId: number;
  parentTitle: string | null;
  onNavigateToRoot: () => void;
}) {
  return (
    <nav className="tasks-breadcrumb-bar" aria-label="Task navigation">
      <button
        type="button"
        className="tasks-breadcrumb-back"
        onClick={onNavigateToRoot}
        title="Back to all tasks"
      >
        ← All tasks
      </button>
      <span className="tasks-breadcrumb-separator" aria-hidden="true">/</span>
      <span className="tasks-breadcrumb-current">
        #{parentTaskId}{parentTitle ? ` ${parentTitle}` : ''}
      </span>
    </nav>
  );
}
