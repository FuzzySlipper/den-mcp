using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public static class AppAgentConstants
{
    public const string Actor = "desktop-app-agent";
    public const int DefaultMessageLimit = 10;
    public const int DefaultTerminalExcerptLimit = 50;
}

public sealed record AppAgentSelection
{
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("current_route")]
    public string? CurrentRoute { get; init; }

    [JsonPropertyName("current_tab")]
    public string? CurrentTab { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("selected_file_path")]
    public string? SelectedFilePath { get; init; }

    [JsonPropertyName("selected_diff_range")]
    public string? SelectedDiffRange { get; init; }
}

public sealed record AppAgentBuildContextRequest
{
    [JsonPropertyName("selection")]
    public AppAgentSelection Selection { get; init; } = new();

    [JsonPropertyName("agent_run_id")]
    public string? AgentRunId { get; init; }

    [JsonPropertyName("parent_request_id")]
    public string? ParentRequestId { get; init; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }

    [JsonPropertyName("terminal_excerpts")]
    public IReadOnlyList<AppAgentTerminalExcerptRequest> TerminalExcerpts { get; init; } = [];

    [JsonPropertyName("message_limit")]
    public int MessageLimit { get; init; } = AppAgentConstants.DefaultMessageLimit;
}

public sealed record AppAgentBuildContextResponse
{
    [JsonPropertyName("context")]
    public required AppAgentContextPacket Context { get; init; }
}

public sealed record AppAgentContextPacket
{
    [JsonPropertyName("context_version")]
    public int ContextVersion { get; init; } = 1;

    [JsonPropertyName("selection")]
    public AppAgentSelection Selection { get; init; } = new();

    [JsonPropertyName("task_summary")]
    public AppAgentTaskSummary? TaskSummary { get; init; }

    [JsonPropertyName("git_snapshot")]
    public AppAgentGitSnapshot GitSnapshot { get; init; } = new();

    [JsonPropertyName("session_summaries")]
    public IReadOnlyList<AppAgentSessionSummary> SessionSummaries { get; init; } = [];

    [JsonPropertyName("command_summaries")]
    public IReadOnlyList<AppAgentCommandSummary> CommandSummaries { get; init; } = [];

    [JsonPropertyName("terminal_excerpts")]
    public IReadOnlyList<AppAgentTerminalExcerpt> TerminalExcerpts { get; init; } = [];

    [JsonPropertyName("collaboration_state")]
    public AppAgentCollaborationState CollaborationState { get; init; } = new();

    [JsonPropertyName("authority")]
    public AppAgentAuthorityHints Authority { get; init; } = new();

    [JsonPropertyName("audit")]
    public required AppAgentAuditCorrelation Audit { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("built_at")]
    public required string BuiltAt { get; init; }
}

public sealed record AppAgentTaskSummary
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<AppAgentTaskDependencySummary> Dependencies { get; init; } = [];

    [JsonPropertyName("recent_messages")]
    public IReadOnlyList<AppAgentDenMessageSummary> RecentMessages { get; init; } = [];

    [JsonPropertyName("open_review_findings")]
    public IReadOnlyList<AppAgentReviewFindingSummary> OpenReviewFindings { get; init; } = [];

    [JsonPropertyName("review_state")]
    public string ReviewState { get; init; } = "none";
}

public sealed record AppAgentTaskDependencySummary
{
    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record AppAgentReviewFindingSummary
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record AppAgentDenMessageSummary
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("sender")]
    public string Sender { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("metadata_type")]
    public string? MetadataType { get; init; }

    [JsonPropertyName("content_summary")]
    public string ContentSummary { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }
}

public sealed record AppAgentGitSnapshot
{
    [JsonPropertyName("snapshots")]
    public IReadOnlyList<LocalGitSnapshot> Snapshots { get; init; } = [];

    [JsonPropertyName("selected_snapshot")]
    public LocalGitSnapshot? SelectedSnapshot { get; init; }
}

public sealed record AppAgentSessionSummary
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("current_command")]
    public string? CurrentCommand { get; init; }

    [JsonPropertyName("capabilities")]
    public AppAgentSessionCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("last_activity_summary")]
    public string? LastActivitySummary { get; init; }
}

