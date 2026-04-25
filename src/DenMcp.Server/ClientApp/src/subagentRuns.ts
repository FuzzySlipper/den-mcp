import type { AgentStreamEntry, SubagentRunState } from './api/types';

export function stateFromSubagentEvent(eventType: string): SubagentRunState {
  switch (eventType) {
    case 'subagent_started':
    case 'subagent_process_started':
    case 'subagent_heartbeat':
    case 'subagent_assistant_output':
    case 'subagent_prompt_echo_detected':
      return 'running';
    case 'subagent_fallback_started':
      return 'retrying';
    case 'subagent_completed':
      return 'complete';
    case 'subagent_timeout':
    case 'subagent_startup_timeout':
    case 'subagent_terminal_drain_timeout':
      return 'timeout';
    case 'subagent_aborted':
    case 'subagent_abort':
      return 'aborted';
    case 'subagent_failed':
    case 'subagent_spawn_error':
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

export function formatInfrastructureFailureReason(reason: string | null): string {
  switch (reason) {
    case 'extension_load':
      return 'extension load';
    case 'extension_runtime':
      return 'extension runtime';
    case 'child_error':
      return 'child process';
    case 'forced_kill':
      return 'forced kill';
    case null:
      return '';
    default:
      return reason.replace(/_/g, ' ');
  }
}

export function summarizeSubagentRunEntry(entry: AgentStreamEntry): string {
  const body = entry.body?.replace(/\s+/g, ' ').trim();
  return body || entry.event_type.replace(/_/g, ' ');
}
