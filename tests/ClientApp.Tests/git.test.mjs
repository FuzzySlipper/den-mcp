import assert from 'node:assert/strict';
import test from 'node:test';
import {
  dirtyCount,
  formatAheadBehind,
  formatBranchLabel,
  gitDiffBadges,
  gitFileStatusLabel,
  gitNoticeCounts,
  groupGitFiles,
  shouldRequestStagedDiff,
  shortSha,
  summarizeGitStatus,
} from '../../src/DenMcp.Server/ClientApp/src/git.ts';

function status(overrides = {}) {
  return {
    project_id: 'den-mcp',
    workspace_id: null,
    task_id: null,
    workspace_branch: null,
    workspace_base_branch: null,
    workspace_base_commit: null,
    workspace_head_commit: null,
    root_path: '/repo',
    is_git_repository: true,
    branch: 'task/git-ui',
    is_detached: false,
    head_sha: 'abcdef1234567890',
    upstream: 'origin/task/git-ui',
    ahead: 2,
    behind: 1,
    dirty_counts: { total: 3, staged: 1, unstaged: 1, untracked: 1, modified: 1, added: 1, deleted: 0, renamed: 0 },
    files: [],
    warnings: [],
    errors: [],
    truncated: false,
    ...overrides,
  };
}

function file(path, overrides = {}) {
  return {
    path,
    old_path: null,
    index_status: '.',
    worktree_status: '.',
    category: 'changed',
    is_untracked: false,
    ...overrides,
  };
}

test('git status summaries include dirty count, branch, ahead-behind, and short head', () => {
  const item = status();
  assert.equal(shortSha(item.head_sha), 'abcdef1234');
  assert.equal(formatBranchLabel(item), 'task/git-ui');
  assert.equal(formatAheadBehind(item), 'ahead 2, behind 1');
  assert.equal(dirtyCount(item), 3);
  assert.equal(summarizeGitStatus(item), '3 dirty · task/git-ui · ahead 2, behind 1 · abcdef1234');
});

test('detached git status summary falls back to head sha', () => {
  const item = status({ branch: null, is_detached: true, ahead: null, behind: null });
  assert.equal(formatBranchLabel(item), 'detached @ abcdef1234');
  assert.equal(formatAheadBehind(item), 'even');
});

test('status notices and diff badges expose warning/error/truncation render state', () => {
  const item = status({ warnings: ['No upstream'], errors: ['not a repo'] });
  assert.deepEqual(gitNoticeCounts(item), { warnings: 1, errors: 1 });

  assert.deepEqual(gitDiffBadges({
    project_id: 'den-mcp',
    workspace_id: null,
    task_id: null,
    workspace_branch: null,
    workspace_base_branch: null,
    workspace_base_commit: null,
    workspace_head_commit: null,
    root_path: '/repo',
    path: 'src/Foo.cs',
    base_ref: null,
    head_ref: null,
    max_bytes: 1024,
    staged: false,
    diff: 'diff --git a/src/Foo.cs b/src/Foo.cs',
    truncated: true,
    binary: true,
    warnings: ['Git output truncated'],
    errors: ['binary unsupported'],
  }), ['truncated', 'binary', '1 error', '1 warning', 'max 1024 bytes']);
});

test('changed files group into staged, unstaged, untracked, renamed, and deleted sections', () => {
  const staged = file('src/staged.cs', { index_status: 'M', worktree_status: '.', category: 'modified' });
  const groups = groupGitFiles([
    staged,
    file('src/unstaged.cs', { index_status: '.', worktree_status: 'M', category: 'modified' }),
    file('scratch.md', { index_status: '?', worktree_status: '?', category: 'untracked', is_untracked: true }),
    file('src/new-name.cs', { old_path: 'src/old-name.cs', index_status: 'R', worktree_status: '.', category: 'renamed' }),
    file('src/deleted.cs', { index_status: '.', worktree_status: 'D', category: 'deleted' }),
  ]);

  assert.deepEqual(groups.map(group => group.key), ['staged', 'unstaged', 'untracked', 'renamed', 'deleted']);
  assert.deepEqual(groups.map(group => group.label), ['Staged', 'Unstaged', 'Untracked', 'Renamed', 'Deleted']);
  assert.equal(groups[0].files[0].path, 'src/staged.cs');
  assert.equal(gitFileStatusLabel(groups[0].files[0]), 'M ');
  assert.equal(gitFileStatusLabel(groups[2].files[0]), '??');
  assert.equal(shouldRequestStagedDiff(staged), true);
  assert.equal(shouldRequestStagedDiff(groups[1].files[0]), false);
});
