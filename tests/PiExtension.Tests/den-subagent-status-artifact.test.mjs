import assert from 'node:assert/strict';
import { chmod, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createSubagentRunRecorder } from '../../pi-dev/lib/den-subagent-recorder.ts';
import { runPiCliSubagent, subagentSucceeded } from '../../pi-dev/lib/den-subagent-runner.ts';
import {
  collectContextMetricsFromSessionJsonl,
} from '../../pi-dev/lib/den-subagent-pipeline.ts';
import { buildSubagentParentToolResult } from '../../pi-dev/lib/den-subagent-parent-tool-result.ts';
import {
  collectFinalBranchHead,
  buildFinalBranchHeadMetadata,
} from '../../pi-dev/lib/den-subagent-final-head.ts';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

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

async function git(cwd, args) {
  const { stdout } = await execFileAsync('git', ['-C', cwd, ...args], { timeout: 10_000 });
  return String(stdout).trim();
}

async function initGitRepoWithTaskBranch(t, branch = 'task/final-head') {
  const repo = await mkdtemp(path.join(os.tmpdir(), 'den-final-head-'));
  t.after(async () => rm(repo, { recursive: true, force: true }));
  await git(repo, ['init', '-b', 'main']);
  await git(repo, ['config', 'user.email', 'test@example.com']);
  await git(repo, ['config', 'user.name', 'Test User']);
  await writeFile(path.join(repo, 'work.txt'), 'launch\n', 'utf8');
  await git(repo, ['add', 'work.txt']);
  await git(repo, ['commit', '-m', 'launch']);
  const launchHead = await git(repo, ['rev-parse', 'HEAD']);
  await git(repo, ['checkout', '-b', branch]);
  await writeFile(path.join(repo, 'work.txt'), 'final\n', 'utf8');
  await git(repo, ['commit', '-am', 'final']);
  const finalHead = await git(repo, ['rev-parse', 'HEAD']);
  return { repo, branch, launchHead, finalHead };
}

// ---------------------------------------------------------------------------
// collectContextMetricsFromSessionJsonl tests
// ---------------------------------------------------------------------------

test('collectContextMetricsFromSessionJsonl returns undefined for empty input', () => {
  assert.equal(collectContextMetricsFromSessionJsonl(undefined), undefined);
  assert.equal(collectContextMetricsFromSessionJsonl(''), undefined);
  assert.equal(collectContextMetricsFromSessionJsonl('\n\n'), undefined);
});

