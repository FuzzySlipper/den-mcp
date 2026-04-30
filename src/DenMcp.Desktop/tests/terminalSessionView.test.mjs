import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildTerminalSessionOverview,
  canAttachInline,
  relativeActivityLabel,
  terminalSessionRefreshUrgency,
  terminalStatusLabel,
} from '../src/terminalSessionView.ts';

function snapshot(overrides = {}) {
  const request = {
    task_id: 913,
    workspace_id: 'ws-main',
    session_id: 'pi-artifact-1',
    parent_session_id: null,
    agent_identity: 'pi',
    role: 'coder',
    current_command: 'bash',
    current_phase: 'running',
    title: 'artifact run',
    display_name: 'Pi artifact run',
    cwd: '/work/den-mcp',
    kind: 'artifact_observer',
    backend: 'pi_artifact',
    status: 'running',
    started_at: '2026-04-30T00:00:00.000Z',
    last_activity_at: '2026-04-30T00:02:00.000Z',
    exited_at: null,
    exit_code: null,
    source_display_name: 'desktop test',
    capabilities: { can_read_activity: true },
    recent_activity: { items: [{ kind: 'tool', tool: 'bash', summary: 'git status', timestamp: '2026-04-30T00:02:00.000Z' }] },
    child_sessions: { items: [] },
    control_capabilities: { can_attach: false, can_stream_terminal: false, can_send_input: false, can_stop: false },
    warnings: [],
    source_instance_id: 'desktop-1',
    observed_at: '2026-04-30T00:02:00.000Z',
    ...overrides.request,
  };
  return {
    projectId: 'den-mcp',
    request,
    lastPublishStatus: 'published',
    lastPublishError: null,
    lastPublishedAt: null,
    artifactRoot: '/tmp/run',
    ...overrides,
    request,
  };
}

test('terminal overview merges local sidecar summaries with observed warnings and activity', () => {
  const rows = buildTerminalSessionOverview([
    {
      session_id: 'pty-1',
      display_name: 'PTY 1',
      kind: 'terminal',
      backend: 'direct_pty',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 913,
      workspace_id: 'ws-main',
      cwd: '/work/den-mcp',
      can_attach: true,
      can_stream_terminal: true,
      can_send_input: true,
      can_resize: true,
      can_detach: true,
      can_reconnect: true,
      can_terminate: true,
      last_activity_at: '2026-04-30T00:01:00.000Z',
      last_observed_at: '2026-04-30T00:01:00.000Z',
      warnings: [],
    },
  ], [snapshot({ request: { session_id: 'pty-1', warnings: ['lease heartbeat delayed'], observed_at: '2026-04-30T00:02:00.000Z' } })], Date.parse('2026-04-30T00:02:20.000Z'));

  assert.equal(rows.length, 1);
  assert.equal(rows[0].displayName, 'PTY 1');
  assert.equal(rows[0].authority, 'local');
  assert.equal(rows[0].warnings[0], 'lease heartbeat delayed');
  assert.equal(rows[0].recentActivity[0].summary, 'git status');
  assert.equal(canAttachInline(rows[0]), true);
  assert.ok(rows[0].capabilityLabels.includes('inline attach'));
});

test('observed-only Pi artifact sessions stay read-only but show calm activity state', () => {
  const rows = buildTerminalSessionOverview([], [snapshot()], Date.parse('2026-04-30T00:03:00.000Z'));

  assert.equal(rows.length, 1);
  assert.equal(rows[0].backend, 'pi_artifact');
  assert.equal(rows[0].readOnly, true);
  assert.equal(rows[0].capabilityLabels.join(','), 'activity');
  assert.equal(canAttachInline(rows[0]), false);
});

test('stale/source offline labels are calm and relative activity is readable', () => {
  const rows = buildTerminalSessionOverview([], [snapshot({ request: { status: 'source_offline', observed_at: '2026-04-30T00:00:00.000Z' } })], Date.parse('2026-04-30T00:05:00.000Z'));

  assert.equal(rows[0].stale, true);
  assert.equal(rows[0].statusTone, 'idle');
  assert.equal(terminalStatusLabel('source_offline'), 'source offline');
  assert.equal(relativeActivityLabel('2026-04-30T00:00:00.000Z', Date.parse('2026-04-30T00:05:00.000Z')), '5m ago');
});

test('terminal event refresh urgency favors boundary changes over noisy activity', () => {
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'running' }), 'coalesced');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.heartbeat' }), 'coalesced');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.replay_complete' }), 'coalesced');

  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'detached' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'exited' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.exit' }), 'immediate');
});
