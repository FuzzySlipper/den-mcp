import assert from 'node:assert/strict';
import test from 'node:test';
import {
  TERMINAL_ATTACH_INTERACTION_DECISION,
  buildTerminalSessionOverview,
  canAttachInline,
  relativeActivityLabel,
  terminalInlineAttachButtonLabel,
  terminalNonAttachableDoubleClickHint,
  terminalSessionCardActionHint,
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

test('terminal attach labels encode select-first explicit attach UX', () => {
  const [attachable] = buildTerminalSessionOverview([
    {
      session_id: 'pty-1',
      display_name: 'PTY 1',
      kind: 'terminal',
      backend: 'direct_pty',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 1038,
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
  ], [], Date.parse('2026-04-30T00:02:20.000Z'));
  const [externalOnly] = buildTerminalSessionOverview([
    {
      session_id: 'tmux-1',
      display_name: 'tmux detached',
      kind: 'terminal',
      backend: 'tmux',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 1038,
      workspace_id: 'ws-main',
      cwd: '/work/den-mcp',
      can_attach: false,
      can_stream_terminal: false,
      can_open_external_attach: true,
      last_activity_at: '2026-04-30T00:01:00.000Z',
      last_observed_at: '2026-04-30T00:01:00.000Z',
      warnings: [],
    },
  ], [], Date.parse('2026-04-30T00:02:20.000Z'));
  const [readOnly] = buildTerminalSessionOverview([], [snapshot()], Date.parse('2026-04-30T00:03:00.000Z'));

  assert.equal(TERMINAL_ATTACH_INTERACTION_DECISION, 'select_first_explicit_attach');
  assert.equal(terminalInlineAttachButtonLabel(attachable), 'Attach inline');
  assert.equal(terminalInlineAttachButtonLabel(attachable, true), 'Reattach inline');
  assert.equal(terminalSessionCardActionHint(attachable), 'Single click selects; Attach inline, Enter, or double-click attaches');
  assert.equal(terminalSessionCardActionHint(externalOnly), 'Single click selects; External attach shows copy-only attach information');
  assert.equal(terminalInlineAttachButtonLabel(readOnly), 'Inline attach unavailable');
  assert.equal(terminalSessionCardActionHint(readOnly), 'Single click selects and previews metadata');
});

test('terminal event refresh urgency favors boundary changes over noisy activity', () => {
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'running' }), 'coalesced');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.heartbeat' }), 'coalesced');
  // replay_complete is coalesced because the attach flow already calls refreshSessionsNow()
  // at attach time; replay_complete is a streaming milestone, not a state boundary (#1064).
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.replay_complete' }), 'coalesced');

  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'detached' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'exited' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.exit' }), 'immediate');
});

test('replay_complete urgency decision documented: attach flow already refreshes immediately', () => {
  // Decision record for #1064: den.terminal.replay_complete remains coalesced.
  //
  // Rationale:
  // 1. The attach flow triggers an immediate session-list refresh after receiving the
  //    attach response — capabilities and session state are already current.
  // 2. replay_complete fires once after the sidecar finishes replaying buffered output;
  //    it signals a streaming milestone, not a state/capability boundary.
  // 3. Making it immediate would add a redundant session-list refresh with no UX benefit.
  // 4. Noisy event coalescing from #1037 (heartbeat, active-stream status) is preserved.
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.replay_complete' }), 'coalesced');
  // Verify state-boundary events remain immediate for contrast.
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.exit' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'lifecycle', event: 'den.terminal.error' }), 'immediate');
});

