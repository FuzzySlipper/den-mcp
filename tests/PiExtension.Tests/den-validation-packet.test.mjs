import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { mkdtemp, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { promisify } from 'node:util';
import {
  buildValidationPacketMeta,
  deriveValidationStatus,
  executeValidationCommand,
  formatValidationPacketMessage,
  parseValidationArgs,
  runValidation,
} from '../../pi-dev/lib/den-validation-packet.ts';

const execFileAsync = promisify(execFile);

// ---------------------------------------------------------------------------
// deriveValidationStatus
// ---------------------------------------------------------------------------

test('deriveValidationStatus returns pass when all commands pass', () => {
  const status = deriveValidationStatus([
    { command: 'echo ok', status: 'pass', exit_code: 0, duration_ms: 100, stdout_preview: '', stderr_preview: '' },
    { command: 'echo ok2', status: 'pass', exit_code: 0, duration_ms: 50, stdout_preview: '', stderr_preview: '' },
  ]);
  assert.equal(status, 'pass');
});

test('deriveValidationStatus returns fail when any command fails', () => {
  const status = deriveValidationStatus([
    { command: 'echo ok', status: 'pass', exit_code: 0, duration_ms: 100, stdout_preview: '', stderr_preview: '' },
    { command: 'exit 1', status: 'fail', exit_code: 1, duration_ms: 50, stdout_preview: '', stderr_preview: '' },
  ]);
  assert.equal(status, 'fail');
});

test('deriveValidationStatus returns blocked when all blocked', () => {
  const status = deriveValidationStatus([
    { command: 'missing-cmd', status: 'blocked', exit_code: null, duration_ms: 10, stdout_preview: '', stderr_preview: '', error: 'not found' },
  ]);
  assert.equal(status, 'blocked');
});

test('deriveValidationStatus returns partial when pass and blocked mixed', () => {
  const status = deriveValidationStatus([
    { command: 'echo ok', status: 'pass', exit_code: 0, duration_ms: 100, stdout_preview: '', stderr_preview: '' },
    { command: 'timeout-cmd', status: 'blocked', exit_code: null, duration_ms: 5000, stdout_preview: '', stderr_preview: '', error: 'timed out' },
  ]);
  assert.equal(status, 'partial');
});

test('deriveValidationStatus returns blocked for empty results', () => {
  assert.equal(deriveValidationStatus([]), 'blocked');
});

// ---------------------------------------------------------------------------
// buildValidationPacketMeta
// ---------------------------------------------------------------------------

test('buildValidationPacketMeta produces stable metadata with correct counts', () => {
  const result = {
    task_id: 957,
    branch: 'task/957-validation-packet-producer',
    base_commit: 'abc1234',
    head_commit: 'def5678',
    status: 'pass',
    command_results: [
      { command: 'node --test a.test.mjs', status: 'pass', exit_code: 0, duration_ms: 200, stdout_preview: '', stderr_preview: '' },
      { command: 'git diff --check', status: 'pass', exit_code: 0, duration_ms: 50, stdout_preview: '', stderr_preview: '' },
    ],
    total_duration_ms: 250,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const meta = buildValidationPacketMeta(result);

  assert.equal(meta.type, 'validation_packet');
  assert.equal(meta.prepared_by, 'conductor');
  assert.equal(meta.workflow, 'expanded_isolation_with_context');
  assert.equal(meta.version, 1);
  assert.equal(meta.task_id, 957);
  assert.equal(meta.branch, 'task/957-validation-packet-producer');
  assert.equal(meta.head_commit, 'def5678');
  assert.equal(meta.status, 'pass');
  assert.equal(meta.command_count, 2);
  assert.equal(meta.pass_count, 2);
  assert.equal(meta.fail_count, 0);
  assert.equal(meta.blocked_count, 0);
});

test('buildValidationPacketMeta handles mixed pass/fail/blocked results', () => {
  const result = {
    status: 'fail',
    command_results: [
      { command: 'pass-cmd', status: 'pass', exit_code: 0, duration_ms: 10, stdout_preview: '', stderr_preview: '' },
      { command: 'fail-cmd', status: 'fail', exit_code: 1, duration_ms: 20, stdout_preview: '', stderr_preview: '' },
      { command: 'blocked-cmd', status: 'blocked', exit_code: null, duration_ms: 5, stdout_preview: '', stderr_preview: '' },
    ],
    total_duration_ms: 35,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const meta = buildValidationPacketMeta(result);
  assert.equal(meta.status, 'fail');
  assert.equal(meta.pass_count, 1);
  assert.equal(meta.fail_count, 1);
  assert.equal(meta.blocked_count, 1);
});

test('buildValidationPacketMeta uses null for missing task/branch/commit', () => {
  const result = {
    status: 'pass',
    command_results: [],
    total_duration_ms: 0,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const meta = buildValidationPacketMeta(result);
  assert.equal(meta.task_id, null);
  assert.equal(meta.branch, null);
  assert.equal(meta.head_commit, null);
  assert.equal(meta.base_commit, null);
});

// ---------------------------------------------------------------------------
// formatValidationPacketMessage
// ---------------------------------------------------------------------------

test('formatValidationPacketMessage includes header, status, context, and summary', () => {
  const result = {
    task_id: 957,
    branch: 'task/957-test',
    head_commit: 'abc1234',
    status: 'pass',
    command_results: [
      { command: 'node --test foo.test.mjs', status: 'pass', exit_code: 0, duration_ms: 200, stdout_preview: 'ok', stderr_preview: '' },
    ],
    total_duration_ms: 200,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const message = formatValidationPacketMessage(result);

  assert.ok(message.includes('# Validation Packet'));
  assert.ok(message.includes('**Status:** pass'));
  assert.ok(message.includes('## Context'));
  assert.ok(message.includes('`#957`'));
  assert.ok(message.includes('`task/957-test`'));
  assert.ok(message.includes('`abc1234`'));
  assert.ok(message.includes('## Summary'));
  assert.ok(message.includes('## Command Results'));
  assert.ok(message.includes('node --test foo.test.mjs'));
  assert.ok(message.includes('✅'));
  assert.ok(message.includes('Validation passed'));
});

test('formatValidationPacketMessage shows failure verdict for failed status', () => {
  const result = {
    status: 'fail',
    command_results: [
      { command: 'exit 1', status: 'fail', exit_code: 1, duration_ms: 50, stdout_preview: '', stderr_preview: 'FAIL' },
    ],
    total_duration_ms: 50,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const message = formatValidationPacketMessage(result);
  assert.ok(message.includes('❌'));
  assert.ok(message.includes('Validation failed'));
  assert.ok(message.includes('Review the command results above'));
  assert.ok(!message.includes('do not conflate')); // only in blocked verdict
});

test('formatValidationPacketMessage shows blocked verdict with infrastructure warning', () => {
  const result = {
    status: 'blocked',
    command_results: [
      { command: 'nonexistent-cmd', status: 'blocked', exit_code: null, duration_ms: 5, stdout_preview: '', stderr_preview: '', error: 'command not found' },
    ],
    total_duration_ms: 5,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: ['command not found'],
  };

  const message = formatValidationPacketMessage(result);
  assert.ok(message.includes('⚠️'));
  assert.ok(message.includes('Validation blocked'));
  assert.ok(message.includes('infrastructure issue'));
  assert.ok(message.includes('Infrastructure Errors'));
});

test('formatValidationPacketMessage shows partial verdict for mixed pass/blocked', () => {
  const result = {
    status: 'partial',
    command_results: [
      { command: 'echo ok', status: 'pass', exit_code: 0, duration_ms: 10, stdout_preview: '', stderr_preview: '' },
      { command: 'timeout-cmd', status: 'blocked', exit_code: null, duration_ms: 5000, stdout_preview: '', stderr_preview: '', error: 'timed out' },
    ],
    total_duration_ms: 5010,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const message = formatValidationPacketMessage(result);
  assert.ok(message.includes('Validation partial'));
  assert.ok(message.includes('Pass: 1'));
  assert.ok(message.includes('Blocked: 1'));
});

test('formatValidationPacketMessage omits Context section when no task/branch/commit', () => {
  const result = {
    status: 'pass',
    command_results: [],
    total_duration_ms: 0,
    timestamp: '2026-04-29T12:00:00.000Z',
    infrastructure_errors: [],
  };

  const message = formatValidationPacketMessage(result);
  assert.ok(!message.includes('## Context'));
});

// ---------------------------------------------------------------------------
// executeValidationCommand (integration, using real shell)
// ---------------------------------------------------------------------------

test('executeValidationCommand captures pass for a successful command', async () => {
  const result = await executeValidationCommand('echo hello', { cwd: os.tmpdir() });
  assert.equal(result.status, 'pass');
  assert.equal(result.exit_code, 0);
  assert.ok(result.stdout_preview.includes('hello'));
  assert.equal(result.error, undefined);
});

test('executeValidationCommand captures fail for non-zero exit code', async () => {
  const result = await executeValidationCommand('exit 42', { cwd: os.tmpdir() });
  assert.equal(result.status, 'fail');
  assert.equal(result.exit_code, 42);
});

test('executeValidationCommand captures fail for missing command (sh returns 127)', async () => {
  const result = await executeValidationCommand('this_command_does_not_exist_xyz', { cwd: os.tmpdir() });
  // sh -c "missing_cmd" exits with code 127, not a spawn error
  assert.equal(result.status, 'fail');
  assert.equal(result.exit_code, 127);
});

test('executeValidationCommand captures blocked for timeout', async () => {
  const result = await executeValidationCommand('sleep 10', { cwd: os.tmpdir(), timeout_ms: 200 });
  assert.equal(result.status, 'blocked');
  assert.ok(result.duration_ms < 5000);
});

// ---------------------------------------------------------------------------
// runValidation (integration)
// ---------------------------------------------------------------------------

test('runValidation runs commands sequentially and aggregates results', async () => {
  const tmp = await mkdtemp(path.join(os.tmpdir(), 'den-val-test-'));
  try {
    const result = await runValidation({
      cwd: tmp,
      commands: ['echo pass1', 'echo pass2'],
    });

    assert.equal(result.status, 'pass');
    assert.equal(result.command_results.length, 2);
    assert.equal(result.command_results[0].status, 'pass');
    assert.equal(result.command_results[1].status, 'pass');
    assert.ok(result.total_duration_ms > 0);
    assert.ok(result.timestamp);
    assert.deepEqual(result.infrastructure_errors, []);
  } finally {
    await import('node:fs/promises').then((fs) => fs.rm(tmp, { recursive: true }));
  }
});

test('runValidation reports fail when one command fails', async () => {
  const tmp = await mkdtemp(path.join(os.tmpdir(), 'den-val-test-'));
  try {
    const result = await runValidation({
      cwd: tmp,
      commands: ['echo pass', 'exit 1', 'echo also-pass'],
    });

    assert.equal(result.status, 'fail');
    assert.equal(result.command_results[0].status, 'pass');
    assert.equal(result.command_results[1].status, 'fail');
    assert.equal(result.command_results[2].status, 'pass');
  } finally {
    await import('node:fs/promises').then((fs) => fs.rm(tmp, { recursive: true }));
  }
});

test('runValidation carries task context through to result', async () => {
  const tmp = await mkdtemp(path.join(os.tmpdir(), 'den-val-test-'));
  try {
    const result = await runValidation({
      cwd: tmp,
      task_id: 957,
      branch: 'task/957-test',
      base_commit: 'abc0000',
      head_commit: 'def1111',
      commands: ['echo ok'],
    });

    assert.equal(result.task_id, 957);
    assert.equal(result.branch, 'task/957-test');
    assert.equal(result.base_commit, 'abc0000');
    assert.equal(result.head_commit, 'def1111');
  } finally {
    await import('node:fs/promises').then((fs) => fs.rm(tmp, { recursive: true }));
  }
});

test('runValidation handles no commands gracefully', async () => {
  const tmp = await mkdtemp(path.join(os.tmpdir(), 'den-val-test-'));
  try {
    const result = await runValidation({ cwd: tmp, commands: [] });
    assert.equal(result.status, 'blocked');
    assert.equal(result.command_results.length, 0);
  } finally {
    await import('node:fs/promises').then((fs) => fs.rm(tmp, { recursive: true }));
  }
});

// ---------------------------------------------------------------------------
// parseValidationArgs
// ---------------------------------------------------------------------------

test('parseValidationArgs parses task_id only', () => {
  const parsed = parseValidationArgs('957');
  assert.equal(parsed.task_id, 957);
  assert.equal(parsed.commands, undefined);
});

test('parseValidationArgs parses --commands as JSON array', () => {
  const parsed = parseValidationArgs('957 --commands \'["echo ok", "npm test"]\'');
  assert.equal(parsed.task_id, 957);
  assert.deepEqual(parsed.commands, ['echo ok', 'npm test']);
});

test('parseValidationArgs parses --no-post flag', () => {
  const parsed = parseValidationArgs('957 --no-post');
  assert.equal(parsed.task_id, 957);
  assert.equal(parsed.post_result, false);
});

test('parseValidationArgs parses branch and commit flags', () => {
  const parsed = parseValidationArgs('957 --branch task/957-test --head-commit abc1234 --base-commit def0000');
  assert.equal(parsed.branch, 'task/957-test');
  assert.equal(parsed.head_commit, 'abc1234');
  assert.equal(parsed.base_commit, 'def0000');
});

test('parseValidationArgs parses --timeout', () => {
  const parsed = parseValidationArgs('957 --timeout 30000');
  assert.equal(parsed.timeout_ms, 30000);
});

test('parseValidationArgs rejects missing task_id', () => {
  assert.throws(() => parseValidationArgs(''), /Usage:/);
  assert.throws(() => parseValidationArgs('abc'), /Usage:/);
});

test('parseValidationArgs rejects unknown flags', () => {
  assert.throws(() => parseValidationArgs('957 --unknown-flag value'), /Unknown validation flag/);
});
