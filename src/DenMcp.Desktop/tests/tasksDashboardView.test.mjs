import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildDashboardView,
  buildSessionChipView,
  deriveProgressStage,
  extractPacketSummaries,
  formatCost,
  formatTokenCount,
  progressStageIndex,
  progressStageLabel,
  relativeTimeLabel,
  taskDisplayState,
  taskDisplayTone,
  taskStatusLabel,
  waveDisplayTone,
} from '../src/tasksDashboardView.ts';

function makeSnapshot(overrides = {}) {
  return {
    snapshot_id: 'snap-1',
    project_id: 'den-mcp',
    parent_task_id: 900,
    focused_task_id: null,
    generated_at: '2026-04-30T00:05:00.000Z',
    header: {
      state: 'running',
      task_count: 5,
      done_count: 2,
      active_count: 2,
      review_count: 1,
      blocked_count: 0,
      completion_percent: 40,
      total_tokens: 250000,
      total_cost: 3.25,
      currency: 'USD',
      last_updated_at: '2026-04-30T00:04:00.000Z',
    },
    tasks: [
      {
        id: 909,
        project_id: 'den-mcp',
        parent_id: 900,
        title: 'Implement projection',
        status: 'done',
        computed_state: 'done',
        dependencies: [],
        packets: [
          { type: 'implementation_packet', created_at: '2026-04-30T00:01:00.000Z', summary: 'Projection implemented' },
          { type: 'merge_summary', created_at: '2026-04-30T00:03:00.000Z' },
        ],
        review: { state: 'approved', open_findings: 0 },
        run_summary: { elapsed: '12m', total_tokens: 80000, total_cost: 1.10, currency: 'USD', branch: 'task/909-projection', worktree_path: '/work/den-mcp' },
        agent_lifecycle: {},
        session_chips: [],
      },
      {
        id: 1029,
        project_id: 'den-mcp',
        parent_id: 900,
        title: 'Tasks dashboard UI',
        status: 'in_progress',
        computed_state: 'coder_running',
        dependencies: [{ task_id: 909 }],
        packets: [
          { type: 'coder_context_packet', created_at: '2026-04-30T00:00:30.000Z' },
        ],
        review: {},
        run_summary: { elapsed: '5m', total_tokens: 45000, total_cost: 0.60, currency: 'USD', branch: 'task/1029-tasks-dashboard-ui' },
        agent_lifecycle: {},
        session_chips: [{ session_id: 'pi-coder-1', display_name: 'Pi coder #1029', backend: 'direct_pty', can_open_external_attach: true, external_attach_command: 'tmux a -t pi-1029' }],
      },
      {
        id: 1038,
        project_id: 'den-mcp',
        parent_id: 900,
        title: 'Terminal attach',
        status: 'review',
        computed_state: 'review',
        dependencies: [],
        packets: [
          { type: 'implementation_packet', created_at: '2026-04-30T00:02:00.000Z' },
          { type: 'review_request_packet', created_at: '2026-04-30T00:03:00.000Z' },
        ],
        review: { state: 'changes_requested', open_findings: 2 },
        run_summary: { total_tokens: 120000, total_cost: 1.55, currency: 'USD' },
        agent_lifecycle: {},
        session_chips: [],
      },
    ],
    waves: [
      { index: 0, label: 'wave 0', state: 'done', task_ids: [909], summary: 'Foundation wave' },
      { index: 1, label: 'wave 1', state: 'running', task_ids: [1029, 1038], summary: 'Dashboard + terminal wave' },
    ],
    lanes: [
      { lane_key: 'coder:1029', task_id: 1029, label: 'coder · #1029', role: 'coder', state: 'running', branch: 'task/1029-tasks-dashboard-ui', worktree_path: '/work/den-mcp', latest_run: {}, latest_agent_event: null, session_chips: [] },
    ],
    freshness: { source: 'bridge', generated_at: '2026-04-30T00:05:00.000Z', is_partial: false, warnings: [], errors: [] },
    ...overrides,
  };
}