public sealed record AppAgentSessionCapabilities
{
    [JsonPropertyName("can_read_activity")]
    public bool CanReadActivity { get; init; }

    [JsonPropertyName("can_attach")]
    public bool CanAttach { get; init; }

    [JsonPropertyName("can_send_input")]
    public bool CanSendInput { get; init; }

    [JsonPropertyName("can_terminate")]
    public bool CanTerminate { get; init; }

    [JsonPropertyName("can_kill")]
    public bool CanKill { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record AppAgentCommandSummary
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("needs_target")]
    public bool NeedsTarget { get; init; }
}

public sealed record AppAgentTerminalExcerptRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("after_cursor")]
    public string? AfterCursor { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = AppAgentConstants.DefaultTerminalExcerptLimit;
}

public sealed record AppAgentTerminalExcerpt
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<TerminalActivityItem> Items { get; init; } = [];

    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "explicit_read_activity";

    [JsonPropertyName("raw_terminal_bytes_persisted")]
    public bool RawTerminalBytesPersisted { get; init; }
}

public sealed record AppAgentCollaborationState
{
    [JsonPropertyName("active_session_id")]
    public string? ActiveSessionId { get; init; }

    [JsonPropertyName("annotated_source_ref")]
    public string? AnnotatedSourceRef { get; init; }

    [JsonPropertyName("compiled_response_draft_ref")]
    public string? CompiledResponseDraftRef { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = "No active collaboration context selected.";
}

public sealed record AppAgentAuthorityHints
{
    [JsonPropertyName("allowed_tools")]
    public IReadOnlyList<AppAgentToolDefinition> AllowedTools { get; init; } = [];

    [JsonPropertyName("disabled_tools")]
    public IReadOnlyList<AppAgentDisabledTool> DisabledTools { get; init; } = [];

    [JsonPropertyName("cancel_available")]
    public bool CancelAvailable { get; init; } = true;

    [JsonPropertyName("stop_available")]
    public bool StopAvailable { get; init; }

    [JsonPropertyName("sandbox_scope")]
    public string SandboxScope { get; init; } = "trusted_desktop_app_core_v1";
}

public sealed record AppAgentAuditCorrelation
{
    [JsonPropertyName("agent_run_id")]
    public required string AgentRunId { get; init; }

    [JsonPropertyName("operator_session_id")]
    public string? OperatorSessionId { get; init; }

    [JsonPropertyName("trace_id")]
    public required string TraceId { get; init; }

    [JsonPropertyName("parent_request_id")]
    public string? ParentRequestId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }
}

public sealed record AppAgentToolDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("display_name")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("disabled_reason")]
    public string? DisabledReason { get; init; }

    [JsonPropertyName("requires_explicit_target")]
    public bool RequiresExplicitTarget { get; init; }

    [JsonPropertyName("destructive")]
    public bool Destructive { get; init; }

    [JsonPropertyName("requires_confirmation")]
    public bool RequiresConfirmation { get; init; }

    [JsonPropertyName("cancellable")]
    public bool Cancellable { get; init; } = true;

    [JsonPropertyName("audit_event_type")]
    public required string AuditEventType { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record AppAgentDisabledTool
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record AppAgentListToolsRequest
{
    [JsonPropertyName("selection")]
    public AppAgentSelection Selection { get; init; } = new();
}

public sealed record AppAgentListToolsResponse
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<AppAgentToolDefinition> Tools { get; init; } = [];
}

public sealed record AppAgentInvokeToolRequest
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("input")]
    public JsonElement Input { get; init; }

    [JsonPropertyName("selection")]
    public AppAgentSelection Selection { get; init; } = new();

    [JsonPropertyName("agent_run_id")]
    public string? AgentRunId { get; init; }

    [JsonPropertyName("trace_id")]
    public string? TraceId { get; init; }
}

public sealed record AppAgentInvokeToolResponse
{
    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("result")]
    public JsonElement Result { get; init; }

    [JsonPropertyName("audit")]
    public required AppAgentAuditCorrelation Audit { get; init; }
}

