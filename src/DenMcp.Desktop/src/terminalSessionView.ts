import type { LocalSessionSnapshot, TerminalSessionSummary } from './desktop/sidecarBridgeApi.ts';
import { recentActivityItems, type RecentActivityItem } from './sessionView.ts';

export type TerminalOverviewAuthority = 'local' | 'observed';
export type TerminalStatusTone = 'ok' | 'running' | 'idle' | 'warn' | 'err' | 'info';
export type TerminalSessionRefreshUrgency = 'immediate' | 'coalesced';
export type TerminalAttachInteractionDecision = 'select_first_explicit_attach';

export interface TerminalSessionRefreshEvent {
  kind: 'status' | 'lifecycle';
  status?: string | null;
  event?: string | null;
}

export interface TerminalOverviewCapabilities {
  canAttach: boolean;
  canStreamTerminal: boolean;
  canDetach: boolean;
  canTerminate: boolean;
  canReconnect: boolean;
  canSendInput: boolean;
  canResize: boolean;
  canReadActivity: boolean;
  canOpenExternalAttach: boolean;
}

export interface TerminalOverviewSession {
  key: string;
  sessionId: string;
  displayName: string;
  projectId: string | null;
  taskId: number | null;
  workspaceId: string | null;
  cwd: string | null;
  kind: string;
  backend: string;
  status: string;
  currentCommand: string | null;
  lastActivityAt: string | null;
  lastObservedAt: string | null;
  sourceInstanceId: string | null;
  sourceDisplayName: string | null;
  authority: TerminalOverviewAuthority;
  capabilities: TerminalOverviewCapabilities;
  capabilityLabels: string[];
  warnings: string[];
  recentActivity: RecentActivityItem[];
  stale: boolean;
  readOnly: boolean;
  statusTone: TerminalStatusTone;
}

const STALE_AFTER_MS = 120_000;

// Product decision for Terminals tab cards: selecting and attaching are separate.
// Single click selects/previews metadata; explicit Attach, Enter, or double-click attaches.
export const TERMINAL_ATTACH_INTERACTION_DECISION: TerminalAttachInteractionDecision = 'select_first_explicit_attach';

// replay_complete is intentionally NOT immediate: the attach flow already triggers
// an immediate session-list refresh when the attach response arrives, so replay_complete
// does not represent a new state boundary. Making it immediate would cause a redundant refresh
// with no UX benefit. See task #1064 for the analysis.
//
// `starting` is intentionally NOT immediate (#1065 analysis):
// - OperatorSession defaults Status to "starting", but every creation path (DirectPty,
//   Tmux, PiArtifact, AppAgent) sets status to Running/Exited/Failed/Stale *before*
//   publishing any session or status event.
// - No current code path emits a status event with `starting` through the terminal
//   event pipeline.
// - Session-list events and the initial list-sessions call already cover new-session
//   discovery; a status event for `starting` cannot appear without a preceding
//   session-list or creation event that already populated the session.
// - Keeping `starting` coalesced is defensive-safe: if it ever did fire, a 750ms delay
//   is acceptable for a transient startup phase that immediately transitions to running.
const IMMEDIATE_REFRESH_LIFECYCLE_EVENTS = new Set(['den.terminal.exit', 'den.terminal.error']);
const IMMEDIATE_REFRESH_STATUSES = new Set(['exited', 'failed', 'crashed', 'detached', 'terminated']);

export function terminalSessionRefreshUrgency(event: TerminalSessionRefreshEvent): TerminalSessionRefreshUrgency {
  if (event.kind === 'lifecycle') {
    return event.event && IMMEDIATE_REFRESH_LIFECYCLE_EVENTS.has(event.event) ? 'immediate' : 'coalesced';
  }

  const status = event.status?.toLowerCase();
  if (status && IMMEDIATE_REFRESH_STATUSES.has(status)) return 'immediate';
  return 'coalesced';
}

export function buildTerminalSessionOverview(
  sessions: TerminalSessionSummary[],
  snapshots: LocalSessionSnapshot[],
  nowMs = Date.now(),
): TerminalOverviewSession[] {
  const byId = new Map<string, TerminalOverviewSession>();

  for (const session of sessions) {
    byId.set(session.session_id, fromSummary(session, nowMs));
  }

  for (const snapshot of snapshots) {
    const existing = byId.get(snapshot.request.session_id);
    if (existing) {
      byId.set(snapshot.request.session_id, mergeSnapshot(existing, snapshot, nowMs));
    } else {
      byId.set(snapshot.request.session_id, fromSnapshot(snapshot, nowMs));
    }
  }

  // Active sessions first (stale === false), then stale sessions, each group sorted newest-first by last activity.
  // This ensures users see active sessions at the top even if a stale session had more recent activity.
  return [...byId.values()].sort((a, b) => {
    if (a.stale !== b.stale) return a.stale ? 1 : -1;
    return timestampMs(b.lastActivityAt ?? b.lastObservedAt) - timestampMs(a.lastActivityAt ?? a.lastObservedAt);
  });
}

