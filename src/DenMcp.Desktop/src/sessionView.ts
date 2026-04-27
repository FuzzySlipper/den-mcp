import type { LocalSessionSnapshot } from './desktop/tauriApi';

export interface RecentActivityItem {
  kind?: string;
  role?: string;
  tool?: string;
  summary?: string;
  timestamp?: string | null;
}

export function sessionKey(snapshot: LocalSessionSnapshot): string {
  return [
    snapshot.projectId,
    snapshot.request.workspace_id ?? 'project',
    snapshot.request.task_id ?? 'none',
    snapshot.request.session_id,
  ].join('::');
}

export function sessionTitle(snapshot: LocalSessionSnapshot): string {
  const task = snapshot.request.task_id ? `task #${snapshot.request.task_id}` : snapshot.projectId;
  return `${task} · ${snapshot.request.role ?? 'pi'} · ${snapshot.request.session_id.slice(0, 12)}`;
}

export function phaseLabel(phase: string | null): string {
  if (!phase) return 'observed';
  switch (phase) {
    case 'complete': return 'complete';
    case 'running': return 'running';
    case 'failed': return 'attention needed';
    case 'cancelled': return 'cancelled';
    default: return phase.replaceAll('_', ' ');
  }
}

export function isSessionIdle(snapshot: LocalSessionSnapshot, nowMs = Date.now(), staleAfterMs = 120_000): boolean {
  const observed = Date.parse(snapshot.request.observed_at);
  if (Number.isNaN(observed)) return true;
  return nowMs - observed > staleAfterMs;
}

export function recentActivityItems(snapshot: LocalSessionSnapshot): RecentActivityItem[] {
  const value = snapshot.request.recent_activity;
  if (!value || typeof value !== 'object' || !('items' in value)) return [];
  const items = (value as { items?: unknown }).items;
  return Array.isArray(items) ? items.filter(isActivityItem) : [];
}

export function capabilitySummary(snapshot: LocalSessionSnapshot): string {
  const value = snapshot.request.control_capabilities;
  if (!value || typeof value !== 'object') return 'observation only';
  const caps = value as Record<string, unknown>;
  const enabled = [
    ['focus', caps.can_focus],
    ['raw stream', caps.can_stream_raw_terminal],
    ['input', caps.can_send_input],
    ['stop', caps.can_stop],
    ['launch', caps.can_launch_managed_session],
  ].filter(([, enabled]) => enabled === true).map(([name]) => name);
  return enabled.length > 0 ? enabled.join(', ') : 'observation only';
}

function isActivityItem(value: unknown): value is RecentActivityItem {
  return !!value && typeof value === 'object' && ('summary' in value || 'tool' in value || 'kind' in value);
}
