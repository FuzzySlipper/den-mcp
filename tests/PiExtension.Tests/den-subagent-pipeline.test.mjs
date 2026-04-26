import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createSubagentRunRecorder } from '../../pi-dev/lib/den-subagent-recorder.ts';
import {
  addSessionArgs,
  buildSubagentPrompt,
  runPiCliSubagent,
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
  normalizePiWorkEvent,
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

function restoreEnv(name, value) {
  if (value === undefined) delete process.env[name];
  else process.env[name] = value;
}

const FAKE_RUNNER_ENV = [
  'PI_CODING_AGENT_DIR',
  'DEN_PI_SUBAGENT_PI_BIN',
  'DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS',
  'DEN_PI_SUBAGENT_FINAL_DRAIN_MS',
  'DEN_PI_SUBAGENT_FORCE_KILL_MS',
  'DEN_PI_SUBAGENT_HEARTBEAT_MS',
  'DEN_PI_SUBAGENT_CONTROL_POLL_MS',
];

async function runFakePiSubagent(t, {
  prefix,
  scriptLines,
  runId,
  options,
  env = {},
  onUpdate,
}) {
  const tmp = await mkdtemp(path.join(os.tmpdir(), prefix));
  const fakePi = path.join(tmp, 'fake-pi');
  await writeFile(fakePi, `${scriptLines.join('\n')}\n`, 'utf8');
  await chmod(fakePi, 0o755);

  const envValues = {
    DEN_PI_SUBAGENT_HEARTBEAT_MS: '0',
    DEN_PI_SUBAGENT_CONTROL_POLL_MS: '0',
    ...env,
  };
  const envNames = new Set([...FAKE_RUNNER_ENV, ...Object.keys(envValues)]);
  const previousEnv = new Map([...envNames].map((name) => [name, process.env[name]]));
  process.env.PI_CODING_AGENT_DIR = path.join(tmp, 'agent');
  process.env.DEN_PI_SUBAGENT_PI_BIN = fakePi;
  for (const [name, value] of Object.entries(envValues)) {
    if (value === undefined) delete process.env[name];
    else process.env[name] = String(value);
  }

  t.after(async () => {
    for (const [name, value] of previousEnv) restoreEnv(name, value);
    await rm(tmp, { recursive: true, force: true });
  });

  const recorder = await createSubagentRunRecorder(runId);
  const result = await runPiCliSubagent({
    cfg: { projectId: 'den-mcp', agent: 'pi', role: 'conductor', instanceId: 'pi-main', baseUrl: 'http://den' },
    options,
    cwd: tmp,
    runId,
    recorder,
    startedAt: new Date().toISOString(),
    signal: undefined,
    controlSource: undefined,
    onUpdate,
  });

  return { tmp, fakePi, recorder, result };
}

async function readJson(filePath) {
  return JSON.parse(await readFile(filePath, 'utf8'));
}

async function readJsonLines(filePath) {
  const text = await readFile(filePath, 'utf8');
  return text.trim() ? text.trim().split('\n').map((line) => JSON.parse(line)) : [];
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

test('subagent work event normalizer summarizes child Pi events without prompts', () => {
  assert.deepEqual(normalizePiWorkEvent({
    type: 'tool_execution_start',
    toolCallId: 'tool-1',
    toolName: 'bash',
    args: { command: 'node --test tests/PiExtension.Tests/*.mjs' },
  }, 1234), {
    type: 'subagent.work_tool_start',
    ts: 1234,
    source_type: 'tool_execution_start',
    tool_call_id: 'tool-1',
    tool_name: 'bash',
    args_preview: '{"command":"node --test tests/PiExtension.Tests/*.mjs"}',
  });

  assert.deepEqual(normalizePiWorkEvent({
    type: 'tool_execution_end',
    toolCallId: 'tool-1',
    toolName: 'bash',
    result: { content: [{ type: 'text', text: 'ok\n' }] },
    isError: false,
  }, 1235), {
    type: 'subagent.work_tool_end',
    ts: 1235,
    source_type: 'tool_execution_end',
    tool_call_id: 'tool-1',
    tool_name: 'bash',
    result_preview: '{"content":[{"type":"text","text":"ok\\n"}]}',
    is_error: false,
  });

  assert.deepEqual(normalizePiWorkEvent({
    type: 'message_update',
    assistantMessageEvent: { type: 'text_delta' },
    message: assistantMessage('Running tests now'),
  }, 1236), {
    type: 'subagent.work_message_update',
    ts: 1236,
    source_type: 'message_update',
    role: 'assistant',
    model: 'gpt-test',
    update_kind: 'text_delta',
    text_preview: 'Running tests now',
    text_chars: 17,
    content_types: ['text'],
    stop_reason: 'stop',
  });

  assert.equal(normalizePiWorkEvent({
    type: 'message_update',
    assistantMessageEvent: { type: 'thinking_delta' },
    message: { role: 'assistant', content: [{ type: 'thinking', thinking: 'private scratchpad' }] },
  }, 1236), undefined);

  assert.equal(normalizePiWorkEvent({
    type: 'message_end',
    message: { role: 'user', content: [{ type: 'text', text: 'full generated prompt' }] },
  }, 1237), undefined);
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
    rerun_of_run_id: null,
    review_round_id: null,
    workspace_id: null,
    worktree_path: null,
    branch: null,
    base_branch: null,
    base_commit: null,
    head_commit: null,
    purpose: null,
    artifacts,
    output_status: 'assistant_final',
  });
  assert.deepEqual(buildSubagentRunMetadata({
    runId: 'run-review',
    role: 'reviewer',
    taskId: 808,
    cwd: '/repo/worktree',
    backend: 'pi-cli',
    reviewRoundId: 135,
    workspaceId: 'workspace-1',
    worktreePath: '/repo/worktree',
    branch: 'task/808-subagent-context-metadata',
    baseBranch: 'main',
    baseCommit: 'base-sha',
    headCommit: 'head-sha',
    purpose: 'Review Follow-Up',
  }), {
    schema: SUBAGENT_RUN_SCHEMA,
    schema_version: SUBAGENT_RUN_SCHEMA_VERSION,
    run_id: 'run-review',
    role: 'reviewer',
    task_id: 808,
    cwd: '/repo/worktree',
    backend: 'pi-cli',
    model: null,
    tools: null,
    session_mode: 'fresh',
    session: null,
    rerun_of_run_id: null,
    review_round_id: 135,
    workspace_id: 'workspace-1',
    worktree_path: '/repo/worktree',
    branch: 'task/808-subagent-context-metadata',
    base_branch: 'main',
    base_commit: 'base-sha',
    head_commit: 'head-sha',
    purpose: 'review_follow_up',
    artifacts: null,
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
  assert.equal(subagentOpsEventTypeForEvent('subagent.work_tool_start'), 'subagent_work_tool_start');
  assert.equal(subagentOpsEventTypeForEvent('subagent.work_message_update'), undefined);
  assert.equal(subagentOpsEventTypeForEvent('message_end'), undefined);
  assert.equal(subagentRunStateFromOpsEventType('subagent_assistant_output'), 'running');
  assert.equal(subagentRunStateFromOpsEventType('subagent_work_tool_start'), 'running');
  assert.equal(subagentRunStateFromOpsEventType('subagent_abort_requested'), 'aborting');
  assert.equal(subagentRunStateFromOpsEventType('subagent_rerun_requested'), 'rerun_requested');
  assert.equal(subagentRunStateFromOpsEventType('subagent_rerun_accepted'), 'rerun_accepted');
  assert.equal(subagentRunStateFromOpsEventType('subagent_rerun_unavailable'), 'failed');
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

test('pi cli runner observes Den abort control request and terminates child', async (t) => {
  const previousAgentDir = process.env.PI_CODING_AGENT_DIR;
  const previousPiBin = process.env.DEN_PI_SUBAGENT_PI_BIN;
  const previousPollMs = process.env.DEN_PI_SUBAGENT_CONTROL_POLL_MS;
  const previousStartupMs = process.env.DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS;
  const previousForceKillMs = process.env.DEN_PI_SUBAGENT_FORCE_KILL_MS;
  const tmp = await mkdtemp(path.join(os.tmpdir(), 'den-subagent-abort-'));
  const fakePi = path.join(tmp, 'fake-pi');
  await writeFile(fakePi, [
    '#!/usr/bin/env bash',
    'trap "exit 143" TERM',
    'while true; do sleep 1; done',
    '',
  ].join('\n'), 'utf8');
  await chmod(fakePi, 0o755);

  process.env.PI_CODING_AGENT_DIR = path.join(tmp, 'agent');
  process.env.DEN_PI_SUBAGENT_PI_BIN = fakePi;
  process.env.DEN_PI_SUBAGENT_CONTROL_POLL_MS = '25';
  process.env.DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS = '10000';
  process.env.DEN_PI_SUBAGENT_FORCE_KILL_MS = '1000';
  t.after(async () => {
    restoreEnv('PI_CODING_AGENT_DIR', previousAgentDir);
    restoreEnv('DEN_PI_SUBAGENT_PI_BIN', previousPiBin);
    restoreEnv('DEN_PI_SUBAGENT_CONTROL_POLL_MS', previousPollMs);
    restoreEnv('DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS', previousStartupMs);
    restoreEnv('DEN_PI_SUBAGENT_FORCE_KILL_MS', previousForceKillMs);
    await rm(tmp, { recursive: true, force: true });
  });

  const recorder = await createSubagentRunRecorder('run-abort-control');
  let polls = 0;
  const result = await runPiCliSubagent({
    cfg: { projectId: 'den-mcp', agent: 'pi', role: 'conductor', instanceId: 'pi-main', baseUrl: 'http://den' },
    options: { role: 'planner', prompt: 'wait forever' },
    cwd: tmp,
    runId: 'run-abort-control',
    recorder,
    startedAt: new Date().toISOString(),
    signal: undefined,
    controlSource: {
      async poll() {
        polls++;
        return polls === 1
          ? { action: 'abort', entryId: 42, requestedBy: 'web-ui', reason: 'test abort' }
          : undefined;
      },
    },
    onUpdate: undefined,
  });

  assert.equal(result.aborted, true);
  assert.equal(result.infrastructure_failure_reason, 'aborted');
  assert.equal(subagentSucceeded(result), false);

  const eventText = await readFile(recorder.artifacts.events_jsonl_path, 'utf8');
  const statusText = await readFile(recorder.artifacts.status_json_path, 'utf8');
  assert.match(eventText, /"type":"subagent.abort"/);
  assert.match(eventText, /"request_entry_id":42/);
  assert.match(eventText, /"requested_by":"web-ui"/);
  assert.match(statusText, /"state": "aborted"/);
});

test('pi cli runner suppresses prompt-echo-only output when child exits 143', async (t) => {
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-echo-143-',
    runId: 'run-prompt-echo-143',
    scriptLines: [
      '#!/usr/bin/env node',
      'const prompt = process.argv[process.argv.length - 1];',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: prompt }] } }));',
      'process.exit(143);',
    ],
    options: { role: 'reviewer', taskId: 772, prompt: 'Review the current branch and finish with a verdict.' },
  });

  assert.equal(result.exit_code, 143);
  assert.equal(result.signal, undefined);
  assert.equal(result.final_output, '');
  assert.equal(result.assistant_final_found, false);
  assert.equal(result.prompt_echo_detected, true);
  assert.equal(result.output_status, 'prompt_echo_only');
  assert.equal(result.message_count, 1);
  assert.equal(result.assistant_message_count, 1);
  assert.equal(subagentSucceeded(result), false);

  const status = await readJson(recorder.artifacts.status_json_path);
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  const stdout = await readJsonLines(recorder.artifacts.stdout_jsonl_path);
  assert.equal(status.state, 'failed');
  assert.equal(status.output_status, 'prompt_echo_only');
  assert.equal(status.exit_code, 143);
  assert.ok(events.some((event) => event.type === 'subagent.prompt_echo_detected'));
  assert.ok(stdout.some((event) => event.type === 'message_end'));
});

