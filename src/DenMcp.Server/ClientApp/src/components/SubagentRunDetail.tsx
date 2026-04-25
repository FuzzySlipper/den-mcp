import { useCallback } from 'react';
import { getSubagentRun } from '../api/client';
import type { AgentStreamEntry, SubagentRunSummary } from '../api/types';
import { usePolling } from '../hooks/usePolling';
import { formatInfrastructureFailureReason, formatSubagentDuration, summarizeSubagentRunEntry } from '../subagentRuns';
import { truncate } from '../utils';

interface Props {
  run: SubagentRunSummary;
  onClose: () => void;
  onOpenTask: (taskId: number) => void;
  onOpenEntry: (entry: AgentStreamEntry) => void;
}

function formatLabel(value: string): string {
  return value.replace(/_/g, ' ');
}

function formatDate(iso: string): string {
  return new Date(`${iso}Z`).toLocaleString();
}

export function SubagentRunDetail({ run, onClose, onOpenTask, onOpenEntry }: Props) {
  const fetchRun = useCallback(
    () => getSubagentRun(run.run_id, {
      projectId: run.project_id ?? undefined,
      taskId: run.task_id ?? undefined,
    }),
    [run.project_id, run.run_id, run.task_id],
  );
  const { data: detail, error } = usePolling(fetchRun, run.state === 'running' || run.state === 'retrying' ? 2000 : 10_000);
  const summary = detail?.summary ?? run;
  const events = detail?.events ?? [run.latest];

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>Sub-agent run</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Run</dt>
            <dd className="mono-value">{summary.run_id}</dd>
            <dt>State</dt>
            <dd><span className={`subagent-state subagent-state-${summary.state}`}>{summary.state}</span></dd>
            {summary.role && (
              <>
                <dt>Role</dt>
                <dd>{summary.role}</dd>
              </>
            )}
            {summary.backend && (
              <>
                <dt>Backend</dt>
                <dd>{summary.backend}</dd>
              </>
            )}
            {summary.model && (
              <>
                <dt>Model</dt>
                <dd>{summary.model}</dd>
              </>
            )}
            {summary.pid != null && (
              <>
                <dt>PID</dt>
                <dd>{summary.pid}</dd>
              </>
            )}
            {summary.exit_code != null && (
              <>
                <dt>Exit</dt>
                <dd>{summary.exit_code}{summary.signal ? ` (${summary.signal})` : ''}</dd>
              </>
            )}
            {summary.output_status && (
              <>
                <dt>Output</dt>
                <dd>{summary.output_status}</dd>
              </>
            )}
            {summary.timeout_kind && (
              <>
                <dt>Timeout</dt>
                <dd>{summary.timeout_kind}</dd>
              </>
            )}
            {summary.infrastructure_failure_reason && (
              <>
                <dt>Infrastructure</dt>
                <dd>{formatInfrastructureFailureReason(summary.infrastructure_failure_reason)}</dd>
              </>
            )}
            {summary.infrastructure_warning_reason && (
              <>
                <dt>Warning</dt>
                <dd>{formatInfrastructureFailureReason(summary.infrastructure_warning_reason)}</dd>
              </>
            )}
            {summary.fallback_from_model && (
              <>
                <dt>Fallback</dt>
                <dd>{summary.fallback_from_model} → {summary.fallback_model ?? 'configured fallback'}{summary.fallback_from_exit_code != null ? ` (exit ${summary.fallback_from_exit_code})` : ''}</dd>
              </>
            )}
            {(summary.heartbeat_count > 0 || summary.assistant_output_count > 0) && (
              <>
                <dt>Progress</dt>
                <dd>{summary.heartbeat_count} heartbeats, {summary.assistant_output_count} outputs</dd>
              </>
            )}
            {summary.duration_ms != null && (
              <>
                <dt>Duration</dt>
                <dd>{formatSubagentDuration(summary.duration_ms)}</dd>
              </>
            )}
            {summary.project_id && (
              <>
                <dt>Project</dt>
                <dd>{summary.project_id}</dd>
              </>
            )}
            <dt>Events</dt>
            <dd>{summary.event_count}</dd>
          </dl>
        </div>

        {(summary.task_id != null || summary.artifact_dir) && (
          <div className="detail-section">
            <h3>Links</h3>
            <div className="detail-action-row">
              {summary.task_id != null && (
                <button type="button" className="dispatch-action" onClick={() => onOpenTask(summary.task_id!)}>
                  Open task #{summary.task_id}
                </button>
              )}
              {summary.artifact_dir && <span className="subagent-detail-artifact">{summary.artifact_dir}</span>}
            </div>
          </div>
        )}

        {error && (
          <div className="detail-section">
            <h3>Run Detail</h3>
            <div className="detail-description">Could not refresh run detail: {error.message}</div>
          </div>
        )}

        {summary.stderr_preview && (
          <div className="detail-section">
            <h3>Stderr</h3>
            <pre className="detail-pre">{summary.stderr_preview}</pre>
          </div>
        )}

        <div className="detail-section">
          <h3>Lifecycle</h3>
          <div className="subagent-event-list">
            {events.map(event => (
              <button
                key={event.id}
                type="button"
                className="subagent-event-item"
                onClick={() => onOpenEntry(event)}
              >
                <span className="subagent-event-time">{formatDate(event.created_at)}</span>
                <span className="stream-chip stream-chip-event">{formatLabel(event.event_type)}</span>
                <span className="subagent-event-body">{truncate(summarizeSubagentRunEntry(event), 140)}</span>
              </button>
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
