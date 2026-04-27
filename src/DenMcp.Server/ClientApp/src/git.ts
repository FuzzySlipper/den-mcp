import type { GitDiffResponse, GitFileStatus, GitStatusResponse } from './api/types';

export type GitTargetKind = 'project' | 'workspace';

export interface GitStatusTarget {
  id: string;
  kind: GitTargetKind;
  projectId: string;
  workspaceId?: string;
  title: string;
  subtitle: string;
  status: GitStatusResponse;
}

export interface GitFileGroup {
  key: string;
  label: string;
  files: GitFileStatus[];
}

const GROUP_LABELS: Record<string, string> = {
  staged: 'Staged',
  unstaged: 'Unstaged',
  untracked: 'Untracked',
  renamed: 'Renamed',
  deleted: 'Deleted',
  added: 'Added',
  modified: 'Modified',
  changed: 'Changed',
};

const GROUP_ORDER = ['staged', 'unstaged', 'untracked', 'renamed', 'deleted', 'added', 'modified', 'changed'];

export function shortSha(value: string | null | undefined): string {
  return value ? value.slice(0, 10) : 'unknown';
}

export function formatBranchLabel(status: GitStatusResponse): string {
  if (status.is_detached) return `detached @ ${shortSha(status.head_sha)}`;
  return status.branch || status.workspace_branch || 'unknown branch';
}

export function formatAheadBehind(status: GitStatusResponse): string {
  const ahead = status.ahead ?? 0;
  const behind = status.behind ?? 0;
  if (ahead === 0 && behind === 0) return 'even';
  const parts: string[] = [];
  if (ahead > 0) parts.push(`ahead ${ahead}`);
  if (behind > 0) parts.push(`behind ${behind}`);
  return parts.join(', ');
}

export function dirtyCount(status: GitStatusResponse): number {
  return status.dirty_counts?.total ?? status.files.length;
}

export function summarizeGitStatus(status: GitStatusResponse): string {
  const dirty = dirtyCount(status);
  const branch = formatBranchLabel(status);
  const head = shortSha(status.head_sha);
  const sync = formatAheadBehind(status);
  return `${dirty} dirty · ${branch} · ${sync} · ${head}`;
}

export function classifyGitFile(file: GitFileStatus): string {
  if (file.is_untracked) return 'untracked';
  if (file.category === 'renamed' || file.category === 'deleted' || file.category === 'added') return file.category;

  const indexChanged = isChangedStatus(file.index_status);
  const worktreeChanged = isChangedStatus(file.worktree_status);
  if (indexChanged && !worktreeChanged) return 'staged';
  if (worktreeChanged && !indexChanged) return 'unstaged';
  if (file.category && GROUP_LABELS[file.category]) return file.category;
  if (indexChanged) return 'staged';
  if (worktreeChanged) return 'unstaged';
  return 'changed';
}

export function groupGitFiles(files: GitFileStatus[]): GitFileGroup[] {
  const byKey = new Map<string, GitFileStatus[]>();
  for (const file of files) {
    const key = classifyGitFile(file);
    byKey.set(key, [...(byKey.get(key) ?? []), file]);
  }

  return GROUP_ORDER
    .filter(key => (byKey.get(key)?.length ?? 0) > 0)
    .map(key => ({
      key,
      label: GROUP_LABELS[key] ?? key,
      files: [...(byKey.get(key) ?? [])].sort((a, b) => a.path.localeCompare(b.path)),
    }));
}

export function gitFileStatusLabel(file: GitFileStatus): string {
  if (file.is_untracked) return '??';
  const index = normalizeStatus(file.index_status);
  const worktree = normalizeStatus(file.worktree_status);
  return `${index}${worktree}`;
}

export function shouldRequestStagedDiff(file: GitFileStatus): boolean {
  return isChangedStatus(file.index_status) && !isChangedStatus(file.worktree_status);
}

export function sameGitFile(left: GitFileStatus, right: GitFileStatus): boolean {
  return left.path === right.path && (left.old_path ?? null) === (right.old_path ?? null);
}

export function gitNoticeCounts(status: Pick<GitStatusResponse, 'warnings' | 'errors'>): { warnings: number; errors: number } {
  return { warnings: status.warnings.length, errors: status.errors.length };
}

export function gitDiffBadges(diff: GitDiffResponse): string[] {
  const badges: string[] = [];
  if (diff.truncated) badges.push('truncated');
  if (diff.binary) badges.push('binary');
  if (diff.errors.length > 0) badges.push(`${diff.errors.length} error${diff.errors.length === 1 ? '' : 's'}`);
  if (diff.warnings.length > 0) badges.push(`${diff.warnings.length} warning${diff.warnings.length === 1 ? '' : 's'}`);
  badges.push(`max ${diff.max_bytes} bytes`);
  return badges;
}

function normalizeStatus(value: string | null): string {
  return !value || value === '.' ? ' ' : value;
}

function isChangedStatus(value: string | null): boolean {
  return Boolean(value && value !== '.' && value !== '?' && value !== ' ');
}