test('pi cli runner times out children that never emit JSON', async (t) => {
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-startup-timeout-',
    runId: 'run-no-json-startup-timeout',
    scriptLines: [
      '#!/usr/bin/env node',
      'process.stderr.write("fake child started without json\\n");',
      'process.on("SIGTERM", () => process.exit(143));',
      'setInterval(() => {}, 1000);',
    ],
    env: {
      DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS: '100',
      DEN_PI_SUBAGENT_FORCE_KILL_MS: '500',
    },
    options: { role: 'planner', prompt: 'Wait for JSON that will never arrive.' },
  });

  assert.equal(result.timeout_kind, 'startup');
  assert.equal(result.aborted, false);
  assert.equal(result.forced_kill, false);
  assert.equal(result.assistant_final_found, false);
  assert.equal(result.output_status, 'no_assistant_final');
  assert.equal(result.infrastructure_failure_reason, 'timeout');
  assert.equal(subagentSucceeded(result), false);
  assert.ok(result.pid > 0);
  assert.ok(Date.parse(result.started_at) <= Date.parse(result.ended_at));
  assert.ok(result.duration_ms >= 0);
  assert.match(result.stderr_tail, /fake child started without json/);

  const status = await readJson(recorder.artifacts.status_json_path);
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  const stderr = await readFile(recorder.artifacts.stderr_log_path, 'utf8');
  assert.equal(status.state, 'timeout');
  assert.equal(status.timeout_kind, 'startup');
  assert.equal(status.pid, result.pid);
  assert.ok(events.some((event) => event.type === 'subagent.process_started'));
  assert.ok(events.some((event) => event.type === 'subagent.startup_timeout'));
  assert.ok(events.some((event) => event.type === 'subagent.process_finished'));
  assert.match(stderr, /fake child started without json/);
});

