import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildTaskGitFocus,
  dirtyCount,
  formatAheadBehind,
  formatBranchLabel,
  gitDiffBadges,
  gitFileStatusLabel,
  gitNoticeCounts,
  groupGitFiles,
  pickFocusedGitTargetId,
  reviewGitAlignmentWarnings,
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

function target(id, overrides = {}) {
  return {
    id,
    kind: 'workspace',
    projectId: 'den-mcp',
    workspaceId: 'ws-1',
    title: 'task/git-ui · #875',
    subtitle: 'ws-1',
    status: status({ workspace_id: 'ws-1', task_id: 875, workspace_branch: 'task/git-ui' }),
    ...overrides,
  };
}

function reviewRound(overrides = {}) {
  return {
    id: 1,
    task_id: 875,
    round_number: 1,
    requested_by: 'pi',
    branch: 'task/git-ui',
    base_branch: 'main',
    base_commit: 'base',
    head_commit: 'abcdef1234567890',
    last_reviewed_head_commit: null,
    commits_since_last_review: null,
    tests_run: null,
    notes: null,
    preferred_diff_base_ref: null,
    preferred_diff_base_commit: null,
    preferred_diff_head_ref: null,
    preferred_diff_head_commit: null,
    alternate_diff_base_ref: null,
    alternate_diff_base_commit: null,
    alternate_diff_head_ref: null,
    alternate_diff_head_commit: null,
    delta_base_commit: null,
    inherited_commit_count: null,
    task_local_commit_count: null,
    verdict: null,
    verdict_by: null,
    verdict_notes: null,
    requested_at: '2026-04-27T00:00:00',
    verdict_at: null,
    preferred_diff: { base_ref: 'main', base_commit: 'base', head_ref: 'task/git-ui', head_commit: 'abcdef1234567890' },
    alternate_diff: null,
    delta_diff: null,
    branch_composition: { inherited_commit_count: null, task_local_commit_count: 1, has_inherited_changes: null, has_task_local_changes: true },
    is_stacked_branch_review: false,
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

test('task detail git focus includes task, workspace, and branch for Git view links', () => {
  assert.deepEqual(buildTaskGitFocus('den-mcp', 875, {
    id: 'ws-1',
    project_id: 'den-mcp',
    task_id: 875,
    branch: 'task/git-ui',
    worktree_path: '/repo',
    base_branch: 'main',
    base_commit: null,
    head_commit: null,
    state: 'active',
    created_by_run_id: null,
    dev_server_url: null,
    preview_url: null,
    cleanup_policy: 'keep',
    changed_file_summary: null,
    created_at: '2026-04-27T00:00:00',
    updated_at: '2026-04-27T00:00:00',
  }, 'review-branch'), {
    projectId: 'den-mcp',
    taskId: 875,
    workspaceId: 'ws-1',
    branch: 'task/git-ui',
  });
});

test('task git focus picks the matching workspace before project fallback', () => {
  const targets = [
    target('project:den-mcp', { kind: 'project', workspaceId: undefined, status: status({ task_id: null, workspace_id: null }) }),
    target('workspace:den-mcp:ws-1'),
    target('workspace:den-mcp:ws-2', { workspaceId: 'ws-2', status: status({ workspace_id: 'ws-2', task_id: 999, workspace_branch: 'task/other' }) }),
  ];

  assert.equal(pickFocusedGitTargetId(targets, { projectId: 'den-mcp', taskId: 875, workspaceId: 'ws-1', branch: 'task/git-ui' }), 'workspace:den-mcp:ws-1');
  assert.equal(pickFocusedGitTargetId([targets[0]], { projectId: 'den-mcp', taskId: 875 }), 'project:den-mcp');
});

test('review metadata mismatch warnings compare review branch and head to live git state', () => {
  assert.deepEqual(reviewGitAlignmentWarnings(reviewRound(), status()), []);
  assert.deepEqual(reviewGitAlignmentWarnings(reviewRound(), status({
    branch: 'task/other',
    head_sha: '1234567890abcdef',
    workspace_base_branch: 'release',
    workspace_base_commit: '9999999999abcdef',
  })), [
    "Review branch 'task/git-ui' differs from live git branch 'task/other'.",
    "Review base branch 'main' differs from workspace base branch 'release'.",
    'Review base base differs from workspace base 9999999999.',
    'Review head abcdef1234 differs from live git HEAD 1234567890.',
  ]);
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
