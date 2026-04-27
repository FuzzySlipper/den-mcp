import assert from 'node:assert/strict';
import test from 'node:test';
import {
  filterThoughtItems,
  hasRawReasoningPreview,
  sortThoughtItems,
  summarizeThoughtItem,
  thoughtItemFromStreamEntry,
  thoughtItemsFromSubagentRunDetail,
} from '../../src/DenMcp.Server/ClientApp/src/thoughts.ts';

function streamEntry(overrides = {}) {
  return {
    id: 1,
    stream_kind: 'ops',
    event_type: 'agent_work_reasoning_update',
    project_id: 'den-mcp',
    task_id: 826,
    thread_id: null,
    dispatch_id: null,
    sender: 'pi',
    sender_instance_id: 'pi-test',
    recipient_agent: null,
    recipient_role: null,
    recipient_instance_id: null,
    delivery_mode: 'record_only',
    body: null,
    metadata: null,
    dedup_key: null,
    created_at: '2026-04-26T12:00:00',
    ...overrides,
  };
}

function runDetail(workEvents) {
  return {
    summary: {
      run_id: 'run-1234567890',
      state: 'running',
      schema: 'den_subagent_run',
      schema_version: 1,
      latest: streamEntry({ id: 99, event_type: 'subagent_work_message_end', sender: 'pi-subagent' }),
      started: null,
      role: 'coder',
      task_id: 826,
      project_id: 'den-mcp',
      backend: 'pi-cli',
      model: 'openai/gpt-test',
      output_status: null,
      timeout_kind: null,
      infrastructure_failure_reason: null,
      infrastructure_warning_reason: null,
      exit_code: null,
      signal: null,
      pid: null,
      stderr_preview: null,
      fallback_model: null,
      fallback_from_model: null,
      fallback_from_exit_code: null,
      heartbeat_count: 0,
      assistant_output_count: 0,
      last_heartbeat_at: null,
      last_assistant_output_at: null,
      duration_ms: null,
      artifact_dir: null,
      event_count: workEvents.length,
    },
    events: [],
    work_events: workEvents,
    artifacts: null,
  };
}

test('thought stream classification keeps reasoning/message entries and excludes tools', () => {
  const parentReasoning = thoughtItemFromStreamEntry(streamEntry({
    metadata: {
      schema: 'den_parent_agent_work',
      role: 'conductor',
      event: {
        type: 'agent.work_reasoning_update',
        ts: 1_000,
        project_id: 'den-mcp',
        task_id: 826,
        agent: 'pi',
        agent_role: 'conductor',
        reasoning_chars: 44,
        reasoning_redacted: false,
        text_preview: 'local raw preview',
      },
    },
  }));
  const parentTool = thoughtItemFromStreamEntry(streamEntry({
    id: 2,
    event_type: 'agent_work_tool_start',
    metadata: { event: { type: 'agent.work_tool_start', tool_name: 'bash' } },
  }));
  const parentMessage = thoughtItemFromStreamEntry(streamEntry({
    id: 3,
    event_type: 'agent_work_message_end',
    body: 'Assistant finished the plan.',
  }));
  const toolOnlyAssistantMessage = thoughtItemFromStreamEntry(streamEntry({
    id: 4,
    event_type: 'subagent_work_message_end',
    body: 'coder sub-agent produced an assistant message.',
    metadata: {
      role: 'coder',
      event: {
        type: 'subagent.work_message_end',
        tool_calls: [{ name: 'bash', args_preview: 'ls' }],
      },
    },
  }));

  assert.equal(parentTool, null);
  assert.equal(toolOnlyAssistantMessage, null);
  assert.equal(parentReasoning?.kind, 'reasoning');
  assert.equal(parentReasoning?.role, 'conductor');
  assert.equal(parentReasoning?.rawPreviewAvailable, true);
  assert.equal(summarizeThoughtItem(parentReasoning, false), '44 chars, raw preview hidden.');
  assert.equal(summarizeThoughtItem(parentReasoning, true), 'local raw preview');
  assert.equal(parentMessage?.kind, 'assistant_message');
  assert.equal(parentMessage?.textPreview, 'Assistant finished the plan.');
  assert.equal(hasRawReasoningPreview([parentReasoning, parentMessage].filter(Boolean)), true);
});

