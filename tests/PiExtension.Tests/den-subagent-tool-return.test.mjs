import assert from 'node:assert/strict';
import test from 'node:test';
import { SessionManager } from '/usr/lib/node_modules/@mariozechner/pi-coding-agent/dist/core/session-manager.js';
import { convertToLlm } from '/usr/lib/node_modules/@mariozechner/pi-coding-agent/dist/core/messages.js';
import { serializeConversation } from '/usr/lib/node_modules/@mariozechner/pi-coding-agent/dist/core/compaction/utils.js';
import { convertResponsesMessages } from '/usr/lib/node_modules/@mariozechner/pi-coding-agent/node_modules/@mariozechner/pi-ai/dist/providers/openai-responses-shared.js';
import { buildSubagentParentToolResult } from '../../pi-dev/lib/den-subagent-parent-tool-result.ts';

const BASE_ARTIFACTS = {
  dir: '/tmp/den-subagent-runs/run-test',
  stdout_jsonl_path: '/tmp/den-subagent-runs/run-test/stdout.jsonl',
  stderr_log_path: '/tmp/den-subagent-runs/run-test/stderr.log',
  status_json_path: '/tmp/den-subagent-runs/run-test/status.json',
  events_jsonl_path: '/tmp/den-subagent-runs/run-test/events.jsonl',
  session_dir: '/tmp/den-subagent-runs/run-test/sessions',
  session_file_path: '/tmp/den-subagent-runs/run-test/sessions/session.jsonl',
};

function subagentResult(overrides = {}) {
  return {
    run_id: 'run-test',
    role: 'coder',
    task_id: 851,
    review_round_id: undefined,
    workspace_id: undefined,
    worktree_path: undefined,
    branch: 'task/851-slim-subagent-tool-returns',
    base_branch: 'main',
    base_commit: 'base-sha',
    head_commit: 'head-sha',
    purpose: 'implementation',
    session_mode: 'fresh',
    session: undefined,
    pi_session_id: 'pi-session-test',
    pi_session_dir: BASE_ARTIFACTS.session_dir,
    pi_session_file_path: BASE_ARTIFACTS.session_file_path,
    pi_session_persisted: true,
    exit_code: 0,
    signal: undefined,
    pid: 1234,
    backend: 'pi-cli',
    started_at: '2026-04-26T00:00:00.000Z',
    ended_at: '2026-04-26T00:00:05.000Z',
    duration_ms: 5000,
    aborted: false,
    timeout_kind: undefined,
    forced_kill: false,
    final_output: 'Implemented the requested change and ran focused tests.',
    assistant_final_found: true,
    prompt_echo_detected: false,
    output_status: 'assistant_final',
    stderr: '',
    stderr_tail: '',
    model: 'openai-codex/gpt-5.5',
    message_count: 9,
    assistant_message_count: 3,
    child_error_message: undefined,
    infrastructure_failure_reason: undefined,
    infrastructure_warning_reason: undefined,
    artifacts: BASE_ARTIFACTS,
    fallback_from_model: undefined,
    fallback_from_exit_code: undefined,
    ...overrides,
  };
}

function serializedToolResult(result) {
  return JSON.stringify(result);
}

test('sub-agent parent tool return is compact for successful long final output', () => {
  const longFinal = `SUMMARY_START ${'x'.repeat(10_000)} SUMMARY_END`;
  const toolResult = buildSubagentParentToolResult(subagentResult({ final_output: longFinal }));
  const text = toolResult.content[0].text;

  assert.equal(toolResult.isError, false);
  assert.equal(toolResult.details.schema, 'den_subagent_parent_tool_result');
  assert.equal(toolResult.details.run_id, 'run-test');
  assert.equal(toolResult.details.role, 'coder');
  assert.equal(toolResult.details.task_id, 851);
  assert.equal(toolResult.details.state, 'completed');
  assert.equal(toolResult.details.final_output_truncated, true);
  assert.equal(toolResult.details.final_output_chars, longFinal.length);
  assert.match(text, /Sub-agent completed \(coder\)/);
  assert.match(text, /Final summary \(bounded parent copy\):/);
  assert.match(text, /omitted from the parent tool result/);
  assert.ok(text.length < 2_500, `tool content should stay compact, got ${text.length}`);
  assert.ok(serializedToolResult(toolResult).length < 6_000, 'details should not carry huge final output metadata');
});

