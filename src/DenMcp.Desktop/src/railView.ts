import type { LocalGitSnapshot } from './desktop/sidecarBridgeApi';

export interface ProjectRailRow {
  id: string;
  name: string;
  subtitle: string;
  delta: string;
  state: string;
  active: boolean;
  workspaceCount: number;
}

export interface WorkspaceRailRow {
  snapshotKey: string;
  projectId: string;
  workspaceId: string | null;
  taskId: number | null;
  branch: string | null;
  rootPath: string;
  dirty: number;
  state: string;
}

/**
 * Build project rail rows from a flat snapshot list.
 * Each project becomes one row; workspace count is rolled up.
 * The active project is determined by `activeProjectId`, falling back
 * to the first project alphabetically.
 */
export function projectRows(snapshots: LocalGitSnapshot[], activeProjectId: string | null = null): ProjectRailRow[] {
  if (snapshots.length === 0) {
    return [{ id: 'den-mcp', name: 'den-mcp', subtitle: 'awaiting bridge snapshot', delta: '—', state: 'idle', active: true, workspaceCount: 0 }];
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
    workspaceCount: item.workspaces,
  }));
}

/**
 * Check if a project has multiple workspaces.
 */
export function isMultiWorkspaceProject(snapshots: LocalGitSnapshot[], projectId: string): boolean {
  return snapshots.filter((s) => s.scope.projectId === projectId).length > 1;
}

/**
 * Build workspace rows for a given project from the snapshot list.
 * Each snapshot under the project becomes a selectable workspace row.
 * Rows are sorted by workspace ID (nulls last), then by task ID.
 */
export function workspaceRowsForProject(snapshots: LocalGitSnapshot[], projectId: string): WorkspaceRailRow[] {
  return snapshots
    .filter((s) => s.scope.projectId === projectId)
    .map((s) => ({
      snapshotKey: buildSnapshotRowKey(s),
      projectId: s.scope.projectId,
      workspaceId: s.scope.workspaceId ?? null,
      taskId: s.scope.taskId ?? null,
      branch: s.request.branch ?? null,
      rootPath: s.scope.rootPath,
      dirty: s.request.dirty_counts.total,
      state: s.request.warnings.length > 0 || s.request.state !== 'ok' ? 'warn' : s.request.dirty_counts.total > 0 ? 'running' : 'ok',
    }))
    .sort((a, b) => {
      // Sort: workspaceId ascending, nulls last
      const aWs = a.workspaceId ?? '\uffff';
      const bWs = b.workspaceId ?? '\uffff';
      const wsCmp = aWs.localeCompare(bWs);
      if (wsCmp !== 0) return wsCmp;
      // Then by taskId ascending, nulls last
      const aTask = a.taskId ?? Infinity;
      const bTask = b.taskId ?? Infinity;
      return aTask - bTask;
    });
}

/**
 * Build a display label for a workspace row.
 * Shows workspace ID if present, otherwise task ID, otherwise "project root".
 */
export function workspaceRowLabel(row: WorkspaceRailRow): string {
  if (row.workspaceId) {
    return row.taskId ? `${row.workspaceId} · task #${row.taskId}` : row.workspaceId;
  }
  if (row.taskId) {
    return `task #${row.taskId}`;
  }
  return 'project root';
}

/**
 * Build a stable key for a workspace row from a snapshot.
 * Matches the snapshotKey format from snapshotView but is locally computed.
 */
function buildSnapshotRowKey(snapshot: LocalGitSnapshot): string {
  return [
    snapshot.scope.projectId,
    snapshot.scope.workspaceId ?? 'project',
    snapshot.scope.taskId ?? 'none',
    snapshot.request.root_path,
  ].join('::');
}
