import assert from 'node:assert/strict';
import test from 'node:test';
import {
  isMultiWorkspaceProject,
  projectRowTitle,
  projectRows,
  spaceRows,
  workspaceRowLabel,
  workspaceRowsForProject,
  workspaceToggleLabel,
} from '../src/railView.ts';

function space(overrides = {}) {
  return {
    id: 'personal-1',
    name: 'Personal',
    kind: 'personal',
    visibility: 'normal',
    owner: null,
    rootPath: null,
    description: null,
    createdAt: null,
    updatedAt: null,
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

test('projectRows returns placeholder when no snapshots', () => {
  const rows = projectRows([]);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].id, 'den-mcp');
  assert.equal(rows[0].workspaceCount, 0);
  assert.equal(rows[0].active, true);
});

test('projectRows aggregates single-project snapshots', () => {
  const s1 = snapshot();
  s1.scope.workspaceId = 'ws_1';
  const s2 = snapshot();
  s2.scope.workspaceId = 'ws_2';
  s2.scope.taskId = 882;
  s2.request.task_id = 882;
  s2.request.workspace_id = 'ws_2';
  s2.request.root_path = '/repo-2';
  s2.request.dirty_counts = { total: 0, staged: 0, unstaged: 0, untracked: 0, modified: 0, added: 0, deleted: 0, renamed: 0 };

  const rows = projectRows([s1, s2]);

  assert.equal(rows.length, 1);
  assert.equal(rows[0].id, 'den-mcp');
  assert.equal(rows[0].workspaceCount, 2);
  assert.equal(rows[0].subtitle, '2 workspaces');
  assert.equal(rows[0].delta, '±2');
});

test('projectRows sorts multiple projects alphabetically and picks active', () => {
  const rows = projectRows([
    snapshot({ scope: { ...snapshot().scope, projectId: 'beta' } }),
    snapshot({ scope: { ...snapshot().scope, projectId: 'alpha' } }),
  ], 'beta');

  assert.equal(rows.length, 2);
  assert.equal(rows[0].id, 'alpha');
  assert.equal(rows[1].id, 'beta');
  assert.equal(rows[0].active, false);
  assert.equal(rows[1].active, true);
});

test('projectRows falls back to first project when activeProjectId is invalid', () => {
  const rows = projectRows([
    snapshot({ scope: { ...snapshot().scope, projectId: 'alpha' } }),
    snapshot({ scope: { ...snapshot().scope, projectId: 'beta' } }),
  ], 'nonexistent');

  assert.equal(rows[0].active, true);
  assert.equal(rows[1].active, false);
});

test('projectRows marks state as warn when snapshot has warnings', () => {
  const rows = projectRows([
    snapshot({ request: { ...snapshot().request, warnings: ['something off'] } }),
  ]);
  assert.equal(rows[0].state, 'warn');
});

test('spaceRows includes non-project spaces and project snapshot metadata', () => {
  const rows = spaceRows([
    space({ id: 'den-mcp', name: 'Den MCP', kind: 'project', rootPath: '/repo' }),
    space({ id: 'personal-1', name: 'Personal', kind: 'personal' }),
    space({ id: 'kb-1', name: 'Knowledge', kind: 'knowledge_base', visibility: 'hidden' }),
  ], [snapshot()], 'personal-1');

  assert.equal(rows.length, 3);
  assert.equal(rows[0].id, 'den-mcp');
  assert.equal(rows[0].workspaceCount, 1);
  assert.equal(rows[0].repoBacked, true);
  assert.equal(rows[1].id, 'kb-1');
  assert.match(rows[1].subtitle, /hidden/);
  assert.equal(rows[2].id, 'personal-1');
  assert.equal(rows[2].active, true);
  assert.equal(rows[2].repoBacked, false);
});

test('isMultiWorkspaceProject returns true for multi-workspace projects', () => {
  const snapshots = [
    snapshot({ scope: { ...snapshot().scope, workspaceId: 'ws_1' } }),
    snapshot({ scope: { ...snapshot().scope, workspaceId: 'ws_2' } }),
  ];
  assert.equal(isMultiWorkspaceProject(snapshots, 'den-mcp'), true);
  assert.equal(isMultiWorkspaceProject(snapshots, 'nonexistent'), false);
});

