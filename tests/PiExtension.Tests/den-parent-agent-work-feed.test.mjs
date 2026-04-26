import assert from 'node:assert/strict';
import test from 'node:test';
import {
  normalizeParentAgentWorkEvent,
  parentAgentOpsEventTypeForWorkEvent,
} from '../../pi-dev/extensions/den.ts';

const identity = {
  projectId: 'den-mcp',
  agent: 'pi',
  role: 'conductor',
  instanceId: 'pi-den-mcp-test',
  sessionId: 'pi:den-mcp:pi-den-mcp-test:/tmp/session.jsonl',
  taskId: 825,
  cwd: '/repo',
  sessionFile: '/tmp/session.jsonl',
  piSessionId: 'session-parent',
  model: 'openai/gpt-test',
};

function restoreEnv(name, value) {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

test('parent agent work normalization preserves assistant narrative without prompts', () => {
  assert.deepEqual(normalizeParentAgentWorkEvent({
    type: 'message_update',
    assistantMessageEvent: { type: 'text_delta' },
    message: {
      role: 'assistant',
      provider: 'openai',
      model: 'gpt-test',
      stopReason: 'stop',
      content: [{ type: 'text', text: 'Checking Den state.' }],
    },
  }, identity, 1234), {
    type: 'agent.work_message_update',
    ts: 1234,
    source_type: 'message_update',
    task_id: 825,
    backend: 'pi-extension',
    requested_model: 'openai/gpt-test',
    role: 'assistant',
    provider: 'openai',
    model: 'gpt-test',
    update_kind: 'text_delta',
    text_preview: 'Checking Den state.',
    text_chars: 19,
    content_types: ['text'],
    stop_reason: 'stop',
    project_id: 'den-mcp',
    agent: 'pi',
    agent_role: 'conductor',
    instance_id: 'pi-den-mcp-test',
    session_id: 'pi:den-mcp:pi-den-mcp-test:/tmp/session.jsonl',
    pi_session_id: 'session-parent',
    session_file: '/tmp/session.jsonl',
    cwd: '/repo',
  });

  assert.equal(normalizeParentAgentWorkEvent({
    type: 'message_end',
    message: { role: 'user', content: [{ type: 'text', text: 'raw user prompt' }] },
  }, identity, 1235), undefined);
  assert.equal(normalizeParentAgentWorkEvent({
    type: 'turn_end',
    message: { role: 'user', content: [{ type: 'text', text: 'raw user turn' }] },
  }, identity, 1236), undefined);
});

test('parent agent reasoning normalization is redacted by default and raw only by local opt-in', () => {
  const previous = process.env.DEN_PI_SUBAGENT_RAW_REASONING;
  delete process.env.DEN_PI_SUBAGENT_RAW_REASONING;
  try {
    assert.deepEqual(normalizeParentAgentWorkEvent({
      type: 'message_update',
      assistantMessageEvent: { type: 'thinking_delta', delta: 'private planning' },
      message: {
        role: 'assistant',
        provider: 'openai',
        model: 'gpt-test',
        content: [{ type: 'thinking', thinking: 'private planning' }],
      },
    }, identity, 1236), {
      type: 'agent.work_reasoning_update',
      ts: 1236,
      source_type: 'message_update',
      task_id: 825,
      backend: 'pi-extension',
      requested_model: 'openai/gpt-test',
      role: 'assistant',
      provider: 'openai',
      model: 'gpt-test',
      update_kind: 'thinking_delta',
      reasoning_kind: 'thinking_delta',
      reasoning_chars: 16,
      reasoning_redacted: true,
      content_types: ['thinking'],
      project_id: 'den-mcp',
      agent: 'pi',
      agent_role: 'conductor',
      instance_id: 'pi-den-mcp-test',
      session_id: 'pi:den-mcp:pi-den-mcp-test:/tmp/session.jsonl',
      pi_session_id: 'session-parent',
      session_file: '/tmp/session.jsonl',
      cwd: '/repo',
    });

    process.env.DEN_PI_SUBAGENT_RAW_REASONING = '1';
    const raw = normalizeParentAgentWorkEvent({
      type: 'message_update',
      assistantMessageEvent: { type: 'thinking_delta', delta: 'private planning' },
      message: {
        role: 'assistant',
        provider: 'openai',
        model: 'gpt-test',
        content: [{ type: 'thinking', thinking: 'private planning' }],
      },
    }, identity, 1237);
    assert.equal(raw?.reasoning_redacted, false);
    assert.equal(raw?.text_preview, 'private planning');
  } finally {
    restoreEnv('DEN_PI_SUBAGENT_RAW_REASONING', previous);
  }
});

test('parent agent work events map to agent stream ops event types', () => {
  assert.equal(parentAgentOpsEventTypeForWorkEvent({ type: 'agent.work_reasoning_update' }), 'agent_work_reasoning_update');
  assert.equal(parentAgentOpsEventTypeForWorkEvent({ type: 'subagent.work_reasoning_update' }), undefined);
});
