export type TaskStatus = 'planned' | 'in_progress' | 'review' | 'blocked' | 'done' | 'cancelled';
export type DocType = 'prd' | 'spec' | 'adr' | 'convention' | 'reference' | 'note';
export type AgentSessionStatus = 'active' | 'inactive';
export type ReviewVerdict = 'changes_requested' | 'looks_good' | 'follow_up_needed' | 'blocked_by_dependency';
export type ReviewFindingCategory = 'blocking_bug' | 'acceptance_gap' | 'test_weakness' | 'follow_up_candidate';
export type ReviewFindingStatus = 'open' | 'claimed_fixed' | 'verified_fixed' | 'not_fixed' | 'superseded' | 'split_to_follow_up';
export type ReviewPacketKind = 'review_request' | 'rereview_request' | 'review_findings';

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
  open_review_findings: ReviewFinding[];
  resolved_review_findings: ReviewFinding[];
  review_workflow: ReviewWorkflowSummary;
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
  preferred_diff_base_ref: string | null;
  preferred_diff_base_commit: string | null;
  preferred_diff_head_ref: string | null;
  preferred_diff_head_commit: string | null;
  alternate_diff_base_ref: string | null;
  alternate_diff_base_commit: string | null;
  alternate_diff_head_ref: string | null;
  alternate_diff_head_commit: string | null;
  delta_base_commit: string | null;
  inherited_commit_count: number | null;
  task_local_commit_count: number | null;
  verdict: ReviewVerdict | null;
  verdict_by: string | null;
  verdict_notes: string | null;
  requested_at: string;
  verdict_at: string | null;
  preferred_diff: ReviewDiffRange;
  alternate_diff: ReviewDiffRange | null;
  delta_diff: ReviewDiffRange | null;
  branch_composition: ReviewBranchComposition;
  is_stacked_branch_review: boolean;
}

export interface ReviewDiffRange {
  base_ref: string;
  base_commit: string | null;
  head_ref: string;
  head_commit: string;
}

export interface ReviewBranchComposition {
  inherited_commit_count: number | null;
  task_local_commit_count: number | null;
  has_inherited_changes: boolean | null;
  has_task_local_changes: boolean | null;
}

export interface ReviewFinding {
  id: number;
  finding_key: string;
  task_id: number;
  review_round_id: number;
  review_round_number: number;
  finding_number: number;
  created_by: string;
  category: ReviewFindingCategory;
  summary: string;
  notes: string | null;
  file_references: string[] | null;
  test_commands: string[] | null;
  status: ReviewFindingStatus;
  status_updated_by: string | null;
  status_notes: string | null;
  status_updated_at: string | null;
  response_by: string | null;
  response_notes: string | null;
  response_at: string | null;
  follow_up_task_id: number | null;
  created_at: string;
  updated_at: string;
}

export interface ReviewWorkflowSummary {
  current_round: ReviewRound | null;
  current_verdict: ReviewVerdict | null;
  review_round_count: number;
  unresolved_finding_count: number;
  resolved_finding_count: number;
  addressed_finding_count: number;
  timeline: ReviewTimelineEntry[];
}

export interface ReviewTimelineEntry {
  review_round_id: number;
  review_round_number: number;
  branch: string;
  requested_by: string;
  requested_at: string;
  head_commit: string | null;
  last_reviewed_head_commit: string | null;
  commits_since_last_review: number | null;
  verdict: ReviewVerdict | null;
  verdict_by: string | null;
  verdict_at: string | null;
  total_finding_count: number;
  open_finding_count: number;
  addressed_finding_count: number;
  claimed_fixed_finding_count: number;
  resolved_finding_count: number;
}

export interface ReviewPacket {
  kind: ReviewPacketKind;
  title: string;
  content: string;
}

export interface ReviewPacketResult {
  review_round: ReviewRound | null;
  message: Message;
  packet: ReviewPacket;
  findings_addressed: string[];
  open_findings: string[];
  test_commands: string[];
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
