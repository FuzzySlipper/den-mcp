import assert from 'node:assert/strict';
import test from 'node:test';
import {
  validateBuildContextResponse,
  validateCancelResponse,
  validateInvokeToolResponse,
  validateListToolsResponse,
} from '../src/desktop/sidecarBridgeValidation.ts';

// ── validateBuildContextResponse ──

test('validateBuildContextResponse accepts well-formed context response', () => {
  const raw = {
    context: {
      context_version: 1,
      selection: {},
      task_summary: null,
      git_snapshot: { snapshots: [], selected_snapshot: null },
      session_summaries: [],
      command_summaries: [],
      terminal_excerpts: [],
      collaboration_state: { summary: 'none' },
      authority: { allowed_tools: [], disabled_tools: [], cancel_available: false, stop_available: false, sandbox_scope: 'test' },
      audit: { agent_run_id: 'run_1', trace_id: 'tr_1' },
      warnings: [],
      built_at: '2026-05-01T00:00:00Z',
    },
  };
  const result = validateBuildContextResponse(raw);
  assert.equal(result.context.context_version, 1);
  assert.equal(result.context.built_at, '2026-05-01T00:00:00Z');
});

test('validateBuildContextResponse rejects null', () => {
  assert.throws(
    () => validateBuildContextResponse(null),
    /buildContextResponse expected an object, got null/,
  );
});

test('validateBuildContextResponse rejects missing context', () => {
  assert.throws(
    () => validateBuildContextResponse({}),
    /buildContextResponse\.context expected an object/,
  );
});

test('validateBuildContextResponse rejects non-numeric context_version', () => {
  assert.throws(
    () => validateBuildContextResponse({
      context: {
        context_version: 'bad',
        built_at: '2026-05-01T00:00:00Z',
        warnings: [],
      },
    }),
    /context_version expected number/,
  );
});

test('validateBuildContextResponse rejects missing built_at', () => {
  assert.throws(
    () => validateBuildContextResponse({
      context: {
        context_version: 1,
        warnings: [],
      },
    }),
    /built_at/,
  );
});

test('validateBuildContextResponse rejects non-array warnings', () => {
  assert.throws(
    () => validateBuildContextResponse({
      context: {
        context_version: 1,
        built_at: '2026-05-01T00:00:00Z',
        warnings: 'not-array',
      },
    }),
    /warnings expected array/,
  );
});

// ── validateListToolsResponse ──

test('validateListToolsResponse accepts well-formed tools response', () => {
  const raw = {
    tools: [
      { name: 'get_context', display_name: 'Get Context', category: 'read', enabled: true },
    ],
  };
  const result = validateListToolsResponse(raw);
  assert.equal(result.tools.length, 1);
  assert.equal(result.tools[0].name, 'get_context');
});

test('validateListToolsResponse rejects missing tools array', () => {
  assert.throws(
    () => validateListToolsResponse({}),
    /listToolsResponse\.tools expected array/,
  );
});

test('validateListToolsResponse rejects null', () => {
  assert.throws(
    () => validateListToolsResponse(null),
    /listToolsResponse expected an object, got null/,
  );
});

// ── validateInvokeToolResponse ──

test('validateInvokeToolResponse accepts well-formed invoke response', () => {
  const raw = {
    tool_name: 'summarize',
    tool_call_id: 'tc_1',
    status: 'completed',
    result: { summary: 'ok' },
    audit: { agent_run_id: 'run_1', trace_id: 'tr_1' },
  };
  const result = validateInvokeToolResponse(raw);
  assert.equal(result.tool_name, 'summarize');
  assert.equal(result.status, 'completed');
});

test('validateInvokeToolResponse rejects missing tool_name', () => {
  assert.throws(
    () => validateInvokeToolResponse({
      tool_call_id: 'tc_1',
      status: 'completed',
      audit: {},
    }),
    /tool_name/,
  );
});

test('validateInvokeToolResponse rejects missing audit', () => {
  assert.throws(
    () => validateInvokeToolResponse({
      tool_name: 'summarize',
      tool_call_id: 'tc_1',
      status: 'completed',
      result: null,
    }),
    /audit is required/,
  );
});

test('validateInvokeToolResponse rejects array input', () => {
  assert.throws(
    () => validateInvokeToolResponse([1, 2, 3]),
    /invokeToolResponse expected an object, got an array/,
  );
});

// ── validateCancelResponse ──

test('validateCancelResponse accepts well-formed cancel response', () => {
  const raw = {
    request_id: 'req_1',
    accepted: true,
    status: 'cancel_requested',
  };
  const result = validateCancelResponse(raw);
  assert.equal(result.request_id, 'req_1');
  assert.equal(result.accepted, true);
});

test('validateCancelResponse rejects non-boolean accepted', () => {
  assert.throws(
    () => validateCancelResponse({
      request_id: 'req_1',
      accepted: 'yes',
      status: 'cancelled',
    }),
    /accepted expected boolean/,
  );
});

test('validateCancelResponse rejects undefined input', () => {
  assert.throws(
    () => validateCancelResponse(undefined),
    /cancelResponse expected an object, got undefined/,
  );
});