test('buildDashboardView transforms snapshot into display-ready view', () => {
  const snapshot = makeSnapshot();
  const view = buildDashboardView(snapshot, null, Date.parse('2026-04-30T00:05:30.000Z'));

  assert.equal(view.header.stateLabel, 'running');
  assert.equal(view.header.stateTone, 'running');
  assert.equal(view.header.taskCount, 5);
  assert.equal(view.header.doneCount, 2);
  assert.equal(view.header.completionPercent, 40);
  assert.equal(view.header.totalTokens, 250000);
  assert.equal(view.header.totalCost, 3.25);

  assert.equal(view.tasks.length, 3);
  assert.equal(view.tasks[0].id, 909);
  assert.equal(view.tasks[0].displayState, 'done');
  assert.equal(view.tasks[0].displayTone, 'ok');
  assert.equal(view.tasks[0].progressStage, 'done');
  assert.equal(view.tasks[0].branch, 'task/909-projection');

  assert.equal(view.tasks[1].id, 1029);
  assert.equal(view.tasks[1].displayState, 'in_progress');
  assert.equal(view.tasks[1].displayTone, 'running');
  assert.equal(view.tasks[1].sessionChips.length, 1);
  assert.equal(view.tasks[1].sessionChips[0].label, 'Pi coder #1029');
  assert.equal(view.tasks[1].sessionChips[0].canAttach, true);
  assert.equal(view.tasks[1].sessionChips[0].attachCommand, 'tmux a -t pi-1029');

  assert.equal(view.tasks[2].id, 1038);
  assert.equal(view.tasks[2].displayState, 'review');
  assert.equal(view.tasks[2].displayTone, 'accent');
  assert.equal(view.tasks[2].reviewState, 'changes_requested');
  assert.equal(view.tasks[2].reviewFindingsOpen, 2);

  assert.equal(view.waves.length, 2);
  assert.equal(view.waves[0].tone, 'ok');
  assert.equal(view.waves[1].tone, 'running');

  assert.equal(view.lanes.length, 1);
  assert.equal(view.lanes[0].label, 'coder · #1029');
  assert.equal(view.lanes[0].online, true);

  assert.equal(view.freshness.isStale, false);
  assert.equal(view.freshness.isPartial, false);

  assert.ok(view.statusPanel.length > 0);
  assert.equal(view.statusPanel[0].heading, 'Run overview');
});

test('buildDashboardView with null snapshot returns empty view', () => {
  const view = buildDashboardView(null);
  assert.equal(view.header.taskCount, 0);
  assert.equal(view.tasks.length, 0);
  assert.equal(view.waves.length, 0);
  assert.equal(view.lanes.length, 0);
  assert.equal(view.header.stateLabel, 'No snapshot loaded');
  assert.equal(view.header.stateTone, 'idle');
});

test('taskDisplayState normalizes various status strings', () => {
  assert.equal(taskDisplayState('done'), 'done');
  assert.equal(taskDisplayState('merged'), 'done');
  assert.equal(taskDisplayState('complete'), 'done');
  assert.equal(taskDisplayState('cancelled'), 'cancelled');
  assert.equal(taskDisplayState('blocked'), 'blocked');
  assert.equal(taskDisplayState('review'), 'review');
  assert.equal(taskDisplayState('in_review'), 'review');
  assert.equal(taskDisplayState('in_progress'), 'in_progress');
  assert.equal(taskDisplayState('running'), 'in_progress');
  assert.equal(taskDisplayState('active'), 'in_progress');
  assert.equal(taskDisplayState('planned'), 'planned');
  assert.equal(taskDisplayState('queued'), 'planned');
  assert.equal(taskDisplayState('open'), 'planned');
  assert.equal(taskDisplayState('needs_attention'), 'needs_attention');
  assert.equal(taskDisplayState('failed'), 'needs_attention');
  assert.equal(taskDisplayState(''), 'unknown');
  assert.equal(taskDisplayState('custom_status'), 'unknown');

  // computed_state overrides when provided
  assert.equal(taskDisplayState('in_progress', 'coder_running'), 'in_progress');
  assert.equal(taskDisplayState('planned', 'blocked'), 'blocked');
});

test('taskDisplayTone maps display states to tones', () => {
  assert.equal(taskDisplayTone('done'), 'ok');
  assert.equal(taskDisplayTone('in_progress'), 'running');
  assert.equal(taskDisplayTone('review'), 'accent');
  assert.equal(taskDisplayTone('blocked'), 'err');
  assert.equal(taskDisplayTone('needs_attention'), 'warn');
  assert.equal(taskDisplayTone('cancelled'), 'idle');
  assert.equal(taskDisplayTone('planned'), 'info');
  assert.equal(taskDisplayTone('unknown'), 'idle');
});