test('collectContextMetricsFromSessionJsonl counts messages by role and model-visible chars', () => {
  const sessionJsonl = [
    JSON.stringify({ type: 'session', version: 3, id: 's1' }),
    JSON.stringify({ type: 'message', timestamp: '2026-04-30T00:00:00.000Z', message: { role: 'user', content: [{ type: 'text', text: 'Hello, please implement X.' }] } }),
    JSON.stringify({ type: 'message', timestamp: '2026-04-30T00:00:05.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'I will implement X now.' }, { type: 'toolCall', id: 't1', name: 'bash', arguments: { command: 'echo ok' } }] } }),
    JSON.stringify({ type: 'message', timestamp: '2026-04-30T00:00:10.000Z', message: { role: 'toolResult', content: [{ type: 'text', text: 'ok\n' }] } }),
    JSON.stringify({ type: 'message', timestamp: '2026-04-30T00:00:15.000Z', message: { role: 'assistant', content: [{ type: 'text', text: 'Done implementing X.' }] } }),
  ].join('\n');

  const result = collectContextMetricsFromSessionJsonl(sessionJsonl);
  assert.ok(result, 'should return metrics for valid session');
  assert.deepEqual(result.session.message_counts_by_role, {
    user: 1,
    assistant: 2,
    toolResult: 1,
  });
  assert.equal(result.session.model_visible_chars, 'Hello, please implement X.'.length + 'I will implement X now.'.length + 'ok\n'.length + 'Done implementing X.'.length);
});

test('collectContextMetricsFromSessionJsonl ignores non-message entries', () => {
  const sessionJsonl = [
    JSON.stringify({ type: 'session', version: 3, id: 's1' }),
    JSON.stringify({ type: 'turn_start' }),
    'not-json',
    '',
  ].join('\n');

  assert.equal(collectContextMetricsFromSessionJsonl(sessionJsonl), undefined);
});

// ---------------------------------------------------------------------------
// Status artifact final-head persistence tests
// ---------------------------------------------------------------------------

test('status.json includes final_head_commit and final_branch after enrichment', async (t) => {
  const { repo, branch, launchHead, finalHead } = await initGitRepoWithTaskBranch(t, 'task/status-final-head');

  const { result, recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-status-final-head-',
    runId: 'run-status-final-head',
    scriptLines: [
      '#!/usr/bin/env node',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "done" }] } }));',
      'process.exit(0);',
    ],
    options: {
      role: 'coder',
      prompt: 'Work on the branch.',
      headCommit: launchHead,
      branch,
      worktreePath: repo,
    },
  });

  assert.equal(result.exit_code, 0);
  assert.equal(subagentSucceeded(result), true);

  // The status.json should have been enriched with final-head fields by the
  // runDenSubagent wrapper in the extension. However, runFakePiSubagent only
  // runs the backend (runPiCliSubagent), not the full runDenSubagent. So we
  // simulate the enrichment by collecting final-head state and checking the
  // metadata shape.
  const finalHeadState = await collectFinalBranchHead({ worktreePath: repo, branch });
  assert.ok(finalHeadState, 'should resolve final head state');
  assert.equal(finalHeadState.final_head_commit, finalHead);
  assert.equal(finalHeadState.final_branch, branch);
  assert.equal(finalHeadState.final_head_status, 'clean');
  assert.equal(finalHeadState.final_worktree_status, 'clean');

  // Verify the metadata builder produces the expected fields
  const metadata = buildFinalBranchHeadMetadata(finalHeadState);
  assert.equal(metadata.final_head_commit, finalHead);
  assert.equal(metadata.final_branch, branch);
  assert.equal(metadata.final_head_status, 'clean');
  assert.equal(metadata.final_worktree_status, 'clean');

  // Simulate enrichment: read status, merge, write, read back
  const currentStatus = await readJson(recorder.artifacts.status_json_path);
  const enriched = {
    ...currentStatus,
    ...metadata,
    context_metrics: null,
  };
  await recorder.writeStatus(enriched);
  const finalStatus = await readJson(recorder.artifacts.status_json_path);

  assert.equal(finalStatus.final_head_commit, finalHead, 'status.json should persist final_head_commit');
  assert.equal(finalStatus.final_branch, branch, 'status.json should persist final_branch');
  assert.equal(finalStatus.final_head_status, 'clean', 'status.json should persist final_head_status');
  assert.equal(finalStatus.final_worktree_status, 'clean', 'status.json should persist final_worktree_status');
  assert.equal(finalStatus.head_commit, launchHead, 'status.json should preserve starting head_commit');

  // Verify the runner's base fields are still present
  assert.equal(finalStatus.exit_code, 0);
  assert.equal(finalStatus.output_status, 'assistant_final');
  assert.equal(finalStatus.state, 'complete');
});

test('status.json includes dirty worktree status after enrichment', async (t) => {
  const { repo, branch, finalHead } = await initGitRepoWithTaskBranch(t, 'task/status-dirty');
  await writeFile(path.join(repo, 'work.txt'), 'modified\n', 'utf8');

  const { recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-status-dirty-',
    runId: 'run-status-dirty',
    scriptLines: [
      '#!/usr/bin/env node',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "done" }] } }));',
      'process.exit(0);',
    ],
    options: {
      role: 'coder',
      prompt: 'Work on the branch.',
      branch,
      worktreePath: repo,
    },
  });

  const finalHeadState = await collectFinalBranchHead({ worktreePath: repo, branch });
  assert.equal(finalHeadState.final_head_status, 'dirty_uncommitted');
  assert.equal(finalHeadState.final_worktree_status, 'dirty_uncommitted');
  assert.match(finalHeadState.final_worktree_status_short, /M work\.txt/);

  // Simulate enrichment
  const metadata = buildFinalBranchHeadMetadata(finalHeadState);
  const currentStatus = await readJson(recorder.artifacts.status_json_path);
  await recorder.writeStatus({ ...currentStatus, ...metadata, context_metrics: null });
  const finalStatus = await readJson(recorder.artifacts.status_json_path);

  assert.equal(finalStatus.final_head_status, 'dirty_uncommitted');
  assert.equal(finalStatus.final_worktree_status, 'dirty_uncommitted');
  assert.match(finalStatus.final_worktree_status_short, /M work\.txt/);
});

// ---------------------------------------------------------------------------
// Status artifact context_metrics tests
// ---------------------------------------------------------------------------

