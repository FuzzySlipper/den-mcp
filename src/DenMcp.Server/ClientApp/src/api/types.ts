export type TaskStatus = 'planned' | 'in_progress' | 'review' | 'blocked' | 'done' | 'cancelled';
export type DocType = 'prd' | 'spec' | 'adr' | 'convention' | 'reference' | 'note';
export type AgentSessionStatus = 'active' | 'inactive';
export type ReviewVerdict = 'changes_requested' | 'looks_good' | 'follow_up_needed' | 'blocked_by_dependency';

export interface Project {
  id: string;
  name: string;
  root_path: string | null;
  description: string | null;
  created_at: string;
  updated_at: string;
}

export interface ProjectWithStats {
  project: Project;
  task_counts_by_status: Record<string, number>;
  unread_message_count: number;
}

export interface ProjectTask {
  id: number;
  project_id: string;
  title: string;
  description: string | null;
  status: TaskStatus;
  priority: number;
  assigned_to: string | null;
  parent_id: number | null;
  tags: string[] | null;
  created_at: string;
  updated_at: string;
}

export interface TaskSummary {
  id: number;
  project_id: string;
  title: string;
  status: TaskStatus;
  priority: number;
  assigned_to: string | null;
  parent_id: number | null;
  tags: string[] | null;
  dependency_count: number;
  subtask_count: number;
}

export interface TaskDependencyInfo {
  task_id: number;
  title: string;
  status: TaskStatus;
}

export interface TaskDetail {
  task: ProjectTask;
  dependencies: TaskDependencyInfo[];
  subtasks: TaskSummary[];
  recent_messages: Message[];
  review_rounds: ReviewRound[];
}

export interface ReviewRound {
  id: number;
  task_id: number;
  round_number: number;
  requested_by: string;
  branch: string;
  base_branch: string;
  base_commit: string;
  head_commit: string;
  last_reviewed_head_commit: string | null;
  commits_since_last_review: number | null;
  tests_run: string[] | null;
  notes: string | null;
  verdict: ReviewVerdict | null;
  verdict_by: string | null;
  verdict_notes: string | null;
  requested_at: string;
  verdict_at: string | null;
}

export interface Message {
  id: number;
  project_id: string;
  task_id: number | null;
  thread_id: number | null;
  sender: string;
  content: string;
  metadata: unknown | null;
  created_at: string;
}

export interface Thread {
  root: Message;
  replies: Message[];
}

export interface Document {
  id: number;
  project_id: string;
  slug: string;
  title: string;
  content: string;
  doc_type: DocType;
  tags: string[] | null;
  created_at: string;
  updated_at: string;
}

export interface DocumentSummary {
  id: number;
  project_id: string;
  slug: string;
  title: string;
  doc_type: DocType;
  tags: string[] | null;
  updated_at: string;
}

export interface DocumentSearchResult {
  project_id: string;
  slug: string;
  title: string;
  doc_type: DocType;
  snippet: string;
  rank: number;
}

export interface AgentSession {
  agent: string;
  project_id: string;
  session_id: string | null;
  status: AgentSessionStatus;
  checked_in_at: string;
  last_heartbeat: string;
  metadata: string | null;
}