test('waveDisplayTone maps wave states to tones', () => {
  assert.equal(waveDisplayTone('done'), 'ok');
  assert.equal(waveDisplayTone('running'), 'running');
  assert.equal(waveDisplayTone('review'), 'accent');
  assert.equal(waveDisplayTone('blocked'), 'warn');
  assert.equal(waveDisplayTone('needs_attention'), 'warn');
  assert.equal(waveDisplayTone('queued'), 'info');
  assert.equal(waveDisplayTone('custom'), 'idle');
});

test('formatTokenCount formats various token sizes', () => {
  assert.equal(formatTokenCount(null), '—');
  assert.equal(formatTokenCount(undefined), '—');
  assert.equal(formatTokenCount(0), '0');
  assert.equal(formatTokenCount(500), '500');
  assert.equal(formatTokenCount(1500), '1.5k');
  assert.equal(formatTokenCount(1500000), '1.5M');
  assert.equal(formatTokenCount(250000), '250.0k');
});

test('formatCost formats cost with currency', () => {
  assert.equal(formatCost(null), '—');
  assert.equal(formatCost(undefined), '—');
  assert.equal(formatCost(0), '$0.00');
  assert.equal(formatCost(3.25), '$3.25');
  assert.equal(formatCost(0.005), '$0.0050');
  assert.equal(formatCost(10, 'EUR'), 'EUR 10.00');
});

test('relativeTimeLabel converts timestamps to relative labels', () => {
  const now = Date.parse('2026-04-30T00:05:00.000Z');
  assert.equal(relativeTimeLabel(null, now), '—');
  assert.equal(relativeTimeLabel(undefined, now), '—');
  assert.equal(relativeTimeLabel('2026-04-30T00:04:30.000Z', now), '30s ago');
  assert.equal(relativeTimeLabel('2026-04-30T00:03:00.000Z', now), '2m ago');
  assert.equal(relativeTimeLabel('2026-04-29T23:05:00.000Z', now), '1h ago');
  assert.equal(relativeTimeLabel('2026-04-28T00:05:00.000Z', now), '2d ago');
  assert.equal(relativeTimeLabel('invalid-date', now), 'invalid-date');
});

test('extractPacketSummaries parses and sorts packets by timestamp', () => {
  const packets = [
    { type: 'coder_context_packet', created_at: '2026-04-30T00:00:30.000Z' },
    { type: 'implementation_packet', created_at: '2026-04-30T00:02:00.000Z', summary: 'Done' },
    { type: 'review_request_packet', created_at: '2026-04-30T00:03:00.000Z' },
  ];

  const result = extractPacketSummaries(packets);
  assert.equal(result.length, 3);
  // Sorted newest first
  assert.equal(result[0].type, 'review_request_packet');
  assert.equal(result[0].label, 'Review requested');
  assert.equal(result[0].stage, 'review_requested');
  assert.equal(result[1].type, 'implementation_packet');
  assert.equal(result[1].details, 'Done');
  assert.equal(result[2].type, 'coder_context_packet');
});

test('extractPacketSummaries handles unknown types gracefully', () => {
  const packets = [
    { type: 'custom_unknown_packet', created_at: '2026-04-30T00:01:00.000Z' },
    { metadata_type: 'merge_summary', created_at: '2026-04-30T00:02:00.000Z' },
  ];

  const result = extractPacketSummaries(packets);
  assert.equal(result.length, 2);
  assert.equal(result[0].type, 'merge_summary');
  assert.equal(result[0].stage, 'merged');
  assert.equal(result[1].type, 'custom_unknown_packet');
  assert.equal(result[1].stage, 'planned');
  assert.equal(result[1].label, 'custom unknown packet');
});

test('deriveProgressStage finds the furthest stage from packets', () => {
  const packets = [
    { type: 'coder_context_packet', label: 'Context prepared', stage: 'context_prepared', timestamp: null, details: null },
    { type: 'implementation_packet', label: 'Implementation posted', stage: 'implementation_posted', timestamp: null, details: null },
  ];

  assert.equal(deriveProgressStage(packets, 'in_progress'), 'implementation_posted');
  assert.equal(deriveProgressStage([], 'planned'), 'planned');
  assert.equal(deriveProgressStage(packets, 'done'), 'done');
  assert.equal(deriveProgressStage(packets, 'cancelled'), 'planned');
});

