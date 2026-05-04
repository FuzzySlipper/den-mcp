/**
 * Pure view-model helpers for the Messages tab.
 *
 * Transforms MessagesSnapshot from the sidecar bridge into
 * display-ready structures for the renderer. No React or bridge calls here.
 */

import type {
  MessagesSnapshot,
  MessagesMessageRow,
  MessagesFreshness,
} from './electron/sidecarProtocol.ts';

// ── Display types ──────────────────────────────────────────────

export type MessageSenderTone = 'ok' | 'warn' | 'err' | 'accent' | 'idle' | 'info' | 'running';

export type MessageFilterType = 'all' | 'messages' | 'stream' | 'thoughts' | 'user' | 'notifications';

export interface MessageRowView {
  id: number;
  sender: string;
  contentPreview: string;
  contentFull: string;
  intent: string | null;
  metadataType: string | null;
  metadataTypeLabel: string;
  taskId: number | null;
  threadId: number | null;
  createdAt: string | null;
  relativeTime: string;
  isUnread: boolean;
  senderTone: MessageSenderTone;
  hasThread: boolean;
}

export interface MessagesHeaderView {
  projectId: string;
  taskId: number | null;
  totalCount: number;
  unreadCount: number;
  lastUpdatedLabel: string;
  isFiltered: boolean;
  filterDescription: string;
}

export interface MessagesFreshnessView {
  isPartial: boolean;
  isStale: boolean;
  warnings: string[];
  errors: string[];
  source: string;
}

export interface MessagesView {
  header: MessagesHeaderView;
  messages: MessageRowView[];
  threadRoot: MessageRowView | null;
  freshness: MessagesFreshnessView;
  isEmpty: boolean;
}

// ── Constants ──────────────────────────────────────────────────

const PACKET_TYPES = new Set([
  'coder_context_packet',
  'implementation_packet',
  'validation_packet',
  'drift_check_packet',
  'review_request_packet',
  'rereview_packet',
  'review_findings_packet',
  'merge_summary',
  'planning_summary',
  'review_feedback',
  'review_request',
]);

const METADATA_TYPE_LABELS: Record<string, string> = {
  coder_context_packet: 'Context prepared',
  implementation_packet: 'Implementation posted',
  validation_packet: 'Validation completed',
  drift_check_packet: 'Drift check',
  review_request_packet: 'Review requested',
  rereview_packet: 'Rereview requested',
  review_findings_packet: 'Review findings',
  merge_summary: 'Merged',
  planning_summary: 'Planning summary',
  review_feedback: 'Review feedback',
  review_request: 'Review requested',
};

const STALE_FRESHNESS_MS = 120_000;

const PACKET_SENDER_TONES: Record<string, MessageSenderTone> = {
  coder_context_packet: 'info',
  implementation_packet: 'running',
  validation_packet: 'ok',
  drift_check_packet: 'warn',
  review_request_packet: 'accent',
  rereview_packet: 'accent',
  review_findings_packet: 'accent',
  merge_summary: 'ok',
  planning_summary: 'info',
  review_feedback: 'accent',
  review_request: 'accent',
};

// ── Public functions ───────────────────────────────────────────

/**
 * Filter messages by type.
 * - 'all' — no filtering
 * - 'messages' — regular messages (no packet metadata_type)
 * - 'stream' — workflow/packet messages (metadata_type in PACKET_TYPES)
 * - 'thoughts' — best-effort thought/observation classification
 * - 'user' — messages where sender === 'user' (case-insensitive)
 * - 'notifications' — messages with intent === 'notification'
 *
 * Note: 'stream', 'thoughts' filters need backend support to include
 * agent stream entries and thought data. For now they filter from
 * the available task-thread/project messages only.
 */
export function filterMessagesByType(
  messages: MessageRowView[],
  filter: MessageFilterType,
): MessageRowView[] {
  if (filter === 'all') return messages;

  return messages.filter((msg) => {
    switch (filter) {
      case 'messages':
        // Regular messages have no metadata_type, or metadata_type not in the known packet list
        return !msg.metadataType || !PACKET_TYPES.has(msg.metadataType);
      case 'stream':
        return !!msg.metadataType && PACKET_TYPES.has(msg.metadataType);
      case 'thoughts':
        return isThoughtEntry(msg);
      case 'user':
        return msg.sender.toLowerCase() === 'user';
      case 'notifications':
        return msg.intent === 'notification';
      default:
        return true;
    }
  });
}

function isThoughtEntry(msg: MessageRowView): boolean {
  const sender = msg.sender.toLowerCase();
  const isCoderOrReviewer = sender.includes('coder') || sender.includes('reviewer');
  const hasThoughtIndicators =
    msg.contentFull.toLowerCase().includes('thinking') ||
    msg.contentFull.toLowerCase().includes('observation') ||
    msg.contentFull.toLowerCase().includes('analysis');
  return isCoderOrReviewer && hasThoughtIndicators;
}