export function canAttachInline(session: TerminalOverviewSession): boolean {
  return session.capabilities.canAttach && session.capabilities.canStreamTerminal && !session.readOnly;
}

export function terminalInlineAttachButtonLabel(session: TerminalOverviewSession, attached = false): string {
  if (!canAttachInline(session)) return 'Inline attach unavailable';
  return attached ? 'Reattach inline' : 'Attach inline';
}

export function terminalSessionCardActionHint(session: TerminalOverviewSession): string {
  if (canAttachInline(session)) return 'Single click selects; Attach inline, Enter, or double-click attaches';
  if (session.capabilities.canOpenExternalAttach) return 'Single click selects; External attach shows copy-only attach information';
  return 'Single click selects and previews metadata';
}

/** Brief feedback shown when a non-attachable card is double-clicked.
 *  Returns null for attachable cards (double-click is a valid attach trigger). */
export function terminalNonAttachableDoubleClickHint(session: TerminalOverviewSession): string | null {
  if (canAttachInline(session)) return null;
  if (session.readOnly) return 'Read-only session — attach unavailable';
  if (!session.capabilities.canAttach) return 'Attach not available for this session';
  return 'Inline attach unavailable';
}

export function terminalStatusLabel(status: string | null | undefined): string {
  return (status || 'observed').replaceAll('_', ' ');
}