test('pi cli runner preserves assistant final output when terminal drain guard kills stuck child', async (t) => {
  const updates = [];
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-terminal-drain-',
    runId: 'run-terminal-drain-final-output',
    scriptLines: [
      '#!/usr/bin/env node',
      'process.on("SIGTERM", () => process.exit(143));',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "final answer before stuck handles" }] } }));',
      'setInterval(() => {}, 1000);',
    ],
    env: {
      DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS: '1000',
      DEN_PI_SUBAGENT_FINAL_DRAIN_MS: '25',
      DEN_PI_SUBAGENT_FORCE_KILL_MS: '500',
    },
    options: { role: 'coder', prompt: 'Produce a final answer and then keep handles open.' },
    onUpdate(partial) {
      updates.push(partial);
    },
  });

  assert.equal(result.final_output, 'final answer before stuck handles');
  assert.equal(result.assistant_final_found, true);
  assert.equal(result.prompt_echo_detected, false);
  assert.equal(result.output_status, 'assistant_final');
  assert.equal(result.timeout_kind, 'terminal_drain');
  assert.equal(result.forced_kill, false);
  assert.equal(subagentSucceeded(result), true);
  assert.deepEqual(updates, ['final answer before stuck handles']);

  const status = await readJson(recorder.artifacts.status_json_path);
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  assert.equal(status.state, 'complete');
  assert.equal(status.timeout_kind, 'terminal_drain');
  assert.equal(status.output_status, 'assistant_final');
  assert.ok(events.some((event) => event.type === 'subagent.assistant_output'));
  assert.ok(events.some((event) => event.type === 'subagent.terminal_drain_timeout'));
  assert.ok(events.some((event) => event.type === 'subagent.process_finished'));
});

