import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createSubagentRunRecorder } from '../../pi-dev/lib/den-subagent-recorder.ts';
import {
  addSessionArgs,
  buildSubagentPrompt,
  subagentSucceeded,
} from '../../pi-dev/lib/den-subagent-runner.ts';
import {
  SUBAGENT_RUN_SCHEMA,
  SUBAGENT_RUN_SCHEMA_VERSION,
  buildSubagentRunMetadata,
  classifySubagentInfrastructureFailure,
  classifySubagentStderrIssue,
  createSubagentOutputExtractor,
  isSubagentInfrastructureFailure,
  isTerminalAssistantMessage,
  normalizeSubagentRunEvent,
  parsePiStdoutLine,
  subagentOpsEventTypeForEvent,
  subagentRunStateFromOpsEventType,
} from '../../pi-dev/lib/den-subagent-pipeline.ts';

function assistantMessage(text, extra = {}) {
  return {
    role: 'assistant',
    model: 'gpt-test',
    stopReason: 'stop',
    content: [{ type: 'text', text }],
    ...extra,
  };
}

test('parsePiStdoutLine preserves json and raw stdout separately', () => {
  const json = parsePiStdoutLine('{"type":"message_end","message":{"role":"assistant"}}');
  assert.equal(json?.kind, 'json');
  assert.equal(json?.line, '{"type":"message_end","message":{"role":"assistant"}}');
  assert.equal(json?.event.type, 'message_end');

  const raw = parsePiStdoutLine('not json');
  assert.deepEqual(raw, { kind: 'raw_stdout', line: 'not json' });

  assert.equal(parsePiStdoutLine('   '), undefined);
});

test('subagent run schema helpers emit canonical metadata and event mapping', () => {
  const artifacts = {
    dir: '/tmp/den-subagent-runs/run-1',
    stdout_jsonl_path: '/tmp/den-subagent-runs/run-1/stdout.jsonl',
    stderr_log_path: '/tmp/den-subagent-runs/run-1/stderr.log',
    status_json_path: '/tmp/den-subagent-runs/run-1/status.json',
    events_jsonl_path: '/tmp/den-subagent-runs/run-1/events.jsonl',
  };
  assert.deepEqual(buildSubagentRunMetadata({
    runId: 'run-1',
    role: 'planner',
    taskId: 775,
    cwd: '/repo',
    backend: 'pi-cli',
    model: 'gpt-5.5',
    sessionMode: 'fresh',
    artifacts,
  }, { output_status: 'assistant_final' }), {
    schema: SUBAGENT_RUN_SCHEMA,
    schema_version: SUBAGENT_RUN_SCHEMA_VERSION,
    run_id: 'run-1',
    role: 'planner',
    task_id: 775,
    cwd: '/repo',
    backend: 'pi-cli',
    model: 'gpt-5.5',
    tools: null,
    session_mode: 'fresh',
    session: null,
    artifacts,
    output_status: 'assistant_final',
  });
  assert.deepEqual(normalizeSubagentRunEvent({
    type: 'subagent.heartbeat',
    duration_ms: 1200,
  }), {
    schema: SUBAGENT_RUN_SCHEMA,
    schema_version: SUBAGENT_RUN_SCHEMA_VERSION,
    type: 'subagent.heartbeat',
    duration_ms: 1200,
  });
  assert.equal(subagentOpsEventTypeForEvent('subagent.heartbeat'), 'subagent_heartbeat');
  assert.equal(subagentOpsEventTypeForEvent('subagent.spawn_error'), 'subagent_spawn_error');
  assert.equal(subagentOpsEventTypeForEvent('message_end'), undefined);
  assert.equal(subagentRunStateFromOpsEventType('subagent_assistant_output'), 'running');
  assert.equal(subagentRunStateFromOpsEventType('subagent_completed'), 'complete');
  assert.equal(subagentRunStateFromOpsEventType('subagent_failed'), 'failed');
  assert.equal(subagentRunStateFromOpsEventType('something_else'), 'unknown');
});

test('subagent run recorder writes normalized artifacts and ordered progress events', async (t) => {
  const previousAgentDir = process.env.PI_CODING_AGENT_DIR;
  const agentDir = await mkdtemp(path.join(os.tmpdir(), 'den-subagent-recorder-'));
  process.env.PI_CODING_AGENT_DIR = agentDir;
  t.after(async () => {
    if (previousAgentDir === undefined) delete process.env.PI_CODING_AGENT_DIR;
    else process.env.PI_CODING_AGENT_DIR = previousAgentDir;
    await rm(agentDir, { recursive: true, force: true });
  });

  const progress = [];
  const recorder = await createSubagentRunRecorder('run-recorder', {
    progressPublisher(event) {
      progress.push(event);
    },
  });

  await recorder.writeStatus({ state: 'starting', run_id: 'run-recorder' });
  await recorder.appendEvent({ type: 'subagent.heartbeat', duration_ms: 1200 });
  await recorder.flushEvents();
  await recorder.appendStdoutLine('{"type":"message_end"}');
  await recorder.appendRawStdout('plain output');
  await recorder.appendStderr('stderr line\n');

  const eventText = await readFile(recorder.artifacts.events_jsonl_path, 'utf8');
  const statusText = await readFile(recorder.artifacts.status_json_path, 'utf8');
  const stdoutText = await readFile(recorder.artifacts.stdout_jsonl_path, 'utf8');
  const stderrText = await readFile(recorder.artifacts.stderr_log_path, 'utf8');

  assert.equal(recorder.artifacts.dir, path.join(agentDir, 'den-subagent-runs', 'run-recorder'));
  assert.match(statusText, /"state": "starting"/);
  assert.match(eventText, /"schema":"den_subagent_run"/);
  assert.match(eventText, /"type":"subagent.heartbeat"/);
  assert.equal(progress.length, 1);
  assert.equal(progress[0].schema, SUBAGENT_RUN_SCHEMA);
  assert.equal(progress[0].schema_version, SUBAGENT_RUN_SCHEMA_VERSION);
  assert.match(stdoutText, /"type":"message_end"/);
  assert.match(stdoutText, /"type":"raw_stdout"/);
  assert.equal(stderrText, 'stderr line\n');
});

