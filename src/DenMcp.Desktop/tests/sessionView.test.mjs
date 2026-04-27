import assert from 'node:assert/strict';
import test from 'node:test';
import {
  capabilitySummary,
  isSessionIdle,
  phaseLabel,
  recentActivityItems,
  sessionKey,
  sessionTitle,
} from '../src/sessionView.ts';

function snapshot(overrides = {}) {
  return {
    projectId: 'den-mcp',
    request: {
      task_id: 882,
      workspace_id: 'ws_882',
      session_id: '019dceca-74fb-764d-98c6-88c7ac577c32',
      parent_session_id: null,
      agent_identity: 'pi',
      role: 'reviewer',
      current_command: 'bash',
      current_phase: 'running',
      recent_activity: {
        schema: 'den_desktop_recent_activity',
        items: [{ kind: 'assistant_tool_call', tool: 'bash', summary: 'git status', timestamp: '2026-04-27T12:00:00Z' }],
      },
      child_sessions: { items: [] },
      control_capabilities: { can_focus: false, can_stream_raw_terminal: false, can_send_input: false, can_stop: false, can_launch_managed_session: false },
      warnings: [],
      source_instance_id: 'desktop-1',
      observed_at: '2026-04-27T12:00:00.000Z',
    },
    lastPublishStatus: 'published',
    lastPublishError: null,
    lastPublishedAt: null,
    artifactRoot: '/tmp/run',
    ...overrides,
  };
}

test('session helpers expose stable keys and readable labels', () => {
  const item = snapshot();
  assert.equal(sessionKey(item), 'den-mcp::ws_882::882::019dceca-74fb-764d-98c6-88c7ac577c32');
  assert.equal(sessionTitle(item), 'task #882 · reviewer · 019dceca-74f');
  assert.equal(phaseLabel('failed'), 'attention needed');
  assert.equal(phaseLabel('tool_use'), 'tool use');
});

test('session helper treats old observations as idle and current observations as active', () => {
  const item = snapshot();
  assert.equal(isSessionIdle(item, Date.parse('2026-04-27T12:00:30.000Z')), false);
  assert.equal(isSessionIdle(item, Date.parse('2026-04-27T12:05:00.000Z')), true);
});

test('activity and capability helpers prefer structured observation data', () => {
  const item = snapshot();
  assert.equal(capabilitySummary(item), 'observation only');
  assert.deepEqual(recentActivityItems(item).map((entry) => entry.summary), ['git status']);

  const controlled = snapshot({ request: { ...item.request, control_capabilities: { can_focus: true, can_stop: true } } });
  assert.equal(capabilitySummary(controlled), 'focus, stop');
});
