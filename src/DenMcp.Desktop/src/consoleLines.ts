import { ipcHealthState, ipcHealthSummary, formatDuration, STALE_IPC_AFTER_MS } from './desktop/ipcHealth.ts';
// Re-export the canonical bridge type so consumers can import from either path.
// consoleLines is the UI-side model module; sidecarBridgeApi is the protocol-level
// definition. The canonical shape lives in sidecarBridgeApi (mirroring sidecarProtocol)
// and is re-exported here for convenience.
export type { ConsoleCommandLine } from './desktop/sidecarBridgeApi.ts';

/** @deprecated Use ConsoleCommandLine (canonical bridge name) instead. */
export type ConsoleCommandOutputLine = import('./desktop/sidecarBridgeApi.ts').ConsoleCommandLine;

export interface ConsoleLine {
  ts: string;
  level: string;
  message: string;
}

/**
 * An entry in the console command history: what was run and what the structured output was.
 * status may be 'running' for in-flight progress entries rendered by ConsoleDock.
 */
export interface ConsoleCommandHistoryEntry {
  command: string;
  executedAt: string;
  lines: import('./desktop/sidecarBridgeApi.ts').ConsoleCommandLine[];
  status: 'success' | 'error' | 'running';
  errorMessage?: string | null;
}

// Local type shapes — ipcHealth.IpcHealth and sidecarBridgeApi types are all interface/type-only exports
// that get erased at runtime. Inlining here avoids runtime import resolution failures from Node ESM.

interface IpcHealthState {
  state: 'unknown' | 'ok' | 'degraded';
  message: string | null;
  consecutiveFailures: number;
  pendingInvokes: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastHeartbeatAt: string | null;
  lastEventAt: string | null;
  listenerFailures: string[];
}

interface DenConnectionStatus {
  state: string;
  message: string | null;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  nextRetryAt: string | null;
}

interface DiagnosticEntry {
  level: string;
  source: string;
  message: string;
  observedAt: string;
}

interface ObserverStatus {
  kind: string;
  state: string;
  scopesScanned: number;
  warningCount: number;
  lastRunAt: string | null;
  nextRunAt: string | null;
}

interface ConsoleSources {
  diagnostics: DiagnosticEntry[];
  ipcHealth: IpcHealthState | null;
  denConnection: DenConnectionStatus | null;
  observerStatuses: ObserverStatus[];
  lastSyncAt: string | null;
}

/**
 * Build a bounded array of ConsoleLine entries for the console dock.
 * Deduplicates diagnostics that are already represented as summary lines.
 */
