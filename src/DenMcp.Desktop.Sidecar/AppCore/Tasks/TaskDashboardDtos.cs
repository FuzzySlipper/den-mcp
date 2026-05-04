using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record TasksDashboardSnapshotRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("parent_task_id")]
    public long? ParentTaskId { get; init; }

    [JsonPropertyName("focused_task_id")]
    public long? FocusedTaskId { get; init; }

    [JsonPropertyName("include_done")]
    public bool IncludeDone { get; init; }
}

public sealed record TasksDashboardSnapshot
{
    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("parent_task_id")]
    public long? ParentTaskId { get; init; }

    [JsonPropertyName("focused_task_id")]
    public long? FocusedTaskId { get; init; }

    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("header")]
    public TasksDashboardHeader Header { get; init; } = new();

    [JsonPropertyName("tasks")]
    public IReadOnlyList<TasksDashboardTaskRow> Tasks { get; init; } = [];

    [JsonPropertyName("waves")]
    public IReadOnlyList<TasksDashboardWave> Waves { get; init; } = [];

    [JsonPropertyName("lanes")]
    public IReadOnlyList<TasksDashboardLane> Lanes { get; init; } = [];

    [JsonPropertyName("freshness")]
    public TasksDashboardFreshness Freshness { get; init; } = new();
}

public sealed record TasksDashboardHeader
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "unknown";

    [JsonPropertyName("task_count")]
    public int TaskCount { get; init; }

    [JsonPropertyName("done_count")]
    public int DoneCount { get; init; }

    [JsonPropertyName("active_count")]
    public int ActiveCount { get; init; }

    [JsonPropertyName("review_count")]
    public int ReviewCount { get; init; }

    [JsonPropertyName("blocked_count")]
    public int BlockedCount { get; init; }

    [JsonPropertyName("completion_percent")]
    public int CompletionPercent { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("total_cost")]
    public double? TotalCost { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("last_updated_at")]
    public string? LastUpdatedAt { get; init; }
}

public sealed record TasksDashboardTaskRow
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; init; }

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("computed_state")]
    public string ComputedState { get; init; } = "queued";

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("assigned_to")]
    public string? AssignedTo { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<TasksDashboardDependency> Dependencies { get; init; } = [];

    [JsonPropertyName("subtask_ids")]
    public IReadOnlyList<long> SubtaskIds { get; init; } = [];

    [JsonPropertyName("wave_index")]
    public int WaveIndex { get; init; }

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "planned";

    [JsonPropertyName("packets")]
    public IReadOnlyList<TasksDashboardPacketSummary> Packets { get; init; } = [];

    [JsonPropertyName("review")]
    public TasksDashboardReviewSummary Review { get; init; } = new();

    [JsonPropertyName("run_summary")]
    public TasksDashboardRunAggregate RunSummary { get; init; } = new();

    [JsonPropertyName("agent_lifecycle")]
    public TasksDashboardLifecycleSummary AgentLifecycle { get; init; } = new();

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("message_count")]
    public int MessageCount { get; init; }

    [JsonPropertyName("recent_messages")]
    public IReadOnlyList<TasksDashboardRecentMessage> RecentMessages { get; init; } = [];

    [JsonPropertyName("dependency_count")]
    public int DependencyCount { get; init; }

    [JsonPropertyName("subtask_count")]
    public int SubtaskCount { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("session_chips")]
    public IReadOnlyList<TasksDashboardSessionChip> SessionChips { get; init; } = [];

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record TasksDashboardDependency
{
    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; }
}