export function relativeActivityLabel(timestamp: string | null | undefined, nowMs = Date.now()): string {
  if (!timestamp) return 'unknown';
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

function fromSummary(session: TerminalSessionSummary, nowMs: number): TerminalOverviewSession {
  const capabilities = summaryCapabilities(session);
  const lastActivityAt = session.last_activity_at ?? session.last_observed_at ?? session.created_at ?? null;
  const warnings = normalizeWarnings(session.warnings);
  const status = session.status || 'unknown';
  const stale = isStaleStatus(status) || isOlderThan(session.last_observed_at ?? lastActivityAt, nowMs, STALE_AFTER_MS);
  return {
    key: `local::${session.session_id}`,
    sessionId: session.session_id,
    displayName: session.display_name ?? session.title ?? session.session_id,
    projectId: session.project_id ?? null,
    taskId: session.task_id ?? null,
    workspaceId: session.workspace_id ?? null,
    cwd: session.cwd ?? null,
    kind: session.kind ?? 'unknown',
    backend: session.backend ?? 'unknown',
    status,
    currentCommand: session.current_command ?? null,
    lastActivityAt,
    lastObservedAt: session.last_observed_at ?? session.last_activity_at ?? session.created_at ?? null,
    sourceInstanceId: session.source_instance_id ?? null,
    sourceDisplayName: session.source_display_name ?? null,
    authority: 'local',
    capabilities,
    capabilityLabels: capabilityLabels(capabilities),
    warnings,
    recentActivity: [],
    stale,
    readOnly: isReadOnly(session.kind, session.backend, capabilities),
    statusTone: statusTone(status, stale, warnings),
  };
}

function fromSnapshot(snapshot: LocalSessionSnapshot, nowMs: number): TerminalOverviewSession {
  const req = snapshot.request;
  const capabilities = snapshotCapabilities(snapshot);
  const lastActivityAt = req.last_activity_at ?? req.observed_at ?? null;
  const warnings = normalizeWarnings(req.warnings);
  const status = req.status ?? req.current_phase ?? 'observed';
  const stale = isStaleStatus(status) || isOlderThan(req.observed_at, nowMs, STALE_AFTER_MS);
  return {
    key: `${req.source_instance_id}::${req.session_id}`,
    sessionId: req.session_id,
    displayName: req.display_name ?? req.title ?? req.role ?? req.session_id,
    projectId: snapshot.projectId,
    taskId: req.task_id,
    workspaceId: req.workspace_id,
    cwd: req.cwd ?? snapshot.artifactRoot,
    kind: req.kind ?? 'artifact_observer',
    backend: req.backend ?? 'pi_artifact',
    status,
    currentCommand: req.current_command,
    lastActivityAt,
    lastObservedAt: req.observed_at,
    sourceInstanceId: req.source_instance_id,
    sourceDisplayName: req.source_display_name,
    authority: 'observed',
    capabilities,
    capabilityLabels: capabilityLabels(capabilities),
    warnings,
    recentActivity: recentActivityItems(snapshot),
    stale,
    readOnly: true,
    statusTone: statusTone(status, stale, warnings),
  };
}

function mergeSnapshot(base: TerminalOverviewSession, snapshot: LocalSessionSnapshot, nowMs: number): TerminalOverviewSession {
  const fromObserved = fromSnapshot(snapshot, nowMs);
  const warnings = unique([...base.warnings, ...fromObserved.warnings]);
  const recentActivity = fromObserved.recentActivity.length > 0 ? fromObserved.recentActivity : base.recentActivity;
  return {
    ...base,
    projectId: base.projectId ?? fromObserved.projectId,
    taskId: base.taskId ?? fromObserved.taskId,
    workspaceId: base.workspaceId ?? fromObserved.workspaceId,
    cwd: base.cwd ?? fromObserved.cwd,
    currentCommand: base.currentCommand ?? fromObserved.currentCommand,
    lastActivityAt: latestIso(base.lastActivityAt, fromObserved.lastActivityAt),
    lastObservedAt: latestIso(base.lastObservedAt, fromObserved.lastObservedAt),
    sourceInstanceId: base.sourceInstanceId ?? fromObserved.sourceInstanceId,
    sourceDisplayName: base.sourceDisplayName ?? fromObserved.sourceDisplayName,
    warnings,
    recentActivity,
    stale: base.stale || fromObserved.stale,
    statusTone: statusTone(base.status, base.stale || fromObserved.stale, warnings),
  };
}

function summaryCapabilities(session: TerminalSessionSummary): TerminalOverviewCapabilities {
  return {
    canAttach: session.can_attach === true,
    canStreamTerminal: session.can_stream_terminal === true,
    canDetach: session.can_detach === true,
    canTerminate: session.can_terminate === true,
    canReconnect: session.can_reconnect === true,
    canSendInput: session.can_send_input === true,
    canResize: session.can_resize === true,
    canReadActivity: session.can_read_activity === true,
    canOpenExternalAttach: session.can_open_external_attach === true,
  };
}

function snapshotCapabilities(snapshot: LocalSessionSnapshot): TerminalOverviewCapabilities {
  const records = [toRecord(snapshot.request.capabilities), toRecord(snapshot.request.control_capabilities)];
  const bool = (...names: string[]) => records.some((record) => names.some((name) => record[name] === true));
  return {
    canAttach: bool('can_attach'),
    canStreamTerminal: bool('can_stream_terminal', 'can_stream_raw_terminal'),
    canDetach: bool('can_detach'),
    canTerminate: bool('can_terminate', 'can_stop'),
    canReconnect: bool('can_reconnect'),
    canSendInput: bool('can_send_input'),
    canResize: bool('can_resize'),
    canReadActivity: bool('can_read_activity') || recentActivityItems(snapshot).length > 0,
    canOpenExternalAttach: bool('can_open_external_attach', 'can_focus'),
  };
}

function capabilityLabels(capabilities: TerminalOverviewCapabilities): string[] {
  const labels: string[] = [];
  if (capabilities.canAttach && capabilities.canStreamTerminal) labels.push('inline attach');
  else if (capabilities.canAttach) labels.push('attach');
  if (capabilities.canOpenExternalAttach) labels.push('external attach');
  if (capabilities.canReconnect) labels.push('reconnect');
  if (capabilities.canDetach) labels.push('detach');
  if (capabilities.canSendInput) labels.push('input');
  if (capabilities.canResize) labels.push('resize');
  if (capabilities.canTerminate) labels.push('terminate');
  if (capabilities.canReadActivity) labels.push('activity');
  return labels.length > 0 ? labels : ['read-only'];
}

function statusTone(status: string, stale: boolean, warnings: string[]): TerminalStatusTone {
  if (status === 'failed' || status === 'crashed') return 'err';
  if (warnings.length > 0) return 'warn';
  if (stale || status === 'source_offline' || status === 'detached' || status === 'exited') return 'idle';
  if (status === 'running') return 'running';
  if (status === 'idle') return 'idle';
  if (status === 'starting') return 'info';
  return 'ok';
}

function isReadOnly(kind: string | null | undefined, backend: string | null | undefined, capabilities: TerminalOverviewCapabilities): boolean {
  return kind === 'artifact_observer' || backend === 'pi_artifact' || (!capabilities.canAttach && !capabilities.canTerminate && !capabilities.canOpenExternalAttach);
}

function normalizeWarnings(warnings: readonly string[] | null | undefined): string[] {
  return unique((warnings ?? []).filter((warning) => warning.trim().length > 0));
}

function unique(values: string[]): string[] {
  return [...new Set(values)];
}

function toRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
}

function timestampMs(timestamp: string | null | undefined): number {
  if (!timestamp) return 0;
  const parsed = Date.parse(timestamp);
  return Number.isNaN(parsed) ? 0 : parsed;
}

function latestIso(a: string | null, b: string | null): string | null {
  if (!a) return b;
  if (!b) return a;
  return timestampMs(a) >= timestampMs(b) ? a : b;
}

function isOlderThan(timestamp: string | null | undefined, nowMs: number, ageMs: number): boolean {
  const parsed = timestampMs(timestamp);
  return parsed > 0 && nowMs - parsed > ageMs;
}

function isStaleStatus(status: string): boolean {
  return status === 'stale' || status === 'source_offline';
}