export function buildConsoleLines(sources: ConsoleSources, maxLines = 40, nowMs = Date.now()): ConsoleLine[] {
  const lines: ConsoleLine[] = [];
  const now = new Date(nowMs).toISOString();
  const ts = () => formatTimestamp(now);
  const seenMessages = new Set<string>();

  // 1. Observer warnings — one line per observer with warnings
  for (const observer of sources.observerStatuses) {
    if (observer.warningCount > 0) {
      const msg = `${observer.kind}: ${observer.warningCount} warning${observer.warningCount === 1 ? '' : 's'} (${observer.scopesScanned} scopes)`;
      addIfUnique(lines, seenMessages, { ts: formatTimestamp(observer.lastRunAt ?? now), level: 'warn', message: msg });
    }
  }

  // 2. Den connection status summary
  if (sources.denConnection) {
    const conn = sources.denConnection;
    if (conn.state !== 'connected' && conn.state !== 'unknown') {
      const extras = [
        conn.lastSuccessAt ? `last ok ${formatAge(conn.lastSuccessAt, nowMs)}` : null,
        conn.lastFailureAt ? `last fail ${formatAge(conn.lastFailureAt, nowMs)}` : null,
        conn.nextRetryAt ? `retry ${formatAge(conn.nextRetryAt, nowMs)}` : null,
      ].filter(Boolean).join(' · ');
      const msg = `Den connection ${conn.state}${conn.message ? ` — ${conn.message}` : ''}${extras ? ` (${extras})` : ''}`;
      addIfUnique(lines, seenMessages, { ts: ts(), level: levelFromConnectionState(conn.state), message: msg });
    }

    if (sources.lastSyncAt) {
      const syncAge = formatAge(sources.lastSyncAt, nowMs);
      addIfUnique(lines, seenMessages, { ts: ts(), level: 'info', message: `Den sync last run ${syncAge} ago` });
    }
  }

  // 3. IPC health summary (when degraded or stale)
  if (sources.ipcHealth) {
    const health = sources.ipcHealth;
    const healthState = ipcHealthState(health, nowMs);
    if (healthState !== 'ok' || health.consecutiveFailures > 0 || health.listenerFailures.length > 0) {
      addIfUnique(lines, seenMessages, { ts: ts(), level: healthState === 'degraded' ? 'warn' : 'info', message: ipcHealthSummary(health, nowMs) });
    }

    // Recent bridge event timestamps
    if (health.lastEventAt) {
      const age = formatAge(health.lastEventAt, nowMs);
      addIfUnique(lines, seenMessages, { ts: ts(), level: 'info', message: `Bridge event ${age} ago` });
    }

    if (health.lastHeartbeatAt) {
      const age = formatAge(health.lastHeartbeatAt, nowMs);
      if (parseAgeMs(health.lastHeartbeatAt, nowMs) > STALE_IPC_AFTER_MS) {
        addIfUnique(lines, seenMessages, { ts: ts(), level: 'warn', message: `IPC heartbeat stale — ${age} ago` });
      }
    }

    if (health.pendingInvokes > 0) {
      addIfUnique(lines, seenMessages, { ts: ts(), level: 'info', message: `${health.pendingInvokes} pending IPC invoke${health.pendingInvokes === 1 ? '' : 's'}` });
    }
  }

  // 4. Diagnostic entries — newest first, skip any already covered by observer/dedup logic
  const diagnosticsCopy = [...sources.diagnostics].reverse();
  for (const entry of diagnosticsCopy) {
    const producedMessage = `${entry.source}: ${entry.message}`;
    if (seenMessages.has(producedMessage)) continue;
    seenMessages.add(producedMessage);
    lines.push({
      ts: formatTimestamp(entry.observedAt),
      level: entry.level,
      message: producedMessage,
    });
    if (lines.length >= maxLines) break;
  }

  // 5. Truncate to maxLines
  return lines.slice(0, maxLines);
}

function addIfUnique(lines: ConsoleLine[], seen: Set<string>, line: ConsoleLine): void {
  if (seen.has(line.message)) return;
  seen.add(line.message);
  lines.push(line);
}

/** Locale/options used for console timestamp formatting.
 *  Explicit locale and options ensure deterministic HH:MM:SS output across environments.
 *  Tests must use the same contract to detect future drift. */
export const TIMESTAMP_LOCALE = 'en-US';
export const TIMESTAMP_OPTIONS: Intl.DateTimeFormatOptions = { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false };

function formatTimestamp(isoString: string): string {
  const d = new Date(isoString);
  if (Number.isNaN(d.getTime())) return '--:--:--';
  return d.toLocaleTimeString(TIMESTAMP_LOCALE, TIMESTAMP_OPTIONS);
}

function formatAge(isoString: string, nowMs: number): string {
  const ageMs = Math.max(0, nowMs - Date.parse(isoString));
  if (Number.isNaN(ageMs)) return isoString;
  return formatDuration(ageMs);
}

function parseAgeMs(isoString: string, nowMs: number): number {
  return Math.max(0, nowMs - Date.parse(isoString));
}

function levelFromConnectionState(state: string): string {
  if (state === 'offline' || state === 'misconfigured' || state === 'git_error' || state === 'failed') return 'err';
  if (state === 'degraded' || state === 'path_not_visible' || state === 'not_git_repository') return 'warn';
  if (state === 'connected') return 'ok';
  return 'info';
}