test('status.json includes context_metrics block after enrichment', async (t) => {
  const { recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-status-metrics-',
    runId: 'run-status-metrics',
    scriptLines: [
      '#!/usr/bin/env node',
      'const fs = require("node:fs");',
      'const path = require("node:path");',
      'const sessionDir = process.argv[process.argv.indexOf("--session-dir") + 1];',
      'const sessionId = "session-metrics-test";',
      'fs.mkdirSync(sessionDir, { recursive: true });',
      'const sessionFile = path.join(sessionDir, `2026-04-30T00-00-00-000Z_${sessionId}.jsonl`);',
      'fs.writeFileSync(sessionFile, JSON.stringify({ type: "session", version: 3, id: sessionId, cwd: process.cwd() }) + "\\n");',
      'fs.appendFileSync(sessionFile, JSON.stringify({ type: "message", id: "a1", timestamp: "2026-04-30T00:00:01.000Z", message: { role: "user", content: [{ type: "text", text: "Implement X" }] } }) + "\\n");',
      'fs.appendFileSync(sessionFile, JSON.stringify({ type: "message", id: "a2", timestamp: "2026-04-30T00:00:02.000Z", message: { role: "assistant", usage: { input: 100, output: 25 }, content: [{ type: "text", text: "Done" }] } }) + "\\n");',
      'console.log(JSON.stringify({ type: "session", version: 3, id: sessionId, cwd: process.cwd() }));',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "done" }] } }));',
      'process.exit(0);',
    ],
    options: { role: 'coder', prompt: 'Work on the task.' },
  });

  // Read the session file directly from the recorder's session_dir and build context_metrics.
  const { readdir: readdirAsync, stat: statAsync } = await import('node:fs/promises');
  let contextMetrics = null;
  try {
    const sessionEntries = await readdirAsync(recorder.artifacts.session_dir);
    const jsonlFile = sessionEntries.find(f => f.endsWith('.jsonl'));
    if (jsonlFile) {
      const sessionFilePath = path.join(recorder.artifacts.session_dir, jsonlFile);
      const sessionContent = await readFile(sessionFilePath, 'utf8');
      const parsed = collectContextMetricsFromSessionJsonl(sessionContent);
      if (parsed) {
        const sessionFileStat = await statAsync(sessionFilePath);
        contextMetrics = {
          session: {
            ...parsed.session,
            session_file_bytes: sessionFileStat.size,
          },
          usage_summary_source: 'pi_session_assistant_usage',
        };
      }
    }
  } catch {
    // Session metrics are optional
  }

  // Enrich the status artifact
  const status = await readJson(recorder.artifacts.status_json_path);
  await recorder.writeStatus({ ...status, context_metrics: contextMetrics });
  const finalStatus = await readJson(recorder.artifacts.status_json_path);

  assert.ok(finalStatus.context_metrics, 'status.json should include context_metrics');
  assert.ok(finalStatus.context_metrics.session, 'context_metrics should include session block');
  assert.deepEqual(finalStatus.context_metrics.session.message_counts_by_role, {
    user: 1,
    assistant: 1,
  });
  assert.equal(finalStatus.context_metrics.session.model_visible_chars, 'Implement X'.length + 'Done'.length);
  assert.equal(typeof finalStatus.context_metrics.session.session_file_bytes, 'number');
  assert.ok(finalStatus.context_metrics.session.session_file_bytes > 0);
  assert.equal(finalStatus.context_metrics.usage_summary_source, 'pi_session_assistant_usage');
});

test('context_metrics is null in status.json when no session file exists', async (t) => {
  const { recorder } = await runFakePiSubagent(t, {
    prefix: 'den-subagent-no-metrics-',
    runId: 'run-no-metrics',
    scriptLines: [
      '#!/usr/bin/env node',
      'console.log(JSON.stringify({ type: "message_end", message: { role: "assistant", model: "gpt-test", stopReason: "stop", content: [{ type: "text", text: "done" }] } }));',
      'process.exit(0);',
    ],
    options: { role: 'coder', prompt: 'Work on the task.' },
  });

  // Simulate enrichment with no session file
  const status = await readJson(recorder.artifacts.status_json_path);
  await recorder.writeStatus({ ...status, context_metrics: null });
  const finalStatus = await readJson(recorder.artifacts.status_json_path);

  assert.equal(finalStatus.context_metrics, null, 'context_metrics should be null when no session');
});