test('pi cli runner carries conductor context into status, result, and lifecycle events', async (t) => {
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-context-metadata-',
    runId: 'run-context-metadata',
    scriptLines: [
      '#!/usr/bin/env node',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "context preserved" }] } }));',
      'process.exit(0);',
    ],
    options: {
      role: 'reviewer',
      taskId: 808,
      prompt: 'Review the current branch.',
      reviewRoundId: 135,
      workspaceId: 'workspace-1',
      worktreePath: '/tmp/worktrees/den-808',
      branch: 'task/808-subagent-context-metadata',
      baseBranch: 'main',
      baseCommit: 'base-sha',
      headCommit: 'head-sha',
      purpose: 'Review Follow-Up',
    },
  });

  assert.equal(result.exit_code, 0);
  assert.equal(result.review_round_id, 135);
  assert.equal(result.workspace_id, 'workspace-1');
  assert.equal(result.worktree_path, '/tmp/worktrees/den-808');
  assert.equal(result.branch, 'task/808-subagent-context-metadata');
  assert.equal(result.base_branch, 'main');
  assert.equal(result.base_commit, 'base-sha');
  assert.equal(result.head_commit, 'head-sha');
  assert.equal(result.purpose, 'review_follow_up');

  const status = await readJson(recorder.artifacts.status_json_path);
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  assert.equal(status.review_round_id, 135);
  assert.equal(status.workspace_id, 'workspace-1');
  assert.equal(status.worktree_path, '/tmp/worktrees/den-808');
  assert.equal(status.branch, 'task/808-subagent-context-metadata');
  assert.equal(status.base_branch, 'main');
  assert.equal(status.base_commit, 'base-sha');
  assert.equal(status.head_commit, 'head-sha');
  assert.equal(status.purpose, 'review_follow_up');
  assert.ok(events.some((event) => event.type === 'subagent.process_started' && event.review_round_id === 135 && event.purpose === 'review_follow_up'));
  assert.ok(events.some((event) => event.type === 'subagent.process_finished' && event.review_round_id === 135 && event.branch === 'task/808-subagent-context-metadata'));
});

