import type { Message } from '../api/types';
import { formatTimeAgo, truncate } from '../utils';

interface Props {
  messages: Message[];
  isGlobal: boolean;
  onSelect: (message: Message) => void;
}

export function MessageFeed({ messages, isGlobal, onSelect }: Props) {
  if (messages.length === 0) {
    return <div className="empty">No messages</div>;
  }

  return (
    <>
      {messages.map(m => (
        <div key={m.id} className="message-item" onClick={() => onSelect(m)}>
          <span className="message-time">{formatTimeAgo(m.created_at)}</span>
          {isGlobal && <span className="message-project-tag">[{truncate(m.project_id, 12)}]</span>}
          <span className="message-sender">{m.sender}:</span>
          <span className="message-content">{truncate(m.content.replace(/\n/g, ' '), 60)}</span>
        </div>
      ))}
    </>
  );
}
