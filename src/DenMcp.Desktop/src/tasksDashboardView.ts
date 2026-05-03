/**
 * Pure view-model helpers for the Tasks/orchestrator dashboard.
 *
 * Transforms TasksDashboardSnapshot from the sidecar bridge into
 * display-ready structures for the renderer. No React or bridge calls here.
 */

import type {
  TasksDashboardSnapshot,
  TasksDashboardTaskRow,
  TasksDashboardWave,
  TasksDashboardLane,
  TasksDashboardHeader,
  TasksDashboardFreshness,
} from './electron/sidecarProtocol.ts';

// ── Display types ──────────────────────────────────────────────

export type TaskDisplayState =
  | 'planned'
  | 'in_progress'
  | 'review'
  | 'blocked'
  | 'done'
  | 'cancelled'
  | 'needs_attention'
  | 'unknown';

export type TaskDisplayTone = 'ok' | 'running' | 'idle' | 'warn' | 'err' | 'info' | 'accent';

export type ProgressStage =
  | 'planned'
  | 'context_prepared'
  | 'coder_running'
  | 'implementation_posted'
  | 'validation_passed'
  | 'drift_check_complete'
  | 'review_requested'
  | 'approved'
  | 'changes_requested'
  | 'merged'
  | 'done';

export interface PacketSummary {
  type: string;
  label: string;
  stage: ProgressStage;
  timestamp?: string | null;
  details?: string | null;
}

export interface TaskRowView {
  id: number;
  title: string;
  status: string;
  displayState: TaskDisplayState;
  displayTone: TaskDisplayTone;
  progressStage: ProgressStage;
  progressStageLabel: string;
  progressIndex: number;
  packets: PacketSummary[];
  latestPacket: PacketSummary | null;
  reviewState: string | null;
  reviewFindingsOpen: number;
  runElapsed: string | null;
  runTokens: number | null;
  runCost: number | null;
  runCurrency: string | null;
  branch: string | null;
  worktreePath: string | null;
  sessionChips: SessionChipView[];
  dependencies: number[];
  isFocused: boolean;
  priority: number;
  assignedTo: string | null;
  tags: string[];
  description: string;
  messageCount: number;
  recentMessages: RecentMessageView[];
  dependencyCount: number;
  subtaskCount: number;
  subtaskIds: number[];
  parentId: number | null;
  createdAt: string | null;
}

export interface RecentMessageView {
  id: number;
  sender: string;
  intent: string | null;
  metadataType: string | null;
  contentSummary: string;
  createdAt: string | null;
}

export interface SessionChipView {
  key: string;
  label: string;
  backend: string | null;
  canAttach: boolean;
  attachCommand: string | null;
}

export interface WaveView {
  index: number;
  label: string;
  state: string;
  tone: TaskDisplayTone;
  taskIds: number[];
  summary: string | null;
}

export interface LaneView {
  key: string;
  label: string;
  role: string | null;
  state: string;
  tone: TaskDisplayTone;
  branch: string | null;
  worktreePath: string | null;
  taskIds: number[];
  online: boolean;
  sessionChips: SessionChipView[];
}

export interface HeaderView {
  state: string;
  stateLabel: string;
  stateTone: TaskDisplayTone;
  taskCount: number;
  doneCount: number;
  activeCount: number;
  reviewCount: number;
  blockedCount: number;
  completionPercent: number;
  totalTokens: number | null;
  totalCost: number | null;
  currency: string | null;
  lastUpdatedAt: string | null;
  lastUpdatedLabel: string | null;
}

export interface FreshnessView {
  isPartial: boolean;
  warnings: string[];
  errors: string[];
  source: string;
  isStale: boolean;
}

export interface DashboardView {
  header: HeaderView;
  tasks: TaskRowView[];
  waves: WaveView[];
  lanes: LaneView[];
  freshness: FreshnessView;
  statusPanel: StatusPanelSection[];
}

export interface StatusPanelSection {
  heading: string;
  entries: StatusPanelEntry[];
}

export interface StatusPanelEntry {
  label: string;
  value: string;
  tone: TaskDisplayTone;
}

export type TaskStatusFilter = 'all' | 'planned' | 'in_progress' | 'review' | 'blocked' | 'done' | 'cancelled';
export type TaskSortMode = 'priority' | 'status' | 'id' | 'title' | 'updated';

const STATUS_ORDER: Record<string, number> = {
  in_progress: 0,
  review: 1,
  blocked: 2,
  planned: 3,
  done: 4,
  cancelled: 5,
};