test('pi cli runner helpers keep prompt, session, and success semantics stable', () => {
  const prompt = buildSubagentPrompt(
    { projectId: 'den-mcp', agent: 'pi', role: 'conductor', instanceId: 'pi-main', baseUrl: 'http://den' },
    { role: 'planner', taskId: 775, prompt: 'Reply with exactly: OK' },
  );
  assert.match(prompt, /fresh planner sub-agent/);
  assert.match(prompt, /Project: den-mcp/);
  assert.match(prompt, /Den task: #775/);
  assert.match(prompt, /Reply with exactly: OK/);

  const freshArgs = [];
  addSessionArgs(freshArgs, 'fresh');
  assert.deepEqual(freshArgs, ['--no-session']);

  const forkArgs = [];
  addSessionArgs(forkArgs, 'fork', 'session-1');
  assert.deepEqual(forkArgs, ['--fork', 'session-1']);
  assert.throws(() => addSessionArgs([], 'session'), /session is required/);

  assert.equal(subagentSucceeded({ assistant_final_found: true, aborted: false, exit_code: 0 }), true);
  assert.equal(subagentSucceeded({
    assistant_final_found: true,
    aborted: false,
    exit_code: 143,
    timeout_kind: 'terminal_drain',
  }), true);
  assert.equal(subagentSucceeded({ assistant_final_found: false, aborted: false, exit_code: 0 }), false);
  assert.equal(subagentSucceeded({ assistant_final_found: true, aborted: true, exit_code: 0 }), false);
});

test('output extractor accepts assistant final output and records model', () => {
  const events = [];
  const extractor = createSubagentOutputExtractor('Say hi', {
    appendEvent(event) {
      events.push(event);
    },
  });

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: assistantMessage('hello'),
  });

  assert.equal(output, 'hello');
  assert.deepEqual(extractor.snapshot(), {
    finalOutput: 'hello',
    model: 'gpt-test',
    messageCount: 1,
    assistantMessageCount: 1,
    promptEchoDetected: false,
    childErrorMessage: undefined,
  });
  assert.equal(events[0].type, 'subagent.assistant_output');
});

test('output extractor ignores user prompt echoes', () => {
  const prompt = 'Reply with exactly: SUBAGENT_SMOKE_OK';
  const extractor = createSubagentOutputExtractor(prompt);

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: {
      role: 'user',
      content: [{ type: 'text', text: prompt }],
    },
  });

  assert.equal(output, undefined);
  assert.equal(extractor.snapshot().finalOutput, '');
  assert.equal(extractor.snapshot().promptEchoDetected, false);
  assert.equal(extractor.snapshot().messageCount, 1);
  assert.equal(extractor.snapshot().assistantMessageCount, 0);
});

test('output extractor classifies assistant prompt echoes as unusable', () => {
  const prompt = 'This is a deliberately long prompt that should be detected if an assistant echoes the beginning of it back instead of producing an actual answer.';
  const events = [];
  const extractor = createSubagentOutputExtractor(prompt, {
    appendEvent(event) {
      events.push(event);
    },
  });

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: assistantMessage(`${prompt}\n\nMore prompt material.`),
  });

  assert.equal(output, undefined);
  assert.equal(extractor.snapshot().finalOutput, '');
  assert.equal(extractor.snapshot().promptEchoDetected, true);
  assert.equal(extractor.snapshot().assistantMessageCount, 1);
  assert.equal(events[0].type, 'subagent.prompt_echo_detected');
});

test('terminal assistant detection excludes tool-call messages', () => {
  assert.equal(isTerminalAssistantMessage(assistantMessage('done')), true);
  assert.equal(isTerminalAssistantMessage(assistantMessage('needs tool', {
    content: [{ type: 'text', text: 'needs tool' }, { type: 'toolCall', name: 'search' }],
  })), false);
  assert.equal(isTerminalAssistantMessage({ role: 'user', content: [{ type: 'text', text: 'hello' }] }), false);
});

test('infrastructure failures are classified before fallback retry', () => {
  assert.equal(isSubagentInfrastructureFailure({ timeout_kind: 'startup' }), true);
  assert.equal(isSubagentInfrastructureFailure({ forced_kill: true }), true);
  assert.equal(isSubagentInfrastructureFailure({ signal: 'SIGTERM' }), true);
  assert.equal(isSubagentInfrastructureFailure({ child_error_message: 'spawn ENOENT' }), true);
  assert.equal(classifySubagentInfrastructureFailure({
    stderr_tail: 'Error: Failed to load extension "/tmp/bad.ts": Extension does not export a valid factory function',
  }), 'extension_load');
  assert.equal(classifySubagentInfrastructureFailure({
    stderr: 'Extension error (/tmp/footer.ts): This extension ctx is stale after session replacement or reload.',
  }), 'extension_runtime');
  assert.equal(classifySubagentStderrIssue(
    'Extension error (/tmp/footer.ts): This extension ctx is stale after session replacement or reload.',
  ), 'extension_runtime');
  assert.equal(isSubagentInfrastructureFailure({}), false);
});
