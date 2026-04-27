import type {
  Project,
  ProjectWithStats,
  TaskSummary,
  TaskDetail,
  ProjectTask,
  Message,
  MessageFeedItem,
  Thread,
  DocumentSummary,
  Document,
  DocumentSearchResult,
  DocType,
  LibrarianResponse,
  AgentSession,
  AgentStreamEntry,
  AttentionItem,
  SubagentRunSummary,
  SubagentRunDetail,
  DispatchEntry,
  ReviewPacketResult,
} from './types';

async function get<T>(url: string): Promise<T> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`GET ${url}: ${res.status}`);
  return res.json();
}

async function put<T>(url: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`PUT ${url}: ${res.status}`);
  return res.json();
}

async function post<T>(url: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${url}: ${res.status}`);
  return res.json();
}

function esc(s: string): string {
  return encodeURIComponent(s);
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const parts = Object.entries(params)
    .filter(([, v]) => v != null)
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return parts.length > 0 ? `?${parts.join('&')}` : '';
}

// Projects

export function listProjects(): Promise<Project[]> {
  return get('/api/projects');
}

export function getProject(id: string, agent?: string): Promise<ProjectWithStats> {
  const q = buildQuery({ agent });
  return get(`/api/projects/${esc(id)}${q}`);
}

// Tasks

export interface ListTasksOpts {
  status?: string;
  assignedTo?: string;
  tags?: string;
  priority?: number;
  parentId?: number;
  tree?: boolean;
}

export function listTasks(projectId: string, opts: ListTasksOpts = {}): Promise<TaskSummary[]> {
  const q = buildQuery({
    status: opts.status,
    assignedTo: opts.assignedTo,
    tags: opts.tags,
    priority: opts.priority,
    parentId: opts.parentId,
    tree: opts.tree,
  });
  return get(`/api/projects/${esc(projectId)}/tasks${q}`);
}

export function getTask(projectId: string, taskId: number): Promise<TaskDetail> {
  return get(`/api/projects/${esc(projectId)}/tasks/${taskId}`);
}

export function updateTask(
  projectId: string,
  taskId: number,
  agent: string,
  changes: Record<string, unknown>,
): Promise<ProjectTask> {
  return put(`/api/projects/${esc(projectId)}/tasks/${taskId}`, { agent, ...changes });
}

export function requestReview(
  projectId: string,
  taskId: number,
  body: Record<string, unknown>,
): Promise<ReviewPacketResult> {
  return post(`/api/projects/${esc(projectId)}/tasks/${taskId}/review/request`, body);
}

export function postReviewFindings(
  projectId: string,
  taskId: number,
  body: Record<string, unknown>,
): Promise<ReviewPacketResult> {
  return post(`/api/projects/${esc(projectId)}/tasks/${taskId}/review/findings/post`, body);
}

export function getNextTask(projectId: string, assignedTo?: string): Promise<ProjectTask | null> {
  const q = buildQuery({ assignedTo });
  return get<ProjectTask | { message: string }>(`/api/projects/${esc(projectId)}/tasks/next${q}`)
    .then(res => ('message' in res ? null : res));
}

// Messages

export interface GetMessagesOpts {
  taskId?: number;
  since?: string;
  unreadFor?: string;
  limit?: number;
  intent?: string;
}

export function getMessage(projectId: string, messageId: number): Promise<Message | null> {
  return fetch(`/api/projects/${esc(projectId)}/messages/${messageId}`)
    .then(res => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`GET message: ${res.status}`);
      return res.json();
    });
}

export function getMessages(projectId: string, opts: GetMessagesOpts = {}): Promise<Message[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    since: opts.since,
    unreadFor: opts.unreadFor,
    limit: opts.limit,
    intent: opts.intent,
  });
  return get(`/api/projects/${esc(projectId)}/messages${q}`);
}

export interface GetMessageFeedOpts {
  limit?: number;
  intent?: string;
}

export function getMessageFeed(projectId: string, opts: GetMessageFeedOpts = {}): Promise<MessageFeedItem[]> {
  const q = buildQuery({ limit: opts.limit, intent: opts.intent });
  return get(`/api/projects/${esc(projectId)}/messages/feed${q}`);
}

export function getThread(projectId: string, threadId: number): Promise<Thread> {
  return get(`/api/projects/${esc(projectId)}/messages/thread/${threadId}`);
}

// Documents

export function listDocuments(projectId?: string, docType?: string, tags?: string): Promise<DocumentSummary[]> {
  if (projectId) {
    const q = buildQuery({ docType, tags });
    return get(`/api/projects/${esc(projectId)}/documents${q}`);
  }
  const q = buildQuery({ projectId, docType, tags });
  return get(`/api/documents${q}`);
}

export function getDocument(projectId: string, slug: string): Promise<Document | null> {
  return fetch(`/api/projects/${esc(projectId)}/documents/${esc(slug)}`)
    .then(res => {
      if (res.status === 404) return null;
      if (!res.ok) throw new Error(`GET document: ${res.status}`);
      return res.json();
    });
}

export interface SaveDocumentRequest {
  slug: string;
  title: string;
  content: string;
  doc_type?: DocType;
  tags?: string[] | null;
}

export function saveDocument(projectId: string, doc: SaveDocumentRequest): Promise<Document> {
  return post(`/api/projects/${esc(projectId)}/documents`, doc);
}

export function searchDocuments(query: string, projectId?: string): Promise<DocumentSearchResult[]> {
  if (projectId) {
    return get(`/api/projects/${esc(projectId)}/documents/search?query=${esc(query)}`);
  }
  const q = buildQuery({ query, projectId });
  return get(`/api/documents/search${q}`);
}

export interface QueryLibrarianRequest {
  query: string;
  taskId?: number;
  includeGlobal?: boolean;
}

export function queryLibrarian(projectId: string, request: QueryLibrarianRequest): Promise<LibrarianResponse> {
  return post(`/api/projects/${esc(projectId)}/librarian/query`, {
    query: request.query,
    task_id: request.taskId,
    include_global: request.includeGlobal ?? true,
  });
}

// Agents

export function listActiveAgents(projectId?: string): Promise<AgentSession[]> {
  const q = buildQuery({ projectId });
  return get(`/api/agents/active${q}`);
}

// Attention

export interface ListAttentionOpts {
  projectId?: string;
  taskId?: number;
  kind?: string;
  severity?: string;
  limit?: number;
}

export function listAttention(opts: ListAttentionOpts = {}): Promise<AttentionItem[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    kind: opts.kind,
    severity: opts.severity,
    limit: opts.limit,
  });
  return get(`/api/attention${q}`);
}

export function listProjectAttention(projectId: string, opts: Omit<ListAttentionOpts, 'projectId'> = {}): Promise<AttentionItem[]> {
  const q = buildQuery({
    taskId: opts.taskId,
    kind: opts.kind,
    severity: opts.severity,
    limit: opts.limit,
  });
  return get(`/api/projects/${esc(projectId)}/attention${q}`);
}

// Agent stream

export interface ListAgentStreamOpts {
  projectId?: string;
  taskId?: number;
  dispatchId?: number;
  streamKind?: string;
  eventType?: string;
  sender?: string;
  senderInstanceId?: string;
  recipientAgent?: string;
  recipientRole?: string;
  recipientInstanceId?: string;
  metadataRunId?: string;
  limit?: number;
}

export function listAgentStream(opts: ListAgentStreamOpts = {}): Promise<AgentStreamEntry[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    dispatchId: opts.dispatchId,
    streamKind: opts.streamKind,
    eventType: opts.eventType,
    sender: opts.sender,
    senderInstanceId: opts.senderInstanceId,
    recipientAgent: opts.recipientAgent,
    recipientRole: opts.recipientRole,
    recipientInstanceId: opts.recipientInstanceId,
    metadataRunId: opts.metadataRunId,
    limit: opts.limit,
  });
  return get(`/api/agent-stream${q}`);
}

export interface ListSubagentRunsOpts {
  projectId?: string;
  taskId?: number;
  state?: string;
  limit?: number;
}

export function listSubagentRuns(opts: ListSubagentRunsOpts = {}): Promise<SubagentRunSummary[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
    state: opts.state,
    limit: opts.limit,
  });
  return get(`/api/subagent-runs${q}`);
}

export function subagentRunEventsUrl(opts: Omit<ListSubagentRunsOpts, 'state' | 'limit'> = {}): string {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
  });
  return `/api/subagent-runs/events${q}`;
}

export function getSubagentRun(runId: string, opts: Omit<ListSubagentRunsOpts, 'limit'> = {}): Promise<SubagentRunDetail> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
  });
  return get(`/api/subagent-runs/${esc(runId)}${q}`);
}

export type SubagentRunControlAction = 'abort' | 'rerun';

export interface ControlSubagentRunOpts extends Omit<ListSubagentRunsOpts, 'state' | 'limit'> {
  action: SubagentRunControlAction;
  requestedBy?: string;
  reason?: string;
}

export function controlSubagentRun(runId: string, opts: ControlSubagentRunOpts): Promise<AgentStreamEntry> {
  const q = buildQuery({
    projectId: opts.projectId,
    taskId: opts.taskId,
  });
  return post(`/api/subagent-runs/${esc(runId)}/control${q}`, {
    action: opts.action,
    requested_by: opts.requestedBy ?? 'web-ui',
    reason: opts.reason,
  });
}

// Legacy dispatch helpers.
// The default dashboard intentionally does not import these; keep them available
// for historical dispatch detail links or a future explicit legacy/debug view.

export interface ListDispatchesOpts {
  projectId?: string;
  targetAgent?: string;
  status?: string;
}

export function listDispatches(opts: ListDispatchesOpts = {}): Promise<DispatchEntry[]> {
  const q = buildQuery({
    projectId: opts.projectId,
    targetAgent: opts.targetAgent,
    status: opts.status,
  });
  return get(`/api/dispatch${q}`);
}

export function getDispatch(dispatchId: number): Promise<DispatchEntry> {
  return get(`/api/dispatch/${dispatchId}`);
}

export function approveDispatch(dispatchId: number, decidedBy: string): Promise<DispatchEntry> {
  return post(`/api/dispatch/${dispatchId}/approve`, { decided_by: decidedBy });
}

export function rejectDispatch(dispatchId: number, decidedBy: string): Promise<DispatchEntry> {
  return post(`/api/dispatch/${dispatchId}/reject`, { decided_by: decidedBy });
}