// ── Constants ──────────────────────────────────────────────────

export const PROGRESS_STAGES: readonly ProgressStage[] = [
  'planned',
  'context_prepared',
  'coder_running',
  'implementation_posted',
  'validation_passed',
  'drift_check_complete',
  'review_requested',
  'changes_requested',
  'approved',
  'merged',
  'done',
];

const PROGRESS_STAGE_LABELS: Record<ProgressStage, string> = {
  planned: 'Planned',
  context_prepared: 'Context prepared',
  coder_running: 'Coder running',
  implementation_posted: 'Implementation posted',
  validation_passed: 'Validation passed',
  drift_check_complete: 'Drift check complete',
  review_requested: 'Review requested',
  changes_requested: 'Changes requested',
  approved: 'Approved',
  merged: 'Merged',
  done: 'Done',
};

/** Short labels for progress strip dot rendering (one word per stage). */
export const PROGRESS_STAGE_SHORT_LABELS: Record<ProgressStage, string> = {
  planned: 'Planned',
  context_prepared: 'Context',
  coder_running: 'Coder',
  implementation_posted: 'Impl',
  validation_passed: 'Validate',
  drift_check_complete: 'Drift',
  review_requested: 'Review',
  changes_requested: 'Changes',
  approved: 'Approved',
  merged: 'Merged',
  done: 'Done',
};

const PACKET_TYPE_TO_STAGE: Record<string, ProgressStage> = {
  coder_context_packet: 'context_prepared',
  implementation_packet: 'implementation_posted',
  validation_packet: 'validation_passed',
  drift_check_packet: 'drift_check_complete',
  review_request_packet: 'review_requested',
  rereview_packet: 'review_requested',
  review_findings_packet: 'changes_requested',
  merge_summary: 'merged',
};

const PACKET_TYPE_LABELS: Record<string, string> = {
  coder_context_packet: 'Context prepared',
  implementation_packet: 'Implementation posted',
  validation_packet: 'Validation completed',
  drift_check_packet: 'Drift check',
  review_request_packet: 'Review requested',
  rereview_packet: 'Rereview requested',
  review_findings_packet: 'Review findings',
  merge_summary: 'Merged',
  planning_summary: 'Planning summary',
};

const STALE_FRESHNESS_MS = 120_000;

// ── Public functions ───────────────────────────────────────────

export function buildDashboardView(
  snapshot: TasksDashboardSnapshot | null,
  focusedTaskId?: number | null,
  nowMs = Date.now(),
): DashboardView {
  if (!snapshot) {
    return emptyDashboardView();
  }

  const focusedSet = new Set<number>();
  if (focusedTaskId != null) focusedSet.add(focusedTaskId);
  if (snapshot.focused_task_id != null) focusedSet.add(snapshot.focused_task_id);

  const tasks = snapshot.tasks.map((task) => buildTaskRowView(task, focusedSet, snapshot));
  const waves = snapshot.waves.map(buildWaveView);
  const lanes = snapshot.lanes.map((lane) => buildLaneView(lane, snapshot));
  const header = buildHeaderView(snapshot.header, tasks, snapshot.generated_at, nowMs);
  const freshness = buildFreshnessView(snapshot.freshness, snapshot.generated_at, nowMs);
  const statusPanel = buildStatusPanel(snapshot, focusedTaskId);

  return { header, tasks, waves, lanes, freshness, statusPanel };
}

export function taskDisplayState(status: string, computedState?: string): TaskDisplayState {
  const source = computedState || status;
  const normalized = source.toLowerCase().trim();
  if (normalized === 'done' || normalized === 'merged' || normalized === 'complete') return 'done';
  if (normalized === 'cancelled' || normalized === 'canceled') return 'cancelled';
  if (normalized === 'blocked') return 'blocked';
  if (normalized === 'review' || normalized === 'in_review') return 'review';
  if (normalized === 'in_progress' || normalized === 'running' || normalized === 'active' || normalized === 'coder_running' || normalized === 'implementation_posted' || normalized === 'validation_passed' || normalized === 'drift_check_complete' || normalized === 'context_prepared') return 'in_progress';
  if (normalized === 'planned' || normalized === 'queued' || normalized === 'open' || normalized === 'new') return 'planned';
  if (normalized === 'needs_attention' || normalized === 'error' || normalized === 'failed') return 'needs_attention';
  return 'unknown';
}

