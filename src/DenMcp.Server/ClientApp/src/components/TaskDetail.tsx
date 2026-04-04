import { useEffect, useState } from 'react';
import type { TaskDetail as TaskDetailType, TaskStatus } from '../api/types';
import { getTask, updateTask } from '../api/client';
import { formatTimeAgo } from '../utils';

interface Props {
  projectId: string;
  taskId: number;
  onClose: () => void;
}

const STATUSES: TaskStatus[] = ['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'];

export function TaskDetail({ projectId, taskId, onClose }: Props) {
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

        {detail.dependencies.length > 0 && (
          <div className="detail-section">
            <h3>Dependencies</h3>
            {detail.dependencies.map(dep => (
              <div key={dep.task_id} className="list-item" style={{ cursor: 'default' }}>
                <span className={`badge badge-${dep.status}`}>{dep.status}</span>
                {' '}#{dep.task_id} {dep.title}
              </div>
            ))}
          </div>
        )}

        {detail.subtasks.length > 0 && (
          <div className="detail-section">
            <h3>Subtasks</h3>
            {detail.subtasks.map(sub => (
              <div key={sub.id} className="list-item" style={{ cursor: 'default' }}>
                <span className={`badge badge-${sub.status}`}>{sub.status}</span>
                {' '}#{sub.id} {sub.title}
              </div>
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
              <div key={msg.id} className="message-item" style={{ cursor: 'default' }}>
                <span className="message-time">{formatTimeAgo(msg.created_at)}</span>
                <span className="message-sender">{msg.sender}:</span>
                <span className="message-content">{msg.content.replace(/\n/g, ' ')}</span>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
