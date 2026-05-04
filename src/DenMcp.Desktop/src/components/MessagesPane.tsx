import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  MessagesSnapshot,
} from '../electron/sidecarProtocol.ts';
import {
  messagesGetSnapshot,
  type MessagesGetSnapshotRequest,
} from '../desktop/sidecarBridgeApi.ts';
import {
  buildMessagesView,
  filterMessagesByType,
  type MessageFilterType,
  type MessagesView,
  type MessageRowView,
} from '../messagesView.ts';

interface Props {
  projectId: string | null;
  taskId?: number | null;
}

const REFRESH_INTERVAL_MS = 30_000;

export function MessagesPane({ projectId, taskId }: Props) {
  const [snapshot, setSnapshot] = useState<MessagesSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expandedMessageId, setExpandedMessageId] = useState<number | null>(null);
  const [messageFilter, setMessageFilter] = useState<MessageFilterType>('all');
  const [lastRefreshAt, setLastRefreshAt] = useState<string | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => { mountedRef.current = true; return () => { mountedRef.current = false; }; }, []);

  const fetchSnapshot = useCallback(async () => {
    if (!projectId) return;
    setLoading(true);
    setError(null);
    try {
      const request: MessagesGetSnapshotRequest = {
        project_id: projectId,
        task_id: taskId ?? null,
      };
      const result = await messagesGetSnapshot(request);
      if (mountedRef.current) {
        setSnapshot(result as unknown as MessagesSnapshot);
        setLastRefreshAt(new Date().toISOString());
      }
    } catch (err) {
      if (mountedRef.current) {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, [projectId, taskId]);

  // Initial load and periodic refresh
  useEffect(() => {
    void fetchSnapshot();
    const interval = window.setInterval(() => void fetchSnapshot(), REFRESH_INTERVAL_MS);
    return () => window.clearInterval(interval);
  }, [fetchSnapshot]);

  const view = useMemo(() => {
    const raw = buildMessagesView(snapshot);
    const filtered = filterMessagesByType(raw.messages, messageFilter);
    return {
      ...raw,
      messages: filtered,
      isEmpty: filtered.length === 0,
    };
  }, [snapshot, messageFilter]);

  // No project selected
  if (!projectId) {
    return (
      <section className="panel surface-panel messages-pane">
        <p className="eyebrow">Messages</p>
        <h2>Project messages</h2>
        <div className="empty-state">
          <strong>No project selected.</strong>
          <p>Select a project from the left rail to load messages.</p>
        </div>
      </section>
    );
  }

  return (
    <section className="panel messages-pane">
      <MessagesHeader
        projectId={projectId}
        view={view}
        loading={loading}
        error={error}
        lastRefreshAt={lastRefreshAt}
        onRefresh={() => void fetchSnapshot()}
      />
      {snapshot ? (
        <MessagesFilterBar
          currentFilter={messageFilter}
          onChange={setMessageFilter}
        />
      ) : null}
      {snapshot && !view.isEmpty ? (
        <div className="messages-list">
          {view.messages.map((msg) => (
            <MessageCard
              key={msg.id}
              message={msg}
              expanded={expandedMessageId === msg.id}
              onToggle={() => setExpandedMessageId(expandedMessageId === msg.id ? null : msg.id)}
            />
          ))}
        </div>
      ) : (
        <div className="empty-state">
          <strong>{loading ? 'Loading messages…' : emptyStateLabel(messageFilter)}</strong>
          <p>{loading ? 'Fetching the latest messages from the Den Desktop bridge.' : emptyStateDescription(messageFilter)}</p>
        </div>
      )}
      <MessagesFreshnessBanner view={view.freshness} />
    </section>
  );
}

// ── Sub-components ──────────────────────────────────────────────

function MessagesFilterBar({
  currentFilter,
  onChange,
}: {
  currentFilter: MessageFilterType;
  onChange: (filter: MessageFilterType) => void;
}) {
  const filters: MessageFilterType[] = ['all', 'messages', 'stream', 'thoughts', 'user', 'notifications'];
  return (
    <div className="messages-filter-bar">
      {filters.map((f) => (
        <button
          key={f}
          type="button"
          className={`messages-filter-btn${currentFilter === f ? ' active' : ''}`}
          onClick={() => onChange(f)}
        >
          {f === 'all' ? 'All' : f.charAt(0).toUpperCase() + f.slice(1)}
        </button>
      ))}
    </div>
  );
}

function emptyStateLabel(filter: MessageFilterType): string {
  switch (filter) {
    case 'messages': return 'No regular messages found.';
    case 'stream': return 'No workflow stream entries found.';
    case 'thoughts': return 'No thought entries found.';
    case 'user': return 'No user messages found.';
    case 'notifications': return 'No notifications found.';
    default: return 'No messages found.';
  }
}

function emptyStateDescription(filter: MessageFilterType): string {
  switch (filter) {
    case 'stream': return 'Agent stream data requires backend support. Only task-thread and project messages are currently loaded.';
    case 'thoughts': return 'Agent thought data requires backend support. The current filter provides a best-effort placeholder classification.';
    case 'user': return 'No messages sent by the user identity were found in the current view.';
    case 'notifications': return 'Notifications appear when agents send user-facing alerts. None are present in the current view.';
    default: return 'Messages will appear here once they are sent in Den for this project.';
  }
}

function MessagesHeader({
  projectId,
  view,
  loading,
  error,
  lastRefreshAt,
  onRefresh,
}: {
  projectId: string;
  view: MessagesView;
  loading: boolean;
  error: string | null;
  lastRefreshAt: string | null;
  onRefresh: () => void;
}) {
  const h = view.header;
  return (
    <div className="messages-header">
      <div className="messages-header-title">
        <p className="eyebrow">Messages · {projectId}{h.isFiltered ? ` · ${h.filterDescription}` : ''}</p>
        <h2>Project messages</h2>
      </div>
      <div className="messages-header-metrics">
        <div className="messages-header-counts">
          <span className="messages-count-item"><strong>{h.totalCount}</strong> messages</span>
          {h.unreadCount > 0 && <span className="messages-count-item accent"><strong>{h.unreadCount}</strong> unread</span>}
        </div>
      </div>
      <div className="messages-header-actions">
        <button type="button" onClick={onRefresh} disabled={loading}>{loading ? 'Refreshing…' : 'Refresh'}</button>
        {error ? <span className="messages-error">{error}</span> : null}
      </div>
    </div>
  );
}

function MessageCard({ message, expanded, onToggle }: { message: MessageRowView; expanded: boolean; onToggle: () => void }) {
  return (
    <article
      className={`messages-card ${expanded ? 'expanded' : ''} ${message.isUnread ? 'unread' : ''} ${message.intent === 'notification' ? 'notification' : ''}`}
      role="button"
      tabIndex={0}
      onClick={onToggle}
      onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onToggle(); } }}
      aria-pressed={expanded}
      aria-label={`Message #${message.id} from ${message.sender}`}
    >
      <div className="messages-card-topline">
        <div className="messages-card-id-sender">
          <span className="messages-card-id">#{message.id}</span>
          <span className={`messages-card-sender sender-tone-${message.senderTone}`}>{message.sender}</span>
          {message.metadataTypeLabel && <span className="chip">{message.metadataTypeLabel}</span>}
          {message.intent === 'notification' && <span className="chip accent">🔔 notification</span>}
          {message.intent && message.intent !== 'notification' && <span className="chip idle">{message.intent}</span>}
        </div>
        <span className="messages-card-time">{message.relativeTime}</span>
      </div>
      <div className="messages-card-content">
        {expanded ? (
          <pre className="messages-card-full">{message.contentFull}</pre>
        ) : (
          <p className="messages-card-preview">{message.contentPreview}</p>
        )}
      </div>
    </article>
  );
}

function MessagesFreshnessBanner({ view }: { view: MessagesView['freshness'] }) {
  if (!view.isStale && view.warnings.length === 0 && view.errors.length === 0) {
    return null;
  }

  return (
    <div className="messages-freshness-panel">
      {view.isStale && (
        <div className="messages-freshness-stale">
          <strong>Snapshot may be stale</strong>
          <p>Data source: {view.source}. Refresh to get the latest state.</p>
        </div>
      )}
      {view.warnings.length > 0 && (
        <ul className="warning-list">
          {view.warnings.map((w, i) => <li key={i}>{w}</li>)}
        </ul>
      )}
      {view.errors.length > 0 && (
        <div className="error-note">
          {view.errors.map((e, i) => <p key={i}>{e}</p>)}
        </div>
      )}
    </div>
  );
}
