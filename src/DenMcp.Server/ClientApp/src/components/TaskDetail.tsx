import { useEffect, useState } from 'react';
import type {
  Message,
  ReviewFinding,
  ReviewTimelineEntry,
  ReviewVerdict,
  TaskDetail as TaskDetailType,
  TaskStatus,
} from '../api/types';
import { getTask, updateTask } from '../api/client';
import { formatTimeAgo } from '../utils';
import { messageIntentLabel } from '../messageIntents';

interface Props {
  projectId: string;
  taskId: number;
  onSelectTask: (taskId: number) => void;
  onSelectMessage: (message: Message) => void;
  onClose: () => void;
}

const STATUSES: TaskStatus[] = ['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'];

function formatLabel(value: string): string {
  return value.replace(/_/g, ' ');
}

function formatVerdict(verdict: ReviewVerdict | null): string {
  return verdict ? formatLabel(verdict) : 'pending';
}

function formatDelta(detail: TaskDetailType): string {
  const delta = detail.review_workflow.current_round?.delta_diff;
  return delta?.base_commit ? `${delta.base_commit}..${delta.head_commit}` : '(initial review)';
}

function formatTimeline(entry: ReviewTimelineEntry): string {
  const parts = [`${entry.total_finding_count} findings`];
  if (entry.open_finding_count > 0) parts.push(`${entry.open_finding_count} open`);
  if (entry.addressed_finding_count > 0) parts.push(`${entry.addressed_finding_count} addressed`);
  if (entry.resolved_finding_count > 0) parts.push(`${entry.resolved_finding_count} resolved`);
  return parts.join(' · ');
}

function renderFindingMeta(finding: ReviewFinding): string[] {
  const parts: string[] = [];
  if (finding.file_references?.length) parts.push(`Files: ${finding.file_references.join(', ')}`);
  if (finding.test_commands?.length) parts.push(`Tests: ${finding.test_commands.join(', ')}`);
  if (finding.status_notes) parts.push(`Status note: ${finding.status_notes}`);
  else if (finding.response_notes) parts.push(`Response: ${finding.response_notes}`);
  return parts;
}

