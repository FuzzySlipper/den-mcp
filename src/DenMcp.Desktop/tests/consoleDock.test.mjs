import assert from 'node:assert/strict';
import test from 'node:test';
import { buildConsoleLines, TIMESTAMP_LOCALE, TIMESTAMP_OPTIONS } from '../src/consoleLines.ts';
import { initialIpcHealth } from '../src/desktop/ipcHealth.ts';

function makeIpcHealth(overrides = {}) {
  return { ...initialIpcHealth(), ...overrides };
}

function makeDiagnostic(overrides = {}) {
  return {
    level: 'info',
    source: 'operator',
    message: 'started runtime',
    observedAt: '2026-04-29T12:00:00.000Z',
    ...overrides,
  };
}

function makeDenConnection(overrides = {}) {
  return {
    state: 'connected',
    message: null,
    lastSuccessAt: '2026-04-29T12:00:00.000Z',
    lastFailureAt: null,
    nextRetryAt: null,
    ...overrides,
  };
}

function makeObserver(overrides = {}) {
  return {
    kind: 'git',
    state: 'ready',
    scopesScanned: 4,
    warningCount: 0,
    lastRunAt: '2026-04-29T12:00:00.000Z',
    nextRunAt: null,
    ...overrides,
  };
}

// Timestamp constants are imported from consoleLines.ts to avoid drift.

function expectedTimestamp(isoString) {
  return new Date(isoString).toLocaleTimeString(TIMESTAMP_LOCALE, TIMESTAMP_OPTIONS);
}

const nowMs = Date.parse('2026-04-29T12:05:00.000Z');

test('buildConsoleLines returns empty lines when no sources', () => {
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.equal(lines.length, 0);
});

test('buildConsoleLines includes diagnostic entries', () => {
  const diag = makeDiagnostic({ level: 'info', message: 'test entry', source: 'test' });
  const lines = buildConsoleLines({
    diagnostics: [diag],
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.length >= 1);
  assert.equal(lines[0].ts, expectedTimestamp(diag.observedAt));
  assert.equal(lines[0].level, 'info');
  assert.match(lines[0].message, /test entry/);
});

test('buildConsoleLines includes observer warnings', () => {
  const observer = makeObserver({ kind: 'git', warningCount: 3, scopesScanned: 8 });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [observer],
    lastSyncAt: null,
  }, 40, nowMs);

  const line = lines.find((l) => l.level === 'warn' && l.message.includes('git'));
  assert.ok(line);
  assert.equal(line.ts, expectedTimestamp(observer.lastRunAt));
  assert.ok(lines.some((l) => l.message.includes('3 warnings')));
});

test('buildConsoleLines skips observers without warnings', () => {
  const observer = makeObserver({ kind: 'git', warningCount: 0 });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [observer],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.equal(lines.filter((l) => l.message.includes('git')).length, 0);
});

test('buildConsoleLines includes Den connection degraded state', () => {
  const den = makeDenConnection({ state: 'degraded', message: 'heartbeat missing' });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: den,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.level === 'warn' && l.message.includes('Den connection degraded')));
});

test('buildConsoleLines includes Den connection error state', () => {
  const den = makeDenConnection({ state: 'offline', message: 'no route to host' });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: den,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.level === 'err' && l.message.includes('Den connection offline')));
});

test('buildConsoleLines includes sync summary', () => {
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: null,
    denConnection: makeDenConnection(),
    observerStatuses: [],
    lastSyncAt: '2026-04-29T12:00:00.000Z',
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.message.includes('Den sync last run')));
});

test('buildConsoleLines includes IPC health degraded summary', () => {
  const health = makeIpcHealth({ consecutiveFailures: 3, lastFailureAt: '2026-04-29T12:00:00.000Z' });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: health,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.level === 'warn' && l.message.includes('IPC')));
});

test('buildConsoleLines includes recent bridge event timestamp', () => {
  const health = makeIpcHealth({ lastEventAt: '2026-04-29T12:04:00.000Z' });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: health,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.message.includes('Bridge event') && l.message.includes('ago')));
});

test('buildConsoleLines warns about stale heartbeat', () => {
  const health = makeIpcHealth({ lastHeartbeatAt: '2026-04-29T11:00:00.000Z', lastSuccessAt: '2026-04-29T11:00:00.000Z' });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: health,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.level === 'warn' && l.message.includes('IPC heartbeat stale')));
});

test('buildConsoleLines includes pending IPC invokes', () => {
  const health = makeIpcHealth({ pendingInvokes: 2 });
  const lines = buildConsoleLines({
    diagnostics: [],
    ipcHealth: health,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 40, nowMs);

  assert.ok(lines.some((l) => l.message.includes('2 pending IPC invokes')));
});

test('buildConsoleLines deduplicates by message', () => {
  // Create a diagnostic entry whose message matches what the observer warning
  // would produce, so the dedup logic catches it.
  const observer = makeObserver({ kind: 'git', warningCount: 3, scopesScanned: 8 });
  // The observer produces message "git: 3 warnings (8 scopes)".
  // A diagnostic with the same full message but different source will be deduped
  // because seenMessages checks the full message string.
  const diag = makeDiagnostic({
    level: 'warn',
    message: '3 warnings (8 scopes)',
    source: 'git',
  });
  const lines = buildConsoleLines({
    diagnostics: [diag],
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [observer],
    lastSyncAt: null,
  }, 40, nowMs);

  // Observer "git: 3 warnings (8 scopes)" should appear
  // The diagnostic from source 'git' with message '3 warnings (8 scopes)' produces
  // "git: 3 warnings (8 scopes)" which is the same as the observer line — deduped.
  const matches = lines.filter((l) => l.message.includes('3 warnings') && l.message.includes('git'));
  assert.equal(matches.length, 1);
});

test('buildConsoleLines respects maxLines limit', () => {
  const diagnostics = [];
  for (let i = 0; i < 30; i++) {
    diagnostics.push(makeDiagnostic({
      level: 'info',
      message: `diagnostic #${i}`,
      source: 'test',
      observedAt: `2026-04-29T12:00:${String(i).padStart(2, '0')}.000Z`,
    }));
  }

  const lines = buildConsoleLines({
    diagnostics,
    ipcHealth: null,
    denConnection: null,
    observerStatuses: [],
    lastSyncAt: null,
  }, 10, nowMs);

  assert.ok(lines.length <= 10);
});

test('buildConsoleLines includes multiple sources', () => {
  const health = makeIpcHealth({ lastEventAt: '2026-04-29T12:04:00.000Z' });
  const den = makeDenConnection();
  const observer = makeObserver({ warningCount: 1 });
  const diag = makeDiagnostic({ level: 'info', message: 'observer started', source: 'observe-fs' });

  const lines = buildConsoleLines({
    diagnostics: [diag],
    ipcHealth: health,
    denConnection: den,
    observerStatuses: [observer],
    lastSyncAt: '2026-04-29T12:00:00.000Z',
  }, 40, nowMs);

  const messages = new Set(lines.map((l) => l.message));
  assert.ok(messages.size >= 3, `expected at least 3 unique messages, got ${messages.size}: ${[...messages].join(' | ')}`);
});
