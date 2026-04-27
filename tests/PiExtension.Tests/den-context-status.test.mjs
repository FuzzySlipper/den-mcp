import assert from 'node:assert/strict';
import test from 'node:test';
import denExtension from '../../pi-dev/extensions/den.ts';
import {
  buildDenContextStatus,
  buildDenContextStatusToolResult,
  captureDenContextStatus,
  summarizeDenContextStatusForMetadata,
} from '../../pi-dev/lib/den-context-status.ts';

const generatedAt = '2026-04-27T00:00:00.000Z';

function baseInput(overrides = {}) {
  return {
    generatedAt,
    cwd: '/repo',
    sessionId: 'session-1',
    sessionFile: '/tmp/session.jsonl',
    model: {
      provider: 'openai',
      id: 'gpt-test',
      contextWindow: 100_000,
      maxTokens: 16_384,
    },
    compaction: {
      enabled: true,
      reserveTokens: 16_000,
      keepRecentTokens: 20_000,
    },
    sessionEntries: [],
    ...overrides,
  };
}

test('context status classifies ok, watch, and compact-after-task thresholds', () => {
  const ok = buildDenContextStatus(baseInput({
    contextUsage: { tokens: 20_000, contextWindow: 100_000, percent: 20 },
  }));
  assert.equal(ok.context.source, 'pi_context_usage_estimate');
  assert.equal(ok.context.confidence, 'medium');
  assert.equal(ok.recommendation.status, 'ok');
  assert.equal(ok.compaction.auto_compact_threshold_percent, 84);
  assert.equal(ok.compaction.watch_threshold_percent, 63);
  assert.equal(ok.compaction.compact_after_task_threshold_percent, 75.6);

  const watch = buildDenContextStatus(baseInput({
    contextUsage: { tokens: 70_000, contextWindow: 100_000, percent: 70 },
  }));
  assert.equal(watch.recommendation.status, 'watch');
  assert.match(watch.recommendation.reason, /70\.0%/);

  const compact = buildDenContextStatus(baseInput({
    contextUsage: { tokens: 76_000, contextWindow: 100_000, percent: 76 },
  }));
  assert.equal(compact.recommendation.status, 'compact_after_current_task');
  assert.match(compact.recommendation.action, /compact before starting another substantial task/);
});

test('context status handles missing and unknown usage conservatively', () => {
  const missing = buildDenContextStatus(baseInput({
    contextUsage: null,
    sessionEntries: [],
  }));
  assert.equal(missing.context.source, 'unavailable');
  assert.equal(missing.context.confidence, 'unknown');
  assert.equal(missing.context.accuracy, 'unknown');
  assert.equal(missing.context.used_tokens_estimate, null);
  assert.equal(missing.recommendation.status, 'watch');
  assert.match(missing.recommendation.reason, /unknown/);

  const postCompaction = buildDenContextStatus(baseInput({
    contextUsage: { tokens: null, contextWindow: 100_000, percent: null },
  }));
  assert.equal(postCompaction.context.source, 'pi_context_usage_estimate');
  assert.equal(postCompaction.context.confidence, 'unknown');
  assert.equal(postCompaction.recommendation.status, 'watch');
  assert.match(postCompaction.context.notes.join('\n'), /immediately after compaction/);
});

test('context status falls back to last assistant provider usage when Pi usage is unavailable', () => {
  const status = buildDenContextStatus(baseInput({
    contextUsage: null,
    sessionEntries: [
      { type: 'message', timestamp: '2026-04-26T00:00:00.000Z', message: { role: 'assistant', stopReason: 'stop', usage: { input: 1_000, output: 2_000, cacheRead: 3_000, cacheWrite: 4_000, totalTokens: 0 } } },
      { type: 'message', timestamp: '2026-04-26T00:01:00.000Z', message: { role: 'assistant', stopReason: 'stop', usage: { input: 40_000, output: 5_000, cacheRead: 5_000, cacheWrite: 0, totalTokens: 50_000 } } },
    ],
  }));

  assert.equal(status.context.source, 'provider_reported_last_assistant_usage');
  assert.equal(status.context.confidence, 'low');
  assert.equal(status.context.used_tokens_estimate, 50_000);
  assert.equal(status.context.used_percent_estimate, 50);
  assert.equal(status.context.last_usage_timestamp, '2026-04-26T00:01:00.000Z');
  assert.match(status.context.notes.join('\n'), /can be stale/);
});

test('captureDenContextStatus uses ctx.getContextUsage ahead of stale session usage', () => {
  const ctx = {
    cwd: '/repo',
    model: { provider: 'openai', id: 'gpt-test', contextWindow: 100_000, maxTokens: 16_384 },
    getContextUsage() {
      return { tokens: 25_000, contextWindow: 100_000, percent: 25 };
    },
    sessionManager: {
      getSessionId: () => 'session-ctx',
      getSessionFile: () => '/tmp/session-ctx.jsonl',
      getBranch: () => [
        { type: 'message', timestamp: '2026-04-26T00:02:00.000Z', message: { role: 'assistant', stopReason: 'stop', usage: { input: 90_000, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 90_000 } } },
      ],
    },
    settingsManager: {
      getCompactionSettings: () => ({ enabled: true, reserveTokens: 16_000, keepRecentTokens: 20_000 }),
    },
  };

  const status = captureDenContextStatus(ctx);
  assert.equal(status.context.source, 'pi_context_usage_estimate');
  assert.equal(status.context.used_tokens_estimate, 25_000);
  assert.equal(status.context.used_percent_estimate, 25);
  assert.equal(status.session.session_id, 'session-ctx');
  assert.equal(status.session.branch_entry_count, 1);
});

test('den extension registers context status command and model-callable tool', () => {
  const commands = [];
  const tools = [];
  denExtension({
    on() {},
    registerCommand(name, definition) {
      commands.push({ name, definition });
    },
    registerTool(definition) {
      tools.push(definition);
    },
  });

  assert.ok(commands.some((entry) => entry.name === 'den-context-status'));
  assert.ok(commands.some((entry) => entry.name === 'den-compaction-status'));
  const tool = tools.find((entry) => entry.name === 'den_context_status');
  assert.ok(tool, 'den_context_status should be registered');
  assert.deepEqual(tool.parameters, { type: 'object', properties: {}, additionalProperties: false });
});

test('context status tool result and binding metadata stay compact', () => {
  const status = buildDenContextStatus(baseInput({
    contextUsage: { tokens: 76_000, contextWindow: 100_000, percent: 76 },
  }));
  const toolResult = buildDenContextStatusToolResult(status);
  const metadata = summarizeDenContextStatusForMetadata(status);

  assert.equal(toolResult.isError, false);
  assert.match(toolResult.content[0].text, /Context recommendation: compact_after_current_task/);
  assert.equal(toolResult.details.schema, 'den_context_status');
  assert.equal(metadata.recommendation, 'compact_after_current_task');
  assert.equal(metadata.used_percent_estimate, 76);
  assert.ok(JSON.stringify(toolResult).length < 6_000, 'context status tool result should remain bounded');
  assert.ok(JSON.stringify(metadata).length < 1_000, 'binding metadata summary should remain compact');
});