test('progressStageIndex returns correct index', () => {
  assert.equal(progressStageIndex('planned'), 0);
  assert.equal(progressStageIndex('context_prepared'), 1);
  assert.equal(progressStageIndex('coder_running'), 2);
  assert.equal(progressStageIndex('implementation_posted'), 3);
  assert.equal(progressStageIndex('validation_passed'), 4);
  assert.equal(progressStageIndex('drift_check_complete'), 5);
  assert.equal(progressStageIndex('review_requested'), 6);
  assert.equal(progressStageIndex('approved'), 7);
  assert.equal(progressStageIndex('merged'), 8);
  assert.equal(progressStageIndex('done'), 9);
  // changes_requested is not in the stages list, so it returns 0 (fallback)
  assert.equal(progressStageIndex('changes_requested'), 0);
});

test('progressStageLabel returns human-readable labels', () => {
  assert.equal(progressStageLabel('planned'), 'Planned');
  assert.equal(progressStageLabel('coder_running'), 'Coder running');
  assert.equal(progressStageLabel('review_requested'), 'Review requested');
  assert.equal(progressStageLabel('done'), 'Done');
});

test('buildSessionChipView extracts chip data from record', () => {
  const chip = buildSessionChipView({
    session_id: 'sess-1',
    display_name: 'Pi coder #1029',
    backend: 'direct_pty',
    can_open_external_attach: true,
    external_attach_command: 'tmux a -t pi-1029',
  });

  assert.equal(chip.key, 'sess-1');
  assert.equal(chip.label, 'Pi coder #1029');
  assert.equal(chip.backend, 'direct_pty');
  assert.equal(chip.canAttach, true);
  assert.equal(chip.attachCommand, 'tmux a -t pi-1029');
});

test('buildSessionChipView handles minimal record', () => {
  const chip = buildSessionChipView({});
  assert.equal(chip.key, 'unknown');
  assert.equal(chip.label, 'unknown');
  assert.equal(chip.backend, null);
  assert.equal(chip.canAttach, false);
  assert.equal(chip.attachCommand, null);
});

test('taskStatusLabel replaces underscores with spaces', () => {
  assert.equal(taskStatusLabel('in_progress'), 'in progress');
  assert.equal(taskStatusLabel('needs_attention'), 'needs attention');
  assert.equal(taskStatusLabel('done'), 'done');
});

test('focused task is marked in the view', () => {
  const snapshot = makeSnapshot({ focused_task_id: 1029 });
  const view = buildDashboardView(snapshot, 1029);

  const focused = view.tasks.find((t) => t.id === 1029);
  const unfocused = view.tasks.find((t) => t.id === 909);

  assert.ok(focused);
  assert.equal(focused.isFocused, true);
  assert.ok(unfocused);
  assert.equal(unfocused.isFocused, false);
});

test('stale freshness detection based on generated_at timestamp', () => {
  const snapshot = makeSnapshot();
  // 5 minutes after generation = stale
  const view = buildDashboardView(snapshot, null, Date.parse('2026-04-30T00:10:00.000Z'));
  assert.equal(view.freshness.isStale, true);
});

test('partial freshness sets isStale flag', () => {
  const snapshot = makeSnapshot({
    freshness: { source: 'bridge', generated_at: '2026-04-30T00:05:00.000Z', is_partial: true, warnings: ['partial data'], errors: [] },
  });
  const view = buildDashboardView(snapshot, null, Date.parse('2026-04-30T00:05:10.000Z'));
  assert.equal(view.freshness.isPartial, true);
  assert.equal(view.freshness.isStale, true);
  assert.equal(view.freshness.warnings[0], 'partial data');
});

test('status panel includes focused task packet entries', () => {
  const snapshot = makeSnapshot({ focused_task_id: 1029 });
  const view = buildDashboardView(snapshot, 1029);

  // Should have Run overview + Task #1029 + Waves
  assert.ok(view.statusPanel.length >= 2);
  const taskSection = view.statusPanel.find((s) => s.heading.includes('1029'));
  assert.ok(taskSection);
  assert.ok(taskSection.entries.some((e) => e.label === 'Status'));
  assert.ok(taskSection.entries.some((e) => e.label === 'Context prepared'));
});

test('empty snapshot yields empty status panel', () => {
  const view = buildDashboardView(null);
  assert.equal(view.statusPanel.length, 0);
});