test('isMultiWorkspaceProject returns false for single-workspace projects', () => {
  const snapshots = [snapshot()];
  assert.equal(isMultiWorkspaceProject(snapshots, 'den-mcp'), false);
});

test('workspaceToggleLabel describes explicit expand and collapse controls', () => {
  assert.equal(workspaceToggleLabel('Den MCP', false), 'Expand Den MCP workspaces');
  assert.equal(workspaceToggleLabel('Den MCP', true), 'Collapse Den MCP workspaces');
});

test('projectRowTitle documents that multi-workspace rows select while the chevron expands', () => {
  const row = { name: 'Den MCP', subtitle: 'project · 2 workspaces' };
  assert.equal(projectRowTitle(row, false), 'Den MCP · project · 2 workspaces');
  assert.match(projectRowTitle(row, true), /row selects the space; adjacent chevron toggles workspaces/);
});

test('workspaceRowsForProject returns sorted workspace rows', () => {
  const snapshots = [
    snapshot({ scope: { ...snapshot().scope, workspaceId: 'ws_2', taskId: 882 }, request: { ...snapshot().request, task_id: 882, workspace_id: 'ws_2', root_path: '/repo-2' } }),
    snapshot({ scope: { ...snapshot().scope, workspaceId: 'ws_1', taskId: null }, request: { ...snapshot().request, task_id: null, workspace_id: 'ws_1', root_path: '/repo-1' } }),
  ];

  const rows = workspaceRowsForProject(snapshots, 'den-mcp');
  assert.equal(rows.length, 2);
  assert.equal(rows[0].workspaceId, 'ws_1');
  assert.equal(rows[1].workspaceId, 'ws_2');
});

test('workspaceRowsForProject filters to a single project', () => {
  const snapshots = [
    snapshot({ scope: { ...snapshot().scope, projectId: 'alpha' } }),
    snapshot({ scope: { ...snapshot().scope, projectId: 'beta' } }),
  ];

  assert.equal(workspaceRowsForProject(snapshots, 'alpha').length, 1);
  assert.equal(workspaceRowsForProject(snapshots, 'beta').length, 1);
  assert.equal(workspaceRowsForProject(snapshots, 'nonexistent').length, 0);
});

test('workspaceRowLabel shows workspace ID with task ID', () => {
  assert.equal(workspaceRowLabel({ snapshotKey: 'k', projectId: 'p', workspaceId: 'ws_1', taskId: 42, branch: 'main', rootPath: '/r', dirty: 0, state: 'ok' }), 'ws_1 · task #42');
  assert.equal(workspaceRowLabel({ snapshotKey: 'k', projectId: 'p', workspaceId: 'ws_1', taskId: null, branch: 'main', rootPath: '/r', dirty: 0, state: 'ok' }), 'ws_1');
  assert.equal(workspaceRowLabel({ snapshotKey: 'k', projectId: 'p', workspaceId: null, taskId: 42, branch: 'main', rootPath: '/r', dirty: 0, state: 'ok' }), 'task #42');
  assert.equal(workspaceRowLabel({ snapshotKey: 'k', projectId: 'p', workspaceId: null, taskId: null, branch: 'main', rootPath: '/r', dirty: 0, state: 'ok' }), 'project root');
});

test('workspaceRowsForProject sorts null workspace IDs last', () => {
  const snapshots = [
    snapshot({ scope: { ...snapshot().scope, workspaceId: null, taskId: null }, request: { ...snapshot().request, task_id: null, workspace_id: null, root_path: '/repo-root' } }),
    snapshot({ scope: { ...snapshot().scope, workspaceId: 'ws_a', taskId: null }, request: { ...snapshot().request, task_id: null, workspace_id: 'ws_a', root_path: '/repo-a' } }),
  ];

  const rows = workspaceRowsForProject(snapshots, 'den-mcp');
  assert.equal(rows.length, 2);
  assert.equal(rows[0].workspaceId, 'ws_a');
  assert.equal(rows[1].workspaceId, null);
});