public sealed record AppAgentCancelRequest
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record AppAgentCancelResponse
{
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public sealed record AppAgentRunStateEvent
{
    [JsonPropertyName("agent_run_id")]
    public required string AgentRunId { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("observed_at")]
    public required string ObservedAt { get; init; }
}

public sealed record AppAgentToolCallStateEvent
{
    [JsonPropertyName("tool_call_id")]
    public required string ToolCallId { get; init; }

    [JsonPropertyName("agent_run_id")]
    public required string AgentRunId { get; init; }

    [JsonPropertyName("tool_name")]
    public required string ToolName { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("completed_at")]
    public string? CompletedAt { get; init; }

    [JsonPropertyName("cancellable")]
    public bool Cancellable { get; init; }

    [JsonPropertyName("target_summary")]
    public string? TargetSummary { get; init; }
}

public sealed record DenTaskDetail
{
    [JsonPropertyName("task")]
    public DenTaskRecord Task { get; init; } = new();

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<DenTaskDependencyRecord> Dependencies { get; init; } = [];

    [JsonPropertyName("subtasks")]
    public IReadOnlyList<DenTaskRecord> Subtasks { get; init; } = [];

    [JsonPropertyName("recent_messages")]
    public IReadOnlyList<DenMessage> RecentMessages { get; init; } = [];

    [JsonPropertyName("review_rounds")]
    public IReadOnlyList<DenReviewRound> ReviewRounds { get; init; } = [];

    [JsonPropertyName("open_review_findings")]
    public IReadOnlyList<DenReviewFinding> OpenReviewFindings { get; init; } = [];

    [JsonPropertyName("resolved_review_findings")]
    public IReadOnlyList<DenReviewFinding> ResolvedReviewFindings { get; init; } = [];
}

public sealed record DenTaskRecord
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("assigned_to")]
    public string? AssignedTo { get; init; }

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("dependency_count")]
    public int DependencyCount { get; init; }

    [JsonPropertyName("subtask_count")]
    public int SubtaskCount { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record DenTaskDependencyRecord
{
    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record DenMessage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("thread_id")]
    public long? ThreadId { get; init; }

    [JsonPropertyName("sender")]
    public string Sender { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }
}

public sealed record DenReviewRound
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("round_number")]
    public int RoundNumber { get; init; }

    [JsonPropertyName("requested_by")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("base_branch")]
    public string? BaseBranch { get; init; }

    [JsonPropertyName("base_commit")]
    public string? BaseCommit { get; init; }

    [JsonPropertyName("head_commit")]
    public string? HeadCommit { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("requested_at")]
    public string? RequestedAt { get; init; }

    [JsonPropertyName("verdict_at")]
    public string? VerdictAt { get; init; }
}

public sealed record DenReviewFinding
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("review_round_id")]
    public long? ReviewRoundId { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record DenSubagentRunSummary
{
    [JsonPropertyName("run_id")]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    [JsonPropertyName("worktree_path")]
    public string? WorktreePath { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("head_commit")]
    public string? HeadCommit { get; init; }

    [JsonPropertyName("final_head_commit")]
    public string? FinalHeadCommit { get; init; }

    [JsonPropertyName("started_at")]
    public DateTime? StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public DateTime? EndedAt { get; init; }

    [JsonPropertyName("usage_summary")]
    public DenSubagentRunUsageSummary? UsageSummary { get; init; }

    [JsonPropertyName("operator_events")]
    public IReadOnlyList<DenSubagentRunOperatorEvent> OperatorEvents { get; init; } = [];

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; init; }
}

public sealed record DenSubagentRunUsageSummary
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("total_cost")]
    public double? TotalCost { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

public sealed record DenSubagentRunOperatorEvent
{
    [JsonPropertyName("event_name")]
    public string EventName { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTime? OccurredAt { get; init; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; init; }
}

public sealed record DenAgentStreamEntry
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("stream_kind")]
    public string? StreamKind { get; init; }

    [JsonPropertyName("event_type")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("sender")]
    public string? Sender { get; init; }

    [JsonPropertyName("recipient_agent")]
    public string? RecipientAgent { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; init; }
}