public sealed record TasksDashboardWave
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "queued";

    [JsonPropertyName("task_ids")]
    public IReadOnlyList<long> TaskIds { get; init; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

public sealed record TasksDashboardLane
{
    [JsonPropertyName("lane_key")]
    public required string LaneKey { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "unknown";

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("worktree_path")]
    public string? WorktreePath { get; init; }

    [JsonPropertyName("latest_run")]
    public TasksDashboardRunSummary? LatestRun { get; init; }

    [JsonPropertyName("latest_agent_event")]
    public TasksDashboardAgentStreamEvent? LatestAgentEvent { get; init; }

    [JsonPropertyName("session_chips")]
    public IReadOnlyList<TasksDashboardSessionChip> SessionChips { get; init; } = [];
}

public sealed record TasksDashboardPacketSummary
{
    [JsonPropertyName("packet_type")]
    public required string PacketType { get; init; }

    [JsonPropertyName("message_id")]
    public long MessageId { get; init; }

    [JsonPropertyName("sender")]
    public string Sender { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("summary")]
    public string Summary { get; init; } = string.Empty;

    [JsonPropertyName("stage")]
    public string Stage { get; init; } = "packet_seen";
}

public sealed record TasksDashboardReviewSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "none";

    [JsonPropertyName("current_round_id")]
    public long? CurrentRoundId { get; init; }

    [JsonPropertyName("round_count")]
    public int RoundCount { get; init; }

    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("head_commit")]
    public string? HeadCommit { get; init; }

    [JsonPropertyName("open_finding_count")]
    public int OpenFindingCount { get; init; }

    [JsonPropertyName("resolved_finding_count")]
    public int ResolvedFindingCount { get; init; }

    [JsonPropertyName("merge_eligible")]
    public bool? MergeEligible { get; init; }

    [JsonPropertyName("merge_eligibility_reason")]
    public string? MergeEligibilityReason { get; init; }
}

public sealed record TasksDashboardRunAggregate
{
    [JsonPropertyName("run_count")]
    public int RunCount { get; init; }

    [JsonPropertyName("active_run_count")]
    public int ActiveRunCount { get; init; }

    [JsonPropertyName("latest_run")]
    public TasksDashboardRunSummary? LatestRun { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    [JsonPropertyName("total_cost")]
    public double? TotalCost { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed record TasksDashboardRunSummary
{
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("purpose")]
    public string? Purpose { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("head_commit")]
    public string? HeadCommit { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("ended_at")]
    public string? EndedAt { get; init; }

    [JsonPropertyName("duration_ms")]
    public int? DurationMs { get; init; }

    [JsonPropertyName("usage")]
    public TasksDashboardUsageSummary? Usage { get; init; }

    [JsonPropertyName("lifecycle_events")]
    public IReadOnlyList<TasksDashboardRunLifecycleEvent> LifecycleEvents { get; init; } = [];
}

public sealed record TasksDashboardUsageSummary
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

public sealed record TasksDashboardRunLifecycleEvent
{
    [JsonPropertyName("event_name")]
    public required string EventName { get; init; }

    [JsonPropertyName("occurred_at")]
    public string? OccurredAt { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

public sealed record TasksDashboardLifecycleSummary
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "none";

    [JsonPropertyName("latest_event")]
    public TasksDashboardAgentStreamEvent? LatestEvent { get; init; }

    [JsonPropertyName("event_count")]
    public int EventCount { get; init; }
}

public sealed record TasksDashboardAgentStreamEvent
{
    [JsonPropertyName("entry_id")]
    public long EntryId { get; init; }

    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    [JsonPropertyName("sender")]
    public string? Sender { get; init; }

    [JsonPropertyName("recipient_agent")]
    public string? RecipientAgent { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

public sealed record TasksDashboardSessionChip
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string? SourceInstanceId { get; init; }

    [JsonPropertyName("capabilities")]
    public TasksDashboardSessionCapabilities Capabilities { get; init; } = new();

    [JsonPropertyName("last_activity_summary")]
    public string? LastActivitySummary { get; init; }
}

public sealed record TasksDashboardSessionCapabilities
{
    [JsonPropertyName("can_attach")]
    public bool CanAttach { get; init; }

    [JsonPropertyName("can_read_activity")]
    public bool CanReadActivity { get; init; }

    [JsonPropertyName("can_focus")]
    public bool CanFocus { get; init; }

    [JsonPropertyName("can_open_external_attach")]
    public bool CanOpenExternalAttach { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record TasksDashboardRecentMessage
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

public sealed record TasksDashboardFreshness
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = "den_http";

    [JsonPropertyName("generated_at")]
    public string? GeneratedAt { get; init; }

    [JsonPropertyName("is_partial")]
    public bool IsPartial { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("errors")]
    public IReadOnlyList<string> Errors { get; init; } = [];
}

// ── Task update bridge command (task #1152) ──────────────────────────────────

public sealed record TaskUpdateRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public required long TaskId { get; init; }

    [JsonPropertyName("agent")]
    public string Agent { get; init; } = "desktop";

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("priority")]
    public int? Priority { get; init; }

    [JsonPropertyName("assigned_to")]
    public string? AssignedTo { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }
}

public sealed record TaskUpdateResponse
{
    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("assigned_to")]
    public string? AssignedTo { get; init; }
}
