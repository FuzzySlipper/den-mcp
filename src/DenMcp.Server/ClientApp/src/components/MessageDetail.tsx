import { useEffect, useState } from 'react';
import type { Message, Thread } from '../api/types';
import { getThread } from '../api/client';
import { formatTimeAgo } from '../utils';

interface Props {
  message: Message;
  onClose: () => void;
}

export function MessageDetail({ message, onClose }: Props) {
  const [thread, setThread] = useState<Thread | null>(null);

  useEffect(() => {
    let cancelled = false;
    getThread(message.project_id, message.id)
      .then(t => { if (!cancelled) setThread(t); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [message.project_id, message.id]);

  return (
    <div className="detail-overlay">
      <div className="detail-header">
        <h2>Message from {message.sender}</h2>
        <button className="detail-close" onClick={onClose}>✕</button>
      </div>
      <div className="detail-body">
        <div className="detail-section">
          <dl className="detail-meta">
            <dt>From</dt>
            <dd>{message.sender}</dd>
            <dt>Project</dt>
            <dd>{message.project_id}</dd>
            <dt>Time</dt>
            <dd>{new Date(message.created_at + 'Z').toLocaleString()}</dd>
            {message.task_id != null && (
              <><dt>Task</dt><dd>#{message.task_id}</dd></>
            )}
            {message.thread_id != null && (
              <><dt>Reply to</dt><dd>#{message.thread_id}</dd></>
            )}
          </dl>
        </div>

        <div className="detail-section">
          <h3>Content</h3>
          <div className="detail-description">{message.content}</div>
        </div>

        {thread && thread.replies.length > 0 && (
          <div className="detail-section">
            <h3>Replies</h3>
            {thread.replies.map(reply => (
              <div key={reply.id} style={{ marginBottom: 12 }}>
                <div style={{ display: 'flex', gap: 6, alignItems: 'baseline', marginBottom: 2 }}>
                  <span className="message-time">{formatTimeAgo(reply.created_at)}</span>
                  <span className="message-sender">{reply.sender}:</span>
                </div>
                <div className="detail-description" style={{ paddingLeft: 10 }}>
                  {reply.content}
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