test('pi cli runner records normalized work events from child Pi stream', async (t) => {
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-work-events-',
    runId: 'run-normalized-work-events',
    scriptLines: [
      '#!/usr/bin/env node',
      'console.log(JSON.stringify({ type: "turn_start" }));',
      'console.log(JSON.stringify({ type: "tool_execution_start", toolCallId: "tool-1", toolName: "bash", args: { command: "echo ok" } }));',
      'console.log(JSON.stringify({ type: "tool_execution_end", toolCallId: "tool-1", toolName: "bash", result: { content: [{ type: "text", text: "ok\\n" }] }, isError: false }));',
      'console.log(JSON.stringify({ type: "message_update", assistantMessageEvent: { type: "text_delta" }, message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "Tests passed." }] } }));',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "final answer" }] } }));',
      'process.exit(0);',
    ],
    options: { role: 'coder', prompt: 'Run a tool and summarize it.' },
  });

  assert.equal(result.exit_code, 0);
  assert.equal(result.final_output, 'final answer');
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  assert.ok(events.some((event) => event.type === 'subagent.work_turn_start'));
  assert.ok(events.some((event) => event.type === 'subagent.work_tool_start' && event.tool_name === 'bash' && event.args_preview.includes('echo ok')));
  assert.ok(events.some((event) => event.type === 'subagent.work_tool_end' && event.result_preview.includes('ok') && event.is_error === false));
  assert.ok(events.some((event) => event.type === 'subagent.work_message_update' && event.text_preview === 'Tests passed.'));
  assert.ok(events.some((event) => event.type === 'subagent.work_message_end' && event.text_preview === 'final answer'));
  assert.ok(events.some((event) => event.type === 'subagent.assistant_output'));
});

test('pi cli runner does not terminal-drain partial assistant tool-use turns', async (t) => {
  const updates = [];
  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-tool-use-preface-',
    runId: 'run-tool-use-preface-not-final',
    scriptLines: [
      '#!/usr/bin/env node',
      'process.on("SIGTERM", () => process.exit(143));',
      'console.log(JSON.stringify({ type: "message_update", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "Now let me run the tests." }] } }));',
      'setTimeout(() => console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "toolUse", content: [{ type: "text", text: "Now let me run the tests." }, { type: "toolCall", id: "tool-1", name: "bash", arguments: { command: "node --test" } }] } })), 50);',
      'setTimeout(() => console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "actual final verdict" }] } })), 90);',
      'setTimeout(() => process.exit(0), 100);',
    ],
    env: {
      DEN_PI_SUBAGENT_STARTUP_TIMEOUT_MS: '1000',
      DEN_PI_SUBAGENT_FINAL_DRAIN_MS: '25',
      DEN_PI_SUBAGENT_FORCE_KILL_MS: '500',
    },
    options: { role: 'reviewer', prompt: 'Review the tests, then produce a final verdict.' },
    onUpdate(partial) {
      updates.push(partial);
    },
  });

  assert.equal(result.exit_code, 0);
  assert.equal(result.timeout_kind, undefined);
  assert.equal(result.final_output, 'actual final verdict');
  assert.equal(result.assistant_final_found, true);
  assert.equal(result.output_status, 'assistant_final');
  assert.equal(subagentSucceeded(result), true);
  assert.deepEqual(updates, ['actual final verdict']);

  const status = await readJson(recorder.artifacts.status_json_path);
  const events = await readJsonLines(recorder.artifacts.events_jsonl_path);
  assert.equal(status.state, 'complete');
  assert.equal(status.timeout_kind, null);
  assert.equal(status.output_status, 'assistant_final');
  assert.ok(events.some((event) => event.type === 'subagent.assistant_output'));
  assert.equal(events.some((event) => event.type === 'subagent.terminal_drain_timeout'), false);
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

test('output extractor ignores assistant tool-use prefaces as final output', () => {
  const events = [];
  const extractor = createSubagentOutputExtractor('Run tests', {
    appendEvent(event) {
      events.push(event);
    },
  });

  const output = extractor.updateFromEvent({
    type: 'message_end',
    message: assistantMessage('Now let me run the tests.', {
      stopReason: 'toolUse',
      content: [
        { type: 'text', text: 'Now let me run the tests.' },
        { type: 'toolCall', id: 'tool-1', name: 'bash', arguments: { command: 'node --test' } },
      ],
    }),
  });

  assert.equal(output, undefined);
  assert.deepEqual(extractor.snapshot(), {
    finalOutput: '',
    model: 'gpt-test',
    messageCount: 1,
    assistantMessageCount: 1,
    promptEchoDetected: false,
    childErrorMessage: undefined,
  });
  assert.deepEqual(events, []);
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
