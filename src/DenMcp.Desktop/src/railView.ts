import type { DenSpace, LocalGitSnapshot } from './desktop/sidecarBridgeApi';
import { snapshotKey } from './snapshotView.ts';

/** Sentinel value for the Global project filter option in the LeftRail. */
export const GLOBAL_PROJECT_ID = '_global' as const;

export interface ProjectRailRow {
  id: string;
  name: string;
  subtitle: string;
  delta: string;
  state: string;
  active: boolean;
  workspaceCount: number;
  kind?: string;
  visibility?: string;
  rootPath?: string | null;
  repoBacked?: boolean;
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
 * When `activeProjectId` is GLOBAL_PROJECT_ID ('_global'), the Global row is marked active.
 */
export function projectRows(snapshots: LocalGitSnapshot[], activeProjectId: string | null = null): ProjectRailRow[] {
  if (snapshots.length === 0) {
    return [{ id: 'den-mcp', name: 'den-mcp', subtitle: 'awaiting bridge snapshot', delta: '—', state: 'idle', active: true, workspaceCount: 0, kind: 'project', visibility: 'normal', rootPath: null, repoBacked: false }];
  }

  const byProject = summarizeSnapshots(snapshots);
  const sorted = [...byProject.entries()].sort(([a], [b]) => a.localeCompare(b));
  // Determine active ID: Global doesn't map to a project, so fall through to first project
  const activeId = (activeProjectId && activeProjectId !== GLOBAL_PROJECT_ID && byProject.has(activeProjectId))
    ? activeProjectId
    : sorted[0]?.[0] ?? null;
  return sorted.map(([id, item]) => ({
    id,
    name: id,
    subtitle: `${item.workspaces} workspace${item.workspaces === 1 ? '' : 's'}`,
    delta: item.dirty > 0 ? `±${item.dirty}` : 'clean',
    state: item.warning ? 'warn' : item.dirty > 0 ? 'running' : 'ok',
    active: id === activeId,
    workspaceCount: item.workspaces,
    kind: 'project',
    visibility: 'normal',
    rootPath: null,
    repoBacked: item.workspaces > 0,
  }));
}

/**
 * Build rail rows from generalized Den spaces while preserving snapshot rollups
 * for repo-backed projects. Snapshot-only projects are kept as degraded/fallback
 * rows when the sidecar has not fetched spaces yet.
 */
export function spaceRows(spaces: DenSpace[], snapshots: LocalGitSnapshot[], activeSpaceId: string | null = null): ProjectRailRow[] {
  if (spaces.length === 0) {
    return projectRows(snapshots, activeSpaceId);
  }

  const byProject = summarizeSnapshots(snapshots);
  const sortedSpaces = [...spaces]
    .filter((space) => space.id !== GLOBAL_PROJECT_ID)
    .sort((a, b) => {
      if (a.kind === 'project' && b.kind !== 'project') return -1;
      if (a.kind !== 'project' && b.kind === 'project') return 1;
      return a.id.localeCompare(b.id);
    });

  const ids = new Set(sortedSpaces.map((space) => space.id));
  const activeId = activeSpaceId && activeSpaceId !== GLOBAL_PROJECT_ID
    ? activeSpaceId
    : sortedSpaces[0]?.id ?? null;

  const rows = sortedSpaces.map((space) => {
    const summary = byProject.get(space.id) ?? { dirty: 0, workspaces: 0, warning: false };
    return rowFromSummary(
      space.id,
      space.name || space.id,
      summary,
      activeId === space.id,
      space.kind,
      space.visibility,
      space.rootPath,
    );
  });

  for (const [id, summary] of [...byProject.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    if (!ids.has(id)) {
      rows.push(rowFromSummary(id, id, summary, activeId === id, 'project', 'normal', null));
    }
  }

  return rows;
}

function summarizeSnapshots(snapshots: LocalGitSnapshot[]): Map<string, { dirty: number; workspaces: number; warning: boolean }> {
  const byProject = new Map<string, { dirty: number; workspaces: number; warning: boolean }>();
  for (const snapshot of snapshots) {
    const id = snapshot.scope.projectId;
    const current = byProject.get(id) ?? { dirty: 0, workspaces: 0, warning: false };
    current.dirty += snapshot.request.dirty_counts.total;
    current.workspaces += 1;
    current.warning ||= snapshot.request.warnings.length > 0 || snapshot.request.state !== 'ok';
    byProject.set(id, current);
  }
  return byProject;
}

function rowFromSummary(
  id: string,
  name: string,
  item: { dirty: number; workspaces: number; warning: boolean },
  active: boolean,
  kind: string,
  visibility: string,
  rootPath: string | null,
): ProjectRailRow {
  const repoBacked = Boolean(rootPath?.trim()) || item.workspaces > 0;
  const capability = kind === 'project'
    ? repoBacked ? 'repo-backed project' : 'project'
    : repoBacked ? 'root-backed space' : 'space only';
  const workspaceLabel = item.workspaces > 0
    ? `${item.workspaces} workspace${item.workspaces === 1 ? '' : 's'}`
    : capability;
  const visibilityLabel = visibility && visibility !== 'normal' ? ` · ${visibility}` : '';
  return {
    id,
    name,
    subtitle: `${kind}${visibilityLabel} · ${workspaceLabel}`,
    delta: item.dirty > 0 ? `±${item.dirty}` : item.workspaces > 0 ? 'clean' : '—',
    state: item.warning ? 'warn' : item.dirty > 0 ? 'running' : item.workspaces > 0 ? 'ok' : 'idle',
    active,
    workspaceCount: item.workspaces,
    kind,
    visibility,
    rootPath,
    repoBacked,
  };
}

/**
 * Build the Global rail row, shown as the first item in the LeftRail.
 */
export function globalRailRow(active: boolean): ProjectRailRow {
  return {
    id: GLOBAL_PROJECT_ID,
    name: 'Global',
    subtitle: 'All spaces',
    delta: '',
    state: 'global',
    active,
    workspaceCount: 0,
    kind: 'system',
    visibility: 'normal',
    rootPath: null,
    repoBacked: false,
  };
}

/**
 * Check if a project has multiple workspaces.
 */
export function isMultiWorkspaceProject(snapshots: LocalGitSnapshot[], projectId: string): boolean {
  return snapshots.filter((s) => s.scope.projectId === projectId).length > 1;
}

/**
 * Build the accessible label for the explicit workspace expand/collapse control.
 * Project row clicks select the space; this separate control manages the child workspace list.
 */
export function workspaceToggleLabel(projectName: string, expanded: boolean): string {
  return `${expanded ? 'Collapse' : 'Expand'} ${projectName} workspaces`;
}

/**
 * Build the project-row title text shown in the rail.
 */
export function projectRowTitle(row: Pick<ProjectRailRow, 'name' | 'subtitle'>, hasWorkspaceToggle: boolean): string {
  const base = `${row.name} · ${row.subtitle}`;
  return hasWorkspaceToggle ? `${base} · row selects the space; adjacent chevron toggles workspaces` : base;
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
      snapshotKey: snapshotKey(s),
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

