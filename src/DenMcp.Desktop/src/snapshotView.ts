import type { DesktopDiffSnapshotLatestResult, GitFileStatus, LocalGitSnapshot } from './desktop/tauriApi';

export interface FileGroup {
  category: string;
  files: GitFileStatus[];
}

export function snapshotKey(snapshot: LocalGitSnapshot): string {
  return [
    snapshot.scope.projectId,
    snapshot.scope.workspaceId ?? 'project',
    snapshot.scope.taskId ?? 'none',
    snapshot.request.root_path,
  ].join('::');
}

export function groupChangedFiles(files: GitFileStatus[]): FileGroup[] {
  const order = ['modified', 'added', 'deleted', 'renamed', 'untracked', 'changed'];
  const groups = new Map<string, GitFileStatus[]>();
  for (const file of files) {
    const category = file.category || 'changed';
    groups.set(category, [...(groups.get(category) ?? []), file]);
  }
  return [...groups.entries()]
    .sort(([left], [right]) => (order.indexOf(left) === -1 ? 99 : order.indexOf(left)) - (order.indexOf(right) === -1 ? 99 : order.indexOf(right)))
    .map(([category, groupedFiles]) => ({ category, files: groupedFiles.sort((a, b) => a.path.localeCompare(b.path)) }));
}

export function snapshotTitle(snapshot: LocalGitSnapshot): string {
  if (snapshot.scope.workspaceId) {
    return `${snapshot.scope.projectId} · workspace ${snapshot.scope.workspaceId}`;
  }
  return `${snapshot.scope.projectId} · project root`;
}

export function freshnessLabel(snapshot: LocalGitSnapshot, nowMs = Date.now()): string {
  const observed = Date.parse(snapshot.request.observed_at);
  if (Number.isNaN(observed)) return 'freshness unknown';
  const seconds = Math.max(0, Math.round((nowMs - observed) / 1000));
  if (seconds < 60) return `${seconds}s old`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m old`;
  const hours = Math.round(minutes / 60);
  return `${hours}h old`;
}

export function calmStateLabel(state: string): string {
  switch (state) {
    case 'path_not_visible': return 'path not visible';
    case 'not_git_repository': return 'not a git repo';
    case 'source_offline': return 'source offline';
    case 'git_error': return 'git warning';
    case 'missing': return 'missing';
    default: return state.replaceAll('_', ' ');
  }
}

export function diffStatusMessage(result: DesktopDiffSnapshotLatestResult | null, selectedPath: string | null): string {
  if (!selectedPath) return 'Select a changed file to check for a bounded diff snapshot.';
  if (!result) return 'Diff status has not been requested yet.';
  if (result.snapshot?.diff) return '';
  if (result.state === 'missing') return 'Diff not available from this source yet.';
  if (result.state === 'source_offline') return 'Diff source is stale or offline.';
  if (result.state === 'path_not_visible') return 'Path is not visible to the selected source.';
  return `Diff unavailable: ${calmStateLabel(result.state)}.`;
}