test('starting status urgency decision documented: no event path emits starting, remains coalesced', () => {
  // Decision record for #1065: `starting` status remains coalesced.
  //
  // Evidence from sidecar event-flow analysis:
  // 1. OperatorSession defaults Status to "starting", but every creation path overrides
  //    to Running/Exited/Failed/Stale before publishing session or status events:
  //    - DirectPtyOperatorSessionService: Status = OperatorSessionStatus.Running at creation
  //    - TmuxOperatorSessionService: Status = Running or Stale
  //    - OperatorSessionRegistry.RegisterFromPiSnapshot: FromLegacyPhase -> Running/Exited/Failed
  //    - AppAgentServices: Status = Running or Exited
  // 2. No current code path emits a status event with value "starting".
  // 3. New-session discovery is covered by session-list events and the initial
  //    terminalListSessions call, not by status events.
  // 4. If starting were ever emitted in the future, a 750ms coalesced delay is acceptable
  //    for a transient startup phase that immediately transitions to running.
  //
  // Making starting immediate would be dead code with no event-flow justification.
  // Noisy active-stream coalescing from #1037 is preserved.
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'starting' }), 'coalesced');
  // Verify the active-stream statuses that drove #1037 coalescing remain coalesced.
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'running' }), 'coalesced');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'idle' }), 'coalesced');
  // Verify state-boundary terminal statuses remain immediate.
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'exited' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'detached' }), 'immediate');
  assert.equal(terminalSessionRefreshUrgency({ kind: 'status', status: 'failed' }), 'immediate');
});

test('terminalNonAttachableDoubleClickHint returns null for attachable cards, contextual hint otherwise', () => {
  // Decision record for #1068: non-attachable card double-clicks show brief feedback.
  //
  // Rationale:
  // 1. Attachable cards respond to double-click with an attach action, giving visible feedback.
  // 2. Non-attachable cards silently did nothing on double-click, creating inconsistent UX.
  // 3. terminalNonAttachableDoubleClickHint provides a brief contextual message without
  //    triggering attach or external attach commands.
  // 4. Single-click select/preview behavior is preserved unchanged.
  // 5. Explicit Attach button in the action row remains the primary attach affordance.

  // Attachable inline session — no hint (double-click is a valid attach)
  const [attachable] = buildTerminalSessionOverview([
    {
      session_id: 'pty-1',
      display_name: 'PTY 1',
      kind: 'terminal',
      backend: 'direct_pty',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 1068,
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
  ], [], Date.parse('2026-04-30T00:02:20.000Z'));
  assert.equal(terminalNonAttachableDoubleClickHint(attachable), null);

  // Read-only Pi artifact session
  const [readOnly] = buildTerminalSessionOverview([], [snapshot()], Date.parse('2026-04-30T00:03:00.000Z'));
  assert.equal(readOnly.readOnly, true);
  assert.equal(terminalNonAttachableDoubleClickHint(readOnly), 'Read-only session — attach unavailable');

  // External-attach-only session (not read-only, no inline attach)
  const [externalOnly] = buildTerminalSessionOverview([
    {
      session_id: 'tmux-1',
      display_name: 'tmux detached',
      kind: 'terminal',
      backend: 'tmux',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 1068,
      workspace_id: 'ws-main',
      cwd: '/work/den-mcp',
      can_attach: false,
      can_stream_terminal: false,
      can_open_external_attach: true,
      last_activity_at: '2026-04-30T00:01:00.000Z',
      last_observed_at: '2026-04-30T00:01:00.000Z',
      warnings: [],
    },
  ], [], Date.parse('2026-04-30T00:02:20.000Z'));
  assert.equal(externalOnly.readOnly, false);
  assert.equal(terminalNonAttachableDoubleClickHint(externalOnly), 'Attach not available for this session');

  // Session with canAttach but not canStreamTerminal (partial capabilities)
  const [partial] = buildTerminalSessionOverview([
    {
      session_id: 'partial-1',
      display_name: 'partial session',
      kind: 'terminal',
      backend: 'tmux',
      status: 'running',
      project_id: 'den-mcp',
      task_id: 1068,
      workspace_id: 'ws-main',
      cwd: '/work/den-mcp',
      can_attach: true,
      can_stream_terminal: false,
      last_activity_at: '2026-04-30T00:01:00.000Z',
      last_observed_at: '2026-04-30T00:01:00.000Z',
      warnings: [],
    },
  ], [], Date.parse('2026-04-30T00:02:20.000Z'));
  assert.equal(canAttachInline(partial), false);
  assert.equal(terminalNonAttachableDoubleClickHint(partial), 'Inline attach unavailable');
});