export function taskDisplayTone(state: TaskDisplayState): TaskDisplayTone {
  switch (state) {
    case 'done': return 'ok';
    case 'in_progress': return 'running';
    case 'review': return 'accent';
    case 'blocked': return 'err';
    case 'needs_attention': return 'warn';
    case 'cancelled': return 'idle';
    case 'planned': return 'info';
    default: return 'idle';
  }
}

export function taskStatusLabel(status: string): string {
  return status.replaceAll('_', ' ');
}

export function progressStageIndex(stage: ProgressStage): number {
  const index = PROGRESS_STAGES.indexOf(stage);
  return index >= 0 ? index : 0;
}

export function progressStageLabel(stage: ProgressStage): string {
  return PROGRESS_STAGE_LABELS[stage] ?? stage;
}

export function relativeTimeLabel(timestamp: string | null | undefined, nowMs = Date.now()): string {
  if (!timestamp) return '—';
  const parsed = Date.parse(timestamp);
  if (Number.isNaN(parsed)) return timestamp;
  const seconds = Math.max(0, Math.round((nowMs - parsed) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
}

export function formatTokenCount(tokens: number | null | undefined): string {
  if (tokens == null) return '—';
  if (tokens < 1000) return String(tokens);
  if (tokens < 1_000_000) return `${(tokens / 1000).toFixed(1)}k`;
  return `${(tokens / 1_000_000).toFixed(1)}M`;
}

export function formatCost(cost: number | null | undefined, currency?: string | null): string {
  if (cost == null) return '—';
  const prefix = currency && currency !== 'USD' ? `${currency} ` : '$';
  if (cost > 0 && cost < 0.01) return `${prefix}${cost.toFixed(4)}`;
  return `${prefix}${cost.toFixed(2)}`;
}

export function waveDisplayTone(state: string): TaskDisplayTone {
  const normalized = state.toLowerCase();
  if (normalized === 'done' || normalized === 'complete') return 'ok';
  if (normalized === 'running' || normalized === 'active') return 'running';
  if (normalized === 'review' || normalized === 'in_review') return 'accent';
  if (normalized === 'blocked' || normalized === 'needs_attention') return 'warn';
  if (normalized === 'queued') return 'info';
  return 'idle';
}

export function laneOnline(lane: TasksDashboardLane): boolean {
  return lane.state === 'running' || lane.state === 'active';
}

export function extractPacketSummaries(packets: Array<Record<string, unknown>>): PacketSummary[] {
  return packets
    .map(extractSinglePacket)
    .filter((p): p is PacketSummary => p != null)
    .sort((a, b) => timestampMs(b.timestamp) - timestampMs(a.timestamp));
}

export function deriveProgressStage(packets: PacketSummary[], taskStatus: string): ProgressStage {
  if (taskStatus === 'done') return 'done';
  if (taskStatus === 'cancelled') return 'planned';

  const best = packets.reduce<ProgressStage>((bestStage, packet) => {
    const packetIndex = progressStageIndex(packet.stage);
    const bestIndex = progressStageIndex(bestStage);
    return packetIndex > bestIndex ? packet.stage : bestStage;
  }, 'planned');

  return best;
}

export function buildSessionChipView(chip: Record<string, unknown>): SessionChipView {
  const key = String(chip.session_id ?? chip.key ?? 'unknown');
  const label = String(chip.display_name ?? chip.label ?? chip.session_id ?? key);
  const backend = typeof chip.backend === 'string' ? chip.backend : null;
  const canAttach = chip.can_open_external_attach === true || chip.can_attach === true;
  const attachCommand = typeof chip.external_attach_command === 'string' ? chip.external_attach_command : null;
  return { key, label, backend, canAttach, attachCommand };
}

export function copyToClipboard(text: string): void {
  if (typeof navigator !== 'undefined' && navigator.clipboard) {
    void navigator.clipboard.writeText(text);
  }
}

export function filterTasksByStatus(tasks: TaskRowView[], filter: TaskStatusFilter): TaskRowView[] {
  if (filter === 'all') return tasks;
  return tasks.filter((task) => task.status === filter);
}

export function sortTasks(tasks: TaskRowView[], mode: TaskSortMode): TaskRowView[] {
  const sorted = [...tasks];
  switch (mode) {
    case 'priority':
      sorted.sort((a, b) => a.priority - b.priority || a.id - b.id);
      break;
    case 'status':
      sorted.sort((a, b) => (STATUS_ORDER[a.status] ?? 99) - (STATUS_ORDER[b.status] ?? 99) || a.priority - b.priority || a.id - b.id);
      break;
    case 'id':
      sorted.sort((a, b) => a.id - b.id);
      break;
    case 'title':
      sorted.sort((a, b) => a.title.localeCompare(b.title));
      break;
    case 'updated':
      sorted.sort((a, b) => {
        const ta = a.latestPacket?.timestamp ?? '';
        const tb = b.latestPacket?.timestamp ?? '';
        return tb.localeCompare(ta) || a.id - b.id;
      });
      break;
    default:
      sorted.sort((a, b) => a.priority - b.priority || a.id - b.id);
  }
  return sorted;
}

export function priorityLabel(priority: number): string {
  if (priority === 1) return 'P1 !!';
  if (priority === 2) return 'P2 !';
  if (priority === 3) return 'P3';
  if (priority === 4) return 'P4';
  return `P${priority}`;
}

export function priorityTone(priority: number): TaskDisplayTone {
  if (priority <= 1) return 'err';
  if (priority === 2) return 'warn';
  if (priority === 3) return 'info';
  return 'idle';
}

export function truncateText(text: string, maxChars: number): string {
  if (text.length <= maxChars) return text;
  return text.slice(0, maxChars) + '…';
}

// ── Internal helpers ───────────────────────────────────────────

function emptyDashboardView(): DashboardView {
  return {
    header: {
      state: 'unknown',
      stateLabel: 'No snapshot loaded',
      stateTone: 'idle',
      taskCount: 0,
      doneCount: 0,
      activeCount: 0,
      reviewCount: 0,
      blockedCount: 0,
      completionPercent: 0,
      totalTokens: null,
      totalCost: null,
      currency: null,
      lastUpdatedAt: null,
      lastUpdatedLabel: null,
    },
    tasks: [],
    waves: [],
    lanes: [],
    freshness: { isPartial: false, warnings: [], errors: [], source: 'none', isStale: false },
    statusPanel: [],
  };
}

function buildHeaderView(
  header: TasksDashboardHeader,
  tasks: TaskRowView[],
  generatedAt: string,
  nowMs: number,
): HeaderView {
  const stateTone = headerStateTone(header.state);
  return {
    state: header.state,
    stateLabel: taskStatusLabel(header.state),
    stateTone,
    taskCount: header.task_count,
    doneCount: header.done_count ?? tasks.filter((t) => t.displayState === 'done').length,
    activeCount: header.active_count ?? tasks.filter((t) => t.displayState === 'in_progress').length,
    reviewCount: header.review_count ?? tasks.filter((t) => t.displayState === 'review').length,
    blockedCount: header.blocked_count ?? tasks.filter((t) => t.displayState === 'blocked').length,
    completionPercent: header.completion_percent,
    totalTokens: header.total_tokens ?? null,
    totalCost: header.total_cost ?? null,
    currency: header.currency ?? null,
    lastUpdatedAt: header.last_updated_at ?? generatedAt,
    lastUpdatedLabel: relativeTimeLabel(header.last_updated_at ?? generatedAt, nowMs),
  };
}

function buildTaskRowView(
  row: TasksDashboardTaskRow,
  focusedSet: Set<number>,
  snapshot: TasksDashboardSnapshot,
): TaskRowView {
  const packets = extractPacketSummaries(row.packets);
  const display = taskDisplayState(row.status, row.computed_state);
  const tone = taskDisplayTone(display);
  const progressStage = deriveProgressStage(packets, row.status);

  const reviewState = typeof row.review?.state === 'string' ? row.review.state : null;
  const reviewFindingsOpen = typeof row.review?.open_findings === 'number' ? row.review.open_findings : 0;

  const runSummary = row.run_summary;
  const runElapsed = typeof runSummary?.elapsed === 'string' ? runSummary.elapsed : null;
  const runTokens = typeof runSummary?.total_tokens === 'number' ? runSummary.total_tokens : null;
  const runCost = typeof runSummary?.total_cost === 'number' ? runSummary.total_cost : null;
  const runCurrency = typeof runSummary?.currency === 'string' ? runSummary.currency : null;

  const branch = typeof runSummary?.branch === 'string' ? runSummary.branch : null;
  const worktreePath = typeof runSummary?.worktree_path === 'string' ? runSummary.worktree_path : null;

  const sessionChips = (row.session_chips ?? []).map(buildSessionChipView);
  const dependencies = extractDependencyIds(row.dependencies);

  const priority = typeof row.priority === 'number' ? row.priority : 3;
  const assignedTo = typeof row.assigned_to === 'string' ? row.assigned_to : null;
  const tags = Array.isArray(row.tags) ? row.tags.filter((t): t is string => typeof t === 'string') : [];
  const description = typeof row.description === 'string' ? row.description : '';
  const messageCount = typeof row.message_count === 'number' ? row.message_count : 0;
  const recentMessages = Array.isArray(row.recent_messages) ? row.recent_messages.map(buildRecentMessageView) : [];
  const dependencyCount = typeof row.dependency_count === 'number' ? row.dependency_count : dependencies.length;
  const subtaskCount = typeof row.subtask_count === 'number' ? row.subtask_count : 0;
  const subtaskIds = Array.isArray(row.subtask_ids) ? row.subtask_ids.filter((n): n is number => typeof n === 'number') : [];
  const parentId = typeof row.parent_id === 'number' ? row.parent_id : null;
  const createdAt = typeof row.created_at === 'string' ? row.created_at : null;

  return {
    id: row.id,
    title: row.title,
    status: row.status,
    displayState: display,
    displayTone: tone,
    progressStage,
    progressStageLabel: progressStageLabel(progressStage),
    progressIndex: progressStageIndex(progressStage),
    packets,
    latestPacket: packets[0] ?? null,
    reviewState,
    reviewFindingsOpen,
    runElapsed,
    runTokens,
    runCost,
    runCurrency,
    branch,
    worktreePath,
    sessionChips,
    dependencies,
    isFocused: focusedSet.has(row.id),
    priority,
    assignedTo,
    tags,
    description,
    messageCount,
    recentMessages,
    dependencyCount,
    subtaskCount,
    subtaskIds,
    parentId,
    createdAt,
  };
}

function buildWaveView(wave: TasksDashboardWave): WaveView {
  return {
    index: wave.index,
    label: wave.label,
    state: wave.state,
    tone: waveDisplayTone(wave.state),
    taskIds: wave.task_ids,
    summary: wave.summary ?? null,
  };
}

function buildLaneView(lane: TasksDashboardLane, snapshot: TasksDashboardSnapshot): LaneView {
  const taskIds = snapshot.tasks
    .filter((t) => t.id === lane.task_id)
    .map((t) => t.id);

  return {
    key: lane.lane_key,
    label: lane.label,
    role: lane.role ?? null,
    state: lane.state,
    tone: waveDisplayTone(lane.state),
    branch: lane.branch ?? null,
    worktreePath: lane.worktree_path ?? null,
    taskIds,
    online: laneOnline(lane),
    sessionChips: (lane.session_chips ?? []).map(buildSessionChipView),
  };
}

function buildFreshnessView(
  freshness: TasksDashboardFreshness,
  generatedAt: string,
  nowMs: number,
): FreshnessView {
  const generatedMs = timestampMs(generatedAt);
  const isStale = generatedMs > 0 && (nowMs - generatedMs) > STALE_FRESHNESS_MS;

  return {
    isPartial: freshness.is_partial,
    warnings: freshness.warnings ?? [],
    errors: freshness.errors ?? [],
    source: freshness.source,
    isStale: isStale || freshness.is_partial,
  };
}

function buildStatusPanel(
  snapshot: TasksDashboardSnapshot,
  focusedTaskId?: number | null,
): StatusPanelSection[] {
  const sections: StatusPanelSection[] = [];

  // Aggregate section
  const headerSection: StatusPanelSection = {
    heading: 'Run overview',
    entries: [
      { label: 'Tasks', value: String(snapshot.header.task_count), tone: 'info' },
      { label: 'Done', value: String(snapshot.header.done_count ?? 0), tone: 'ok' },
      { label: 'Active', value: String(snapshot.header.active_count ?? 0), tone: 'running' },
      { label: 'Review', value: String(snapshot.header.review_count ?? 0), tone: 'accent' },
      { label: 'Blocked', value: String(snapshot.header.blocked_count ?? 0), tone: 'err' },
      { label: 'Completion', value: `${snapshot.header.completion_percent}%`, tone: snapshot.header.completion_percent >= 100 ? 'ok' : 'info' },
    ],
  };
  sections.push(headerSection);

  // Focused task or per-task packet summary
  const targetTaskId = focusedTaskId ?? snapshot.focused_task_id;
  const targetTask = targetTaskId != null ? snapshot.tasks.find((t) => t.id === targetTaskId) : null;

  if (targetTask) {
    const packets = extractPacketSummaries(targetTask.packets);
    const entries: StatusPanelEntry[] = [
      { label: 'Status', value: taskStatusLabel(targetTask.status), tone: taskDisplayTone(taskDisplayState(targetTask.status)) },
    ];
    if (targetTask.review && typeof targetTask.review.state === 'string') {
      entries.push({ label: 'Review', value: targetTask.review.state.replaceAll('_', ' '), tone: reviewTone(targetTask.review.state) });
    }
    for (const packet of packets.slice(0, 4)) {
      entries.push({ label: packet.label, value: packet.details ?? progressStageLabel(packet.stage), tone: packetTone(packet.type) });
    }

    sections.push({ heading: `Task #${targetTask.id} · ${targetTask.title}`, entries });
  }

  // Waves section
  if (snapshot.waves.length > 0) {
    const waveEntries: StatusPanelEntry[] = snapshot.waves.map((wave) => ({
      label: wave.label,
      value: `${wave.state} · ${wave.task_ids.length} tasks`,
      tone: waveDisplayTone(wave.state),
    }));
    sections.push({ heading: 'Waves', entries: waveEntries });
  }

  return sections;
}

function extractSinglePacket(record: Record<string, unknown>): PacketSummary | null {
  const type = typeof record.type === 'string' ? record.type : typeof record.metadata_type === 'string' ? record.metadata_type : null;
  if (!type) return null;

  const stage = PACKET_TYPE_TO_STAGE[type] ?? 'planned';
  const label = PACKET_TYPE_LABELS[type] ?? type.replaceAll('_', ' ');
  const timestamp = typeof record.timestamp === 'string' ? record.timestamp : typeof record.created_at === 'string' ? record.created_at : null;
  const details = extractPacketDetails(record);

  return { type, label, stage, timestamp, details };
}

function extractPacketDetails(record: Record<string, unknown>): string | null {
  if (typeof record.summary === 'string') return record.summary;
  if (typeof record.verdict === 'string') return record.verdict;
  if (typeof record.severity === 'string') return `severity: ${record.severity}`;
  if (typeof record.recommendation === 'string') return record.recommendation;
  return null;
}

function extractDependencyIds(deps: Array<Record<string, unknown>>): number[] {
  return deps
    .map((d) => typeof d.task_id === 'number' ? d.task_id : typeof d.depends_on === 'number' ? d.depends_on : null)
    .filter((id): id is number => id != null);
}

function buildRecentMessageView(record: { id?: number; sender?: string; intent?: string | null; metadata_type?: string | null; content_summary?: string; created_at?: string | null }): RecentMessageView {
  return {
    id: typeof record.id === 'number' ? record.id : 0,
    sender: typeof record.sender === 'string' ? record.sender : '',
    intent: typeof record.intent === 'string' ? record.intent : null,
    metadataType: typeof record.metadata_type === 'string' ? record.metadata_type : null,
    contentSummary: typeof record.content_summary === 'string' ? record.content_summary : '',
    createdAt: typeof record.created_at === 'string' ? record.created_at : null,
  };
}

function headerStateTone(state: string): TaskDisplayTone {
  const normalized = state.toLowerCase();
  if (normalized === 'done' || normalized === 'complete') return 'ok';
  if (normalized === 'running' || normalized === 'active') return 'running';
  if (normalized === 'blocked' || normalized === 'error') return 'err';
  if (normalized === 'needs_attention') return 'warn';
  if (normalized === 'review') return 'accent';
  return 'info';
}

function reviewTone(state: string): TaskDisplayTone {
  const normalized = state.toLowerCase();
  if (normalized === 'looks_good' || normalized === 'approved') return 'ok';
  if (normalized === 'changes_requested') return 'warn';
  if (normalized === 'blocked' || normalized === 'blocked_by_dependency') return 'err';
  return 'info';
}

function packetTone(type: string): TaskDisplayTone {
  if (type === 'merge_summary') return 'ok';
  if (type === 'drift_check_packet') return 'warn';
  if (type === 'review_findings_packet') return 'accent';
  if (type === 'validation_packet') return 'info';
  return 'idle';
}

function timestampMs(timestamp: string | null | undefined): number {
  if (!timestamp) return 0;
  const parsed = Date.parse(timestamp);
  return Number.isNaN(parsed) ? 0 : parsed;
}