test('subagent run thoughts include reasoning and assistant messages without tool cards', () => {
  const items = thoughtItemsFromSubagentRunDetail(runDetail([
    {
      type: 'subagent.work_reasoning_update',
      ts: 2_000,
      run_id: 'run-1234567890',
      task_id: 826,
      subagent_role: 'coder',
      reasoning_chars: 12,
      reasoning_redacted: true,
    },
    {
      type: 'subagent.work_reasoning_update',
      ts: 3_000,
      run_id: 'run-1234567890',
      task_id: 826,
      subagent_role: 'coder',
      reasoning_chars: 18,
      reasoning_redacted: true,
    },
    {
      type: 'subagent.work_tool_start',
      ts: 4_000,
      tool_name: 'bash',
    },
    {
      type: 'subagent.work_message_update',
      ts: 5_000,
      run_id: 'run-1234567890',
      task_id: 826,
      subagent_role: 'coder',
      text_preview: 'partial text delta',
    },
    {
      type: 'subagent.work_message_end',
      ts: 6_000,
      run_id: 'run-1234567890',
      task_id: 826,
      subagent_role: 'coder',
      tool_calls: [{ name: 'bash', args_preview: 'rg thoughts' }],
    },
    {
      type: 'subagent.work_message_end',
      ts: 7_000,
      run_id: 'run-1234567890',
      task_id: 826,
      subagent_role: 'coder',
      text_preview: 'I found the relevant UI code.',
      model: 'gpt-test',
    },
  ]));

  assert.deepEqual(items.map(item => item.kind), ['reasoning', 'assistant_message']);
  assert.equal(items[0].runId, 'run-1234567890');
  assert.equal(items[0].reasoningChars, 18);
  assert.equal(items[1].textPreview, 'I found the relevant UI code.');
  assert.equal(items.every(item => item.run?.run_id === 'run-1234567890'), true);
});

test('redacted provider-visible reasoning summaries display without enabling Raw local', () => {
  const summary = 'Checked available context and identified the relevant UI path.';
  const item = thoughtItemFromStreamEntry(streamEntry({
    metadata: {
      schema: 'den_parent_agent_work',
      role: 'conductor',
      event: {
        type: 'agent.work_reasoning_update',
        ts: 1_000,
        reasoning_chars: 58,
        reasoning_redacted: true,
        reasoning_summary_preview: summary,
        reasoning_summary_source: 'provider_visible',
      },
    },
  }));

  assert.equal(item?.kind, 'reasoning');
  assert.equal(item?.reasoningSummaryPreview, summary);
  assert.equal(item?.rawPreviewAvailable, false);
  assert.equal(hasRawReasoningPreview([item].filter(Boolean)), false);
  assert.equal(summarizeThoughtItem(item, false), summary);
  assert.equal(summarizeThoughtItem(item, true), summary);
});

test('thought filters and sorting support project task agent role controls', () => {
  const first = thoughtItemFromStreamEntry(streamEntry({
    id: 1,
    project_id: 'den-mcp',
    task_id: 826,
    sender: 'pi',
    metadata: { role: 'conductor', event: { type: 'agent.work_message_end', ts: 1_000, text_preview: 'old' } },
  }));
  const second = thoughtItemFromStreamEntry(streamEntry({
    id: 2,
    project_id: 'other-project',
    task_id: 12,
    sender: 'worker',
    metadata: { role: 'reviewer', event: { type: 'agent.work_message_end', ts: 2_000, text_preview: 'new' } },
  }));
  const items = [first, second].filter(Boolean);

  assert.deepEqual(sortThoughtItems(items).map(item => item.textPreview), ['new', 'old']);
  assert.deepEqual(filterThoughtItems(items, { project: 'den', taskId: 826, agent: 'pi', role: 'conduct' }).map(item => item.textPreview), ['old']);
  assert.deepEqual(filterThoughtItems(items, { role: 'missing' }), []);
});