test('sub-agent parent tool return omits verbose stderr and raw child transcripts', () => {
  const toolResult = buildSubagentParentToolResult(subagentResult({
    exit_code: 1,
    assistant_final_found: false,
    output_status: 'no_assistant_final',
    final_output: '',
    stderr: `VERBOSE_STDERR_SENTINEL ${'stderr '.repeat(20_000)}`,
    stderr_tail: 'STDERR_TAIL_SENTINEL should not be returned to parent context',
    child_error_message: `CHILD_ERROR_SENTINEL ${'child '.repeat(1_000)}`,
    infrastructure_failure_reason: 'child_error',
    stdout: 'RAW_STDOUT_SENTINEL should not be copied from accidental result fields',
    work_events: [{ result_preview: 'RAW_WORK_EVENT_SENTINEL should not be copied' }],
    session_transcript: 'RAW_SESSION_TRANSCRIPT_SENTINEL should not be copied',
    massive_metadata: { nested: 'MASSIVE_METADATA_SENTINEL'.repeat(5_000) },
  }));

  const serialized = serializedToolResult(toolResult);
  assert.equal(toolResult.isError, true);
  assert.equal(toolResult.details.state, 'infrastructure_failed');
  assert.equal(toolResult.details.stderr, undefined);
  assert.equal(toolResult.details.stderr_tail, undefined);
  assert.equal(toolResult.details.work_events, undefined);
  assert.equal(toolResult.details.session_transcript, undefined);
  assert.equal(toolResult.details.massive_metadata, undefined);
  assert.ok(toolResult.details.child_error_truncated, 'child error preview should be bounded');
  assert.match(toolResult.content[0].text, /Failure summary \(bounded parent copy\):/);
  assert.match(toolResult.content[0].text, /CHILD_ERROR_SENTINEL/);
  assert.doesNotMatch(serialized, /VERBOSE_STDERR_SENTINEL/);
  assert.doesNotMatch(serialized, /STDERR_TAIL_SENTINEL/);
  assert.doesNotMatch(serialized, /RAW_STDOUT_SENTINEL/);
  assert.doesNotMatch(serialized, /RAW_WORK_EVENT_SENTINEL/);
  assert.doesNotMatch(serialized, /RAW_SESSION_TRANSCRIPT_SENTINEL/);
  assert.doesNotMatch(serialized, /MASSIVE_METADATA_SENTINEL/);
  assert.ok(serialized.length < 7_000, `tool result should stay bounded, got ${serialized.length}`);
});

test('sub-agent parent tool return keeps artifact paths without artifact contents', () => {
  const toolResult = buildSubagentParentToolResult(subagentResult({
    artifacts: {
      ...BASE_ARTIFACTS,
      dir: '/tmp/den-subagent-runs/artifact-heavy',
      stdout_jsonl_path: '/tmp/den-subagent-runs/artifact-heavy/stdout.jsonl',
      stderr_log_path: '/tmp/den-subagent-runs/artifact-heavy/stderr.log',
      status_json_path: '/tmp/den-subagent-runs/artifact-heavy/status.json',
      events_jsonl_path: '/tmp/den-subagent-runs/artifact-heavy/events.jsonl',
      session_dir: '/tmp/den-subagent-runs/artifact-heavy/sessions',
      session_file_path: '/tmp/den-subagent-runs/artifact-heavy/sessions/session.jsonl',
      stdout_jsonl_content: 'RAW_ARTIFACT_STDOUT_CONTENT_SENTINEL',
      events_jsonl_content: 'RAW_ARTIFACT_WORK_EVENT_CONTENT_SENTINEL',
    },
  }));

  assert.equal(toolResult.details.artifacts.dir, '/tmp/den-subagent-runs/artifact-heavy');
  assert.equal(toolResult.details.artifacts.events_jsonl_path, '/tmp/den-subagent-runs/artifact-heavy/events.jsonl');
  assert.equal(toolResult.details.artifacts.session_file_path, '/tmp/den-subagent-runs/artifact-heavy/sessions/session.jsonl');
  const serialized = serializedToolResult(toolResult);
  assert.doesNotMatch(serialized, /RAW_ARTIFACT_STDOUT_CONTENT_SENTINEL/);
  assert.doesNotMatch(serialized, /RAW_ARTIFACT_WORK_EVENT_CONTENT_SENTINEL/);
});

test('Pi parent session stores tool-result details, while compaction/provider payloads use content only', () => {
  const sentinel = 'DETAILS_SENTINEL_SHOULD_NOT_REACH_PROVIDER_PAYLOAD';
  const toolMessage = {
    role: 'toolResult',
    toolCallId: 'call_1|fc_1',
    toolName: 'den_run_subagent',
    content: [{ type: 'text', text: 'VISIBLE_PARENT_TOOL_CONTENT' }],
    details: { sentinel, nested: { stderr: 'VERBOSE_DETAILS_STDERR' } },
    isError: false,
    timestamp: Date.now(),
  };

  const session = SessionManager.inMemory('/repo');
  session.appendMessage(toolMessage);
  const parentContext = session.buildSessionContext();
  assert.equal(parentContext.messages[0].details.sentinel, sentinel, 'Pi session context retains tool-result details');

  const llmMessages = convertToLlm(parentContext.messages);
  assert.equal(llmMessages[0].details.sentinel, sentinel, 'Pi convertToLlm passes tool-result details through to provider adapters');

  const compacted = serializeConversation(llmMessages);
  assert.match(compacted, /VISIBLE_PARENT_TOOL_CONTENT/);
  assert.doesNotMatch(compacted, new RegExp(sentinel));

  const model = {
    id: 'gpt-test',
    name: 'gpt-test',
    api: 'openai-responses',
    provider: 'openai',
    input: ['text'],
    reasoning: false,
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
    contextWindow: 128000,
    maxTokens: 16384,
  };
  const providerPayload = convertResponsesMessages(model, { messages: llmMessages }, new Set(['openai']), {});
  const providerPayloadText = JSON.stringify(providerPayload);
  assert.match(providerPayloadText, /VISIBLE_PARENT_TOOL_CONTENT/);
  assert.doesNotMatch(providerPayloadText, new RegExp(sentinel));
  assert.doesNotMatch(providerPayloadText, /VERBOSE_DETAILS_STDERR/);
});