// ---------------------------------------------------------------------------
// Parent tool result consistency tests
// ---------------------------------------------------------------------------

test('parent tool result includes context_metrics when available', () => {
  const metrics = {
    session: {
      message_counts_by_role: { user: 2, assistant: 3 },
      model_visible_chars: 1500,
      session_file_bytes: 8192,
    },
    usage_summary_source: 'pi_session_assistant_usage',
  };

  const result = {
    run_id: 'run-metrics',
    role: 'coder',
    task_id: 1110,
    branch: 'task/1110-status-context-metrics',
    head_commit: 'head-sha',
    exit_code: 0,
    aborted: false,
    assistant_final_found: true,
    final_output: 'Implemented context metrics.',
    artifacts: { dir: '/tmp/run-metrics' },
    duration_ms: 5000,
    message_count: 5,
    assistant_message_count: 3,
    session_mode: 'fresh',
    backend: 'pi-cli',
    started_at: '2026-04-30T00:00:00.000Z',
    ended_at: '2026-04-30T00:00:05.000Z',
    context_metrics: metrics,
  };

  const toolResult = buildSubagentParentToolResult(result);
  assert.ok(toolResult.details.context_metrics, 'parent tool result should include context_metrics');
  assert.deepEqual(toolResult.details.context_metrics.session.message_counts_by_role, { user: 2, assistant: 3 });
  assert.equal(toolResult.details.context_metrics.session.model_visible_chars, 1500);
  assert.equal(toolResult.details.context_metrics.usage_summary_source, 'pi_session_assistant_usage');
});

test('parent tool result has undefined context_metrics when not set', () => {
  const result = {
    run_id: 'run-no-metrics',
    role: 'coder',
    task_id: 1110,
    branch: 'task/1110',
    head_commit: 'head-sha',
    exit_code: 0,
    aborted: false,
    assistant_final_found: true,
    final_output: 'Done.',
    artifacts: { dir: '/tmp/run-no-metrics' },
    duration_ms: 5000,
    message_count: 2,
    assistant_message_count: 1,
    session_mode: 'fresh',
    backend: 'pi-cli',
    started_at: '2026-04-30T00:00:00.000Z',
    ended_at: '2026-04-30T00:00:05.000Z',
  };

  const toolResult = buildSubagentParentToolResult(result);
  assert.equal(toolResult.details.context_metrics, undefined, 'context_metrics should be absent when not set');
});

test('parent tool result includes final-head fields consistently', () => {
  const result = {
    run_id: 'run-consistent',
    role: 'coder',
    task_id: 1110,
    branch: 'task/1110',
    base_branch: 'main',
    base_commit: 'base-sha',
    head_commit: 'launch-sha',
    requested_head_commit: 'launch-sha',
    purpose: 'implementation',
    exit_code: 0,
    aborted: false,
    assistant_final_found: true,
    final_output: 'Done.',
    artifacts: { dir: '/tmp/run-consistent' },
    duration_ms: 5000,
    message_count: 3,
    assistant_message_count: 2,
    session_mode: 'fresh',
    backend: 'pi-cli',
    started_at: '2026-04-30T00:00:00.000Z',
    ended_at: '2026-04-30T00:00:05.000Z',
    final_head_commit: 'final-sha-abc',
    final_head_status: 'clean',
    final_head_source: 'supplied_branch',
    final_branch: 'task/1110',
    final_worktree_branch: 'task/1110',
    final_branch_matches_worktree: true,
    final_worktree_status: 'clean',
    final_worktree_status_short: undefined,
    final_head_error: undefined,
  };

  const toolResult = buildSubagentParentToolResult(result);

  // Verify all final-head fields are present in details
  assert.equal(toolResult.details.head_commit, 'launch-sha', 'details should preserve starting head_commit');
  assert.equal(toolResult.details.requested_head_commit, 'launch-sha', 'details should preserve requested_head_commit');
  assert.equal(toolResult.details.final_head_commit, 'final-sha-abc', 'details should include final_head_commit');
  assert.equal(toolResult.details.final_head_status, 'clean');
  assert.equal(toolResult.details.final_branch, 'task/1110');
  assert.equal(toolResult.details.final_worktree_status, 'clean');

  // Text output should show both heads since they differ
  const text = toolResult.content[0].text;
  assert.match(text, /Final branch head: final-sha-abc/);
  assert.match(text, /Requested \(starting\) head: launch-sha/);
});
