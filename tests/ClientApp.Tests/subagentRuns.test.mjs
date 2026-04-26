import assert from 'node:assert/strict';
import test from 'node:test';
import {
  formatInfrastructureFailureReason,
  formatSubagentDuration,
  formatSubagentWorkEventType,
  formatSubagentWorkTimestamp,
  stateFromSubagentEvent,
  subagentRunMatchesFilter,
  summarizeSubagentRunEntry,
  summarizeSubagentWorkEvent,
} from '../../src/DenMcp.Server/ClientApp/src/subagentRuns.ts';

function entry(overrides) {
  return {
    id: 1,
    stream_kind: 'ops',
    event_type: 'subagent_started',
    project_id: 'den-mcp',
    task_id: 775,
    thread_id: null,
    dispatch_id: null,
    sender: 'pi',
    sender_instance_id: 'pi-main',
    recipient_agent: null,
    recipient_role: null,
    recipient_instance_id: null,
    delivery_mode: 'record_only',
    body: null,
    metadata: null,
    dedup_key: null,
    created_at: '2026-04-25T04:00:00',
    ...overrides,
  };
}

test('subagent run helpers format labels and summaries', () => {
  assert.equal(stateFromSubagentEvent('subagent_started'), 'running');
  assert.equal(stateFromSubagentEvent('subagent_process_started'), 'running');
  assert.equal(stateFromSubagentEvent('subagent_heartbeat'), 'running');
  assert.equal(stateFromSubagentEvent('subagent_assistant_output'), 'running');
  assert.equal(stateFromSubagentEvent('subagent_work_tool_start'), 'running');
  assert.equal(stateFromSubagentEvent('subagent_fallback_started'), 'retrying');
  assert.equal(stateFromSubagentEvent('subagent_abort_requested'), 'aborting');
  assert.equal(stateFromSubagentEvent('subagent_rerun_requested'), 'rerun_requested');
  assert.equal(stateFromSubagentEvent('subagent_rerun_accepted'), 'rerun_accepted');
  assert.equal(stateFromSubagentEvent('subagent_rerun_unavailable'), 'failed');
  assert.equal(stateFromSubagentEvent('subagent_completed'), 'complete');
  assert.equal(stateFromSubagentEvent('subagent_timeout'), 'timeout');
  assert.equal(stateFromSubagentEvent('subagent_startup_timeout'), 'timeout');
  assert.equal(stateFromSubagentEvent('subagent_abort'), 'aborted');
  assert.equal(stateFromSubagentEvent('subagent_spawn_error'), 'failed');
  assert.equal(stateFromSubagentEvent('unknown_event'), 'unknown');
  assert.equal(formatSubagentDuration(999), '999ms');
  assert.equal(formatSubagentDuration(1014), '1.0s');
  assert.equal(formatSubagentDuration(61_000), '1m1s');
  assert.equal(formatInfrastructureFailureReason('extension_load'), 'extension load');
  assert.equal(formatInfrastructureFailureReason('child_error'), 'child process');
  assert.equal(formatInfrastructureFailureReason('forced_kill'), 'forced kill');
  assert.equal(formatInfrastructureFailureReason(null), '');
  assert.equal(summarizeSubagentRunEntry(entry({
    body: '  lots   of\nspace  ',
  })), 'lots of space');
  assert.equal(summarizeSubagentRunEntry(entry({
    event_type: 'subagent_failed',
  })), 'subagent failed');
});

test('subagent work event helpers render bounded live-feed summaries', () => {
  assert.equal(summarizeSubagentWorkEvent({
    type: 'subagent.work_tool_start',
    tool_name: 'bash',
    args_preview: '{"command":"dotnet test"}',
  }), 'tool started: bash {"command":"dotnet test"}');
  assert.equal(summarizeSubagentWorkEvent({
    type: 'subagent.work_tool_end',
    tool_name: 'bash',
    result_preview: 'Build failed',
    is_error: true,
  }), 'tool errored: bash Build failed');
  assert.equal(summarizeSubagentWorkEvent({
    type: 'subagent.work_message_end',
    tool_calls: [{ name: 'den_get_task' }, { name: 'bash' }],
  }), 'assistant requested tools: den_get_task, bash');
  assert.equal(summarizeSubagentWorkEvent({
    type: 'subagent.work_message_update',
    update_kind: 'thinking_delta',
  }), 'assistant update (thinking_delta)');
  assert.equal(formatSubagentWorkEventType('subagent.work_tool_start'), 'tool start');
  assert.equal(formatSubagentWorkTimestamp(1234), new Date(1234).toLocaleString());
  assert.equal(formatSubagentWorkTimestamp(null), '');
});

test('subagent run filters group operational states', () => {
  const run = state => ({ state });

  assert.equal(subagentRunMatchesFilter(run('running'), 'active'), true);
  assert.equal(subagentRunMatchesFilter(run('retrying'), 'active'), true);
  assert.equal(subagentRunMatchesFilter(run('aborting'), 'active'), true);
  assert.equal(subagentRunMatchesFilter(run('rerun_requested'), 'active'), true);
  assert.equal(subagentRunMatchesFilter(run('complete'), 'active'), false);
  assert.equal(subagentRunMatchesFilter(run('failed'), 'problem'), true);
  assert.equal(subagentRunMatchesFilter(run('timeout'), 'problem'), true);
  assert.equal(subagentRunMatchesFilter(run('aborted'), 'problem'), true);
  assert.equal(subagentRunMatchesFilter(run('unknown'), 'problem'), true);
  assert.equal(subagentRunMatchesFilter(run('complete'), 'complete'), true);
  assert.equal(subagentRunMatchesFilter(run('running'), 'all'), true);
});
