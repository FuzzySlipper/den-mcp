import assert from 'node:assert/strict';
import test from 'node:test';
import { initialIpcHealth, ipcHealthState, ipcHealthSummary } from '../src/desktop/ipcHealth.ts';

test('ipc health starts in calm unknown state', () => {
  const health = initialIpcHealth();

  assert.equal(health.state, 'unknown');
  assert.match(ipcHealthSummary(health), /Waiting for desktop IPC heartbeat/);
});

test('ipc health reports listener failures as degraded', () => {
  const health = {
    ...initialIpcHealth(),
    listenerFailures: ['operator status listener: listen failed'],
  };

  assert.equal(ipcHealthState(health), 'degraded');
  assert.match(ipcHealthSummary(health), /event listener degraded/);
});

test('ipc health reports stale successful IPC as degraded', () => {
  const health = {
    ...initialIpcHealth(),
    state: 'ok',
    lastSuccessAt: '2026-04-27T10:00:00.000Z',
  };

  const nowMs = Date.parse('2026-04-27T10:01:00.000Z');
  assert.equal(ipcHealthState(health, nowMs), 'degraded');
  assert.match(ipcHealthSummary(health, nowMs), /No successful desktop IPC response for 1m/);
});
