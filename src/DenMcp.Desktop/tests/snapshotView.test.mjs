import assert from 'node:assert/strict';
import test from 'node:test';
import {
  calmStateLabel,
  diffStatusMessage,
  freshnessLabel,
  groupChangedFiles,
  snapshotKey,
} from '../src/snapshotView.ts';

function file(path, category, overrides = {}) {
  return {
    path,
    old_path: null,
    index_status: '.',
    worktree_status: 'M',
    category,
    is_untracked: category === 'untracked',
    ...overrides,
  };
}

function snapshot(overrides = {}) {
  return {
    scope: {
      projectId: 'den-mcp',
      projectName: 'Den MCP',
      taskId: 881,
      workspaceId: 'ws_881',
      rootPath: '/repo',
      sourceKind: 'agent_workspace',
    },
    request: {
      task_id: 881,
      workspace_id: 'ws_881',
      root_path: '/repo',
      state: 'ok',
      branch: 'task/881-ui',
      is_detached: false,
      head_sha: 'abcdef1234567890',
      upstream: 'origin/task/881-ui',
      ahead: 1,
      behind: 0,
      dirty_counts: { total: 2, staged: 0, unstaged: 1, untracked: 1, modified: 1, added: 0, deleted: 0, renamed: 0 },
      changed_files: [],
      warnings: [],
      truncated: false,
      source_instance_id: 'desktop-1',
      source_display_name: 'Desk',
      observed_at: '2026-04-27T10:00:00.000Z',
    },
    lastPublishStatus: 'published',
    lastPublishError: null,
    lastPublishedAt: '2026-04-27T10:00:02.000Z',
    ...overrides,
  };
}

test('snapshot helpers build stable keys and calm freshness labels', () => {
  const item = snapshot();
  assert.equal(snapshotKey(item), 'den-mcp::ws_881::881::/repo');
  assert.equal(freshnessLabel(item, Date.parse('2026-04-27T10:01:05.000Z')), '1m old');
  assert.equal(calmStateLabel('path_not_visible'), 'path not visible');
  assert.equal(calmStateLabel('source_offline'), 'source offline');
});

test('changed files group in operator-friendly order', () => {
  const groups = groupChangedFiles([
    file('z.txt', 'untracked'),
    file('b.txt', 'modified'),
    file('a.txt', 'modified'),
    file('gone.txt', 'deleted'),
  ]);

  assert.deepEqual(groups.map((group) => [group.category, group.files.map((entry) => entry.path)]), [
    ['modified', ['a.txt', 'b.txt']],
    ['deleted', ['gone.txt']],
    ['untracked', ['z.txt']],
  ]);
});

test('diff status explains missing or unavailable bounded diffs calmly', () => {
  assert.equal(diffStatusMessage(null, null), 'Select a changed file to check for a bounded diff snapshot.');
  assert.equal(diffStatusMessage(null, 'src/App.tsx'), 'Diff status has not been requested yet.');
  assert.equal(diffStatusMessage({ state: 'missing', snapshot: null }, 'src/App.tsx'), 'Diff not available from this source yet.');
  assert.equal(diffStatusMessage({ state: 'source_offline', snapshot: null }, 'src/App.tsx'), 'Diff source is stale or offline.');
  assert.equal(diffStatusMessage({ state: 'ok', snapshot: { diff: 'diff --git' } }, 'src/App.tsx'), '');
});