export function TaskDetail({ projectId, taskId, onSelectTask, onSelectMessage, onClose }: Props) {
  const [detail, setDetail] = useState<TaskDetailType | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    getTask(projectId, taskId)
      .then(d => { if (!cancelled) setDetail(d); })
      .catch(e => { if (!cancelled) setError(e.message); });
    return () => { cancelled = true; };
  }, [projectId, taskId]);

  const handleStatusChange = async (newStatus: string) => {
    try {
      await updateTask(projectId, taskId, 'web-ui', { status: newStatus });
      const d = await getTask(projectId, taskId);
      setDetail(d);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  };

  if (error) {
    return (
      <div className="detail-overlay">
        <div className="detail-header">
          <h2>Error</h2>
          <button className="detail-close" onClick={onClose}>✕</button>
        </div>
        <div className="detail-body"><div className="empty">{error}</div></div>
      </div>
    );
  }

  if (!detail) {
    return (
      <div className="detail-overlay">
        <div className="detail-header">
          <h2>Loading...</h2>
          <button className="detail-close" onClick={onClose}>✕</button>
        </div>
      </div>
    );
  }

  const { task } = detail;
  const currentRound = detail.review_workflow.current_round;

  const handleTaskNavigation = (nextTaskId: number) => {
    if (nextTaskId !== task.id) {
      onSelectTask(nextTaskId);
    }
  };

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>#{task.id} {task.title}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>Status</dt>
            <dd>
              <select
                className="status-select"
                value={task.status}
                onChange={e => handleStatusChange(e.target.value)}
              >
                {STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
              </select>
            </dd>
            <dt>Priority</dt>
            <dd className={`priority-${task.priority}`}>P{task.priority}</dd>
            <dt>Assigned</dt>
            <dd>{task.assigned_to ?? '(none)'}</dd>
            {task.tags && task.tags.length > 0 && (
              <>
                <dt>Tags</dt>
                <dd>{task.tags.join(', ')}</dd>
              </>
            )}
          </dl>
        </div>

        {detail.review_workflow.review_round_count > 0 && currentRound && (
          <div className="detail-section">
            <h3>Review Workflow</h3>
            <dl className="detail-meta">
              <dt>Current Round</dt>
              <dd>R{currentRound.round_number} on <code>{currentRound.branch}</code></dd>
              <dt>Verdict</dt>
              <dd>
                <span className={`review-pill review-pill-${detail.review_workflow.current_verdict ?? 'pending'}`}>
                  {formatVerdict(detail.review_workflow.current_verdict)}
                </span>
              </dd>
              <dt>Reviewed Diff</dt>
              <dd><code>{currentRound.preferred_diff.base_ref}...{currentRound.preferred_diff.head_ref}</code></dd>
              <dt>Delta</dt>
              <dd><code>{formatDelta(detail)}</code></dd>
              <dt>Open Findings</dt>
              <dd>{detail.review_workflow.unresolved_finding_count}</dd>
            </dl>
          </div>
        )}

        {detail.open_review_findings.length > 0 && (
          <div className="detail-section">
            <h3>Open Review Findings</h3>
            {detail.open_review_findings.map(finding => (
              <div key={finding.id} className="review-card">
                <div className="review-card-head">
                  <strong>{finding.finding_key}</strong>
                  <div className="review-pill-row">
                    <span className={`review-pill review-pill-${finding.category}`}>{formatLabel(finding.category)}</span>
                    <span className={`review-pill review-pill-${finding.status}`}>{formatLabel(finding.status)}</span>
                  </div>
                </div>
                <div className="review-summary">{finding.summary}</div>
                {renderFindingMeta(finding).map(line => (
                  <div key={line} className="review-subtle">{line}</div>
                ))}
              </div>
            ))}
          </div>
        )}

        {detail.review_workflow.timeline.length > 0 && (
          <div className="detail-section">
            <h3>Review Timeline</h3>
            {detail.review_workflow.timeline.map(entry => (
              <div key={entry.review_round_id} className="review-card">
                <div className="review-card-head">
                  <strong>R{entry.review_round_number}</strong>
                  <span className={`review-pill review-pill-${entry.verdict ?? 'pending'}`}>
                    {formatVerdict(entry.verdict)}
                  </span>
                </div>
                <div className="review-summary">
                  <code>{entry.branch}</code> · {formatTimeline(entry)}
                </div>
                <div className="review-subtle">
                  Requested by {entry.requested_by} {formatTimeAgo(entry.requested_at)}
                  {entry.verdict_at ? ` · verdict ${formatTimeAgo(entry.verdict_at)}` : ''}
                </div>
              </div>
            ))}
          </div>
        )}

        {detail.dependencies.length > 0 && (
          <div className="detail-section">
            <h3>Dependencies</h3>
            {detail.dependencies.map(dep => (
              <button
                key={dep.task_id}
                type="button"
                className="list-item detail-nav-button"
                onClick={() => handleTaskNavigation(dep.task_id)}
                title={`Open task #${dep.task_id}`}
              >
                <span className={`badge badge-${dep.status}`}>{dep.status}</span>
                {' '}#{dep.task_id} {dep.title}
              </button>
            ))}
          </div>
        )}

        {detail.subtasks.length > 0 && (
          <div className="detail-section">
            <h3>Subtasks</h3>
            {detail.subtasks.map(sub => (
              <button
                key={sub.id}
                type="button"
                className="list-item detail-nav-button"
                onClick={() => handleTaskNavigation(sub.id)}
                title={`Open task #${sub.id}`}
              >
                <span className={`badge badge-${sub.status}`}>{sub.status}</span>
                {' '}#{sub.id} {sub.title}
              </button>
            ))}
          </div>
        )}

        {task.description && (
          <div className="detail-section">
            <h3>Description</h3>
            <div className="detail-description">{task.description}</div>
          </div>
        )}

        {detail.recent_messages.length > 0 && (
          <div className="detail-section">
            <h3>Recent Messages</h3>
            {detail.recent_messages.map(msg => (
              <button
                key={msg.id}
                type="button"
                className="message-item detail-nav-button detail-message-button"
                onClick={() => onSelectMessage(msg)}
                title={msg.thread_id != null ? `Open thread #${msg.thread_id}` : `Open message #${msg.id}`}
              >
                <span className="message-time">{formatTimeAgo(msg.created_at)}</span>
                <span className={`intent-chip intent-${msg.intent}`}>{messageIntentLabel(msg.intent)}</span>
                <span className="message-sender">{msg.sender}:</span>
                <span className="message-content">{msg.content.replace(/\n/g, ' ')}</span>
              </button>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