export function buildMessagesView(
  snapshot: MessagesSnapshot | null,
  nowMs = Date.now(),
): MessagesView {
  if (!snapshot) {
    return emptyMessagesView();
  }

  const messages = snapshot.messages.map((msg) => buildMessageRowView(msg, nowMs));
  const threadRoot = snapshot.thread_root ? buildMessageRowView(snapshot.thread_root, nowMs) : null;
  const header = buildHeader(snapshot, nowMs);
  const freshness = buildFreshness(snapshot.freshness, snapshot.generated_at, nowMs);

  return {
    header,
    messages,
    threadRoot,
    freshness,
    isEmpty: messages.length === 0,
  };
}

export function senderTone(message: MessagesMessageRow): MessageSenderTone {
  const metadataType = message.metadata_type;
  if (metadataType && metadataType in PACKET_SENDER_TONES) {
    return PACKET_SENDER_TONES[metadataType];
  }
  const sender = message.sender.toLowerCase();
  if (sender.includes('reviewer')) return 'accent';
  if (sender.includes('coder') || sender.includes('pi')) return 'running';
  if (sender === 'user') return 'ok';
  return 'idle';
}

export function metadataTypeLabel(metadataType: string | null): string {
  if (!metadataType) return '';
  return METADATA_TYPE_LABELS[metadataType] ?? metadataType.replaceAll('_', ' ');
}

export function truncateContent(content: string, maxLen = 200): string {
  if (!content) return '';
  const flat = content.replace(/\n+/g, ' ').trim();
  return flat.length <= maxLen ? flat : flat.slice(0, maxLen) + '…';
}

export function relativeTimeLabel(timestamp: string | null | undefined, nowMs = Date.now()): string {
  if (!timestamp) return '—';
  const parsed = Date.parse(timestamp);
  if (Number.isNaN(parsed)) return timestamp;
  const seconds = Math.max(0, Math.round((nowMs - parsed) / 1000));
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
}

// ── Internal helpers ───────────────────────────────────────────

function emptyMessagesView(): MessagesView {
  return {
    header: {
      projectId: '',
      taskId: null,
      totalCount: 0,
      unreadCount: 0,
      lastUpdatedLabel: '—',
      isFiltered: false,
      filterDescription: '',
    },
    messages: [],
    threadRoot: null,
    freshness: { isPartial: false, isStale: false, warnings: [], errors: [], source: 'none' },
    isEmpty: true,
  };
}

function buildMessageRowView(msg: MessagesMessageRow, nowMs: number): MessageRowView {
  return {
    id: msg.id,
    sender: msg.sender,
    contentPreview: truncateContent(msg.content_summary || msg.content),
    contentFull: msg.content,
    intent: msg.intent,
    metadataType: msg.metadata_type,
    metadataTypeLabel: metadataTypeLabel(msg.metadata_type),
    taskId: msg.task_id ?? null,
    threadId: msg.thread_id ?? null,
    createdAt: msg.created_at,
    relativeTime: relativeTimeLabel(msg.created_at, nowMs),
    isUnread: msg.is_unread,
    senderTone: senderTone(msg),
    hasThread: msg.thread_id != null,
  };
}

function buildHeader(snapshot: MessagesSnapshot, nowMs: number): MessagesHeaderView {
  const isFiltered = snapshot.task_id != null || snapshot.thread_id != null;
  const parts: string[] = [];
  if (snapshot.task_id != null) parts.push(`task #${snapshot.task_id}`);
  if (snapshot.thread_id != null) parts.push(`thread #${snapshot.thread_id}`);

  return {
    projectId: snapshot.project_id,
    taskId: snapshot.task_id ?? null,
    totalCount: snapshot.total_count,
    unreadCount: snapshot.unread_count,
    lastUpdatedLabel: relativeTimeLabel(snapshot.generated_at, nowMs),
    isFiltered,
    filterDescription: parts.join(' · '),
  };
}

function buildFreshness(
  freshness: MessagesFreshness,
  generatedAt: string,
  nowMs: number,
): MessagesFreshnessView {
  const generatedMs = Date.parse(generatedAt);
  const isStale = !Number.isNaN(generatedMs) && (nowMs - generatedMs) > STALE_FRESHNESS_MS;

  return {
    isPartial: freshness.is_partial,
    isStale: isStale || freshness.is_partial,
    warnings: freshness.warnings ?? [],
    errors: freshness.errors ?? [],
    source: freshness.source,
  };
}
