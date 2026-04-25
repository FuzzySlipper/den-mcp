import type { AgentStreamEntry, SubagentRunState } from './api/types';

export function stateFromSubagentEvent(eventType: string): SubagentRunState {
  switch (eventType) {
    case 'subagent_started':
      return 'running';
    case 'subagent_fallback_started':
      return 'retrying';
    case 'subagent_completed':
      return 'complete';
    case 'subagent_timeout':
      return 'timeout';
    case 'subagent_aborted':
      return 'aborted';
    case 'subagent_failed':
      return 'failed';
    default:
      return 'unknown';
  }
}

export function formatSubagentDuration(ms: number | null): string {
  if (ms == null) return '';
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const minutes = Math.floor(ms / 60_000);
  const seconds = Math.floor((ms % 60_000) / 1000);
  return `${minutes}m${seconds}s`;
}

export function summarizeSubagentRunEntry(entry: AgentStreamEntry): string {
  const body = entry.body?.replace(/\s+/g, ' ').trim();
  return body || entry.event_type.replace(/_/g, ' ');
}
