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
  AgentSession,
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

export function searchDocuments(query: string, projectId?: string): Promise<DocumentSearchResult[]> {
  if (projectId) {
    return get(`/api/projects/${esc(projectId)}/documents/search?query=${esc(query)}`);
  }
  const q = buildQuery({ query, projectId });
  return get(`/api/documents/search${q}`);
}

// Agents

export function listActiveAgents(projectId?: string): Promise<AgentSession[]> {
  const q = buildQuery({ projectId });
  return get(`/api/agents/active${q}`);
}
