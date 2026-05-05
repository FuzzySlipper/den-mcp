using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

public sealed record DenHealth
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("informational_version")]
    public string? InformationalVersion { get; init; }

    [JsonPropertyName("commit")]
    public string? Commit { get; init; }
}

public sealed record DenProject
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("root_path")]
    public string? RootPath { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record DenSpace
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "project";

    [JsonPropertyName("visibility")]
    public string Visibility { get; init; } = "normal";

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("root_path")]
    public string? RootPath { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record DenAgentWorkspace
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public long TaskId { get; init; }

    [JsonPropertyName("branch")]
    public string Branch { get; init; } = string.Empty;

    [JsonPropertyName("worktree_path")]
    public string WorktreePath { get; init; } = string.Empty;

    [JsonPropertyName("base_branch")]
    public string BaseBranch { get; init; } = string.Empty;

    [JsonPropertyName("base_commit")]
    public string? BaseCommit { get; init; }

    [JsonPropertyName("head_commit")]
    public string? HeadCommit { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("created_by_run_id")]
    public string? CreatedByRunId { get; init; }

    [JsonPropertyName("dev_server_url")]
    public string? DevServerUrl { get; init; }

    [JsonPropertyName("preview_url")]
    public string? PreviewUrl { get; init; }

    [JsonPropertyName("cleanup_policy")]
    public string? CleanupPolicy { get; init; }

    [JsonPropertyName("changed_file_summary")]
    public JsonElement? ChangedFileSummary { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record LatestDiffSnapshotRequest
{
    [JsonPropertyName("projectId")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("taskId")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("rootPath")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("sourceInstanceId")]
    public string SourceInstanceId { get; init; } = string.Empty;
}

[JsonConverter(typeof(DesktopSnapshotStateJsonConverter))]
public enum DesktopSnapshotState
{
    Ok,
    PathNotVisible,
    NotGitRepository,
    GitError,
    SourceOffline,
    Missing,
}

public sealed class DesktopSnapshotStateJsonConverter : JsonConverter<DesktopSnapshotState>
{
    public override DesktopSnapshotState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Desktop snapshot state must be a string.");
        }

        var value = reader.GetString();
        return value switch
        {
            "ok" => DesktopSnapshotState.Ok,
            "path_not_visible" => DesktopSnapshotState.PathNotVisible,
            "not_git_repository" => DesktopSnapshotState.NotGitRepository,
            "git_error" => DesktopSnapshotState.GitError,
            "source_offline" => DesktopSnapshotState.SourceOffline,
            "missing" => DesktopSnapshotState.Missing,
            _ => throw new JsonException($"Unknown desktop snapshot state: {value}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, DesktopSnapshotState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DesktopSnapshotState.Ok => "ok",
            DesktopSnapshotState.PathNotVisible => "path_not_visible",
            DesktopSnapshotState.NotGitRepository => "not_git_repository",
            DesktopSnapshotState.GitError => "git_error",
            DesktopSnapshotState.SourceOffline => "source_offline",
            DesktopSnapshotState.Missing => "missing",
            _ => throw new JsonException($"Unknown desktop snapshot state: {value}"),
        });
    }
}

public sealed record DesktopGitSnapshotRequest
{
    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("root_path")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public DesktopSnapshotState State { get; init; } = DesktopSnapshotState.Ok;

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("is_detached")]
    public bool IsDetached { get; init; }

    [JsonPropertyName("head_sha")]
    public string? HeadSha { get; init; }

    [JsonPropertyName("upstream")]
    public string? Upstream { get; init; }

    [JsonPropertyName("ahead")]
    public long? Ahead { get; init; }

    [JsonPropertyName("behind")]
    public long? Behind { get; init; }

    [JsonPropertyName("dirty_counts")]
    public GitDirtyCounts DirtyCounts { get; init; } = new();

    [JsonPropertyName("changed_files")]
    public IReadOnlyList<GitFileStatus> ChangedFiles { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("source_display_name")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; init; } = string.Empty;
}

public sealed record DesktopDiffSnapshotRequest
{
    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("root_path")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("base_ref")]
    public string? BaseRef { get; init; }

    [JsonPropertyName("head_ref")]
    public string? HeadRef { get; init; }

    [JsonPropertyName("max_bytes")]
    public long MaxBytes { get; init; }

    [JsonPropertyName("staged")]
    public bool Staged { get; init; }

    [JsonPropertyName("diff")]
    public string Diff { get; init; } = string.Empty;

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("binary")]
    public bool Binary { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("source_instance_id")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("source_display_name")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; init; } = string.Empty;
}

public sealed record DesktopSessionSnapshotRequest
{
    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("parent_session_id")]
    public string? ParentSessionId { get; init; }

    [JsonPropertyName("agent_identity")]
    public string? AgentIdentity { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("current_command")]
    public string? CurrentCommand { get; init; }

    [JsonPropertyName("current_phase")]
    public string? CurrentPhase { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }

    [JsonPropertyName("last_activity_at")]
    public string? LastActivityAt { get; init; }

    [JsonPropertyName("exited_at")]
    public string? ExitedAt { get; init; }

    [JsonPropertyName("exit_code")]
    public long? ExitCode { get; init; }

    [JsonPropertyName("source_display_name")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; init; }

    [JsonPropertyName("recent_activity")]
    public JsonElement RecentActivity { get; init; }

    [JsonPropertyName("child_sessions")]
    public JsonElement ChildSessions { get; init; }

    [JsonPropertyName("control_capabilities")]
    public JsonElement ControlCapabilities { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("source_instance_id")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; init; } = string.Empty;
}

public sealed record AppendDesktopSessionEventRequest
{
    [JsonPropertyName("task_id")]
    public int? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string? SourceInstanceId { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; init; }

    [JsonPropertyName("payload")]
    public string? Payload { get; init; }

    [JsonPropertyName("requested_by")]
    public string? RequestedBy { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("observed_at")]
    public DateTime? ObservedAt { get; init; }
}

public sealed record GitDirtyCounts
{
    [JsonPropertyName("total")]
    public long Total { get; init; }

    [JsonPropertyName("staged")]
    public long Staged { get; init; }

    [JsonPropertyName("unstaged")]
    public long Unstaged { get; init; }

    [JsonPropertyName("untracked")]
    public long Untracked { get; init; }

    [JsonPropertyName("modified")]
    public long Modified { get; init; }

    [JsonPropertyName("added")]
    public long Added { get; init; }

    [JsonPropertyName("deleted")]
    public long Deleted { get; init; }

    [JsonPropertyName("renamed")]
    public long Renamed { get; init; }
}

public sealed record GitFileStatus
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("old_path")]
    public string? OldPath { get; init; }

    [JsonPropertyName("index_status")]
    public string? IndexStatus { get; init; }

    [JsonPropertyName("worktree_status")]
    public string? WorktreeStatus { get; init; }

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("is_untracked")]
    public bool IsUntracked { get; init; }
}

public sealed record DesktopDiffSnapshotLatestResult
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("root_path")]
    public string? RootPath { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string? SourceInstanceId { get; init; }

    [JsonPropertyName("state")]
    public DesktopSnapshotState State { get; init; } = DesktopSnapshotState.Missing;

    [JsonPropertyName("is_stale")]
    public bool IsStale { get; init; }

    [JsonPropertyName("freshness_status")]
    public string FreshnessStatus { get; init; } = string.Empty;

    [JsonPropertyName("snapshot")]
    public DesktopDiffSnapshot? Snapshot { get; init; }
}

public sealed record DesktopDiffSnapshot
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public string ProjectId { get; init; } = string.Empty;

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("root_path")]
    public string RootPath { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("base_ref")]
    public string? BaseRef { get; init; }

    [JsonPropertyName("head_ref")]
    public string? HeadRef { get; init; }

    [JsonPropertyName("max_bytes")]
    public long MaxBytes { get; init; }

    [JsonPropertyName("staged")]
    public bool Staged { get; init; }

    [JsonPropertyName("diff")]
    public string Diff { get; init; } = string.Empty;

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("binary")]
    public bool Binary { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("source_instance_id")]
    public string SourceInstanceId { get; init; } = string.Empty;

    [JsonPropertyName("source_display_name")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("observed_at")]
    public string ObservedAt { get; init; } = string.Empty;

    [JsonPropertyName("received_at")]
    public string ReceivedAt { get; init; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; init; } = string.Empty;

    [JsonPropertyName("is_stale")]
    public bool IsStale { get; init; }

    [JsonPropertyName("freshness_seconds")]
    public int FreshnessSeconds { get; init; }
}

// ── Document API DTOs (task #1147) ────────────────────────────────────────

public sealed record DenDocumentSummary
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("doc_type")]
    public string? DocType { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record DenDocumentDetail
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("doc_type")]
    public string? DocType { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; init; }
}

public sealed record StoreDocumentApiRequest
{
    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("doc_type")]
    public string? DocType { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

// ── Collaboration API DTOs ────────────────────────────────────────────────

public sealed record CreateCollaborationDraftApiRequest
{
    [JsonPropertyName("turn_id")]
    public long? TurnId { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; init; }
}

public sealed record CollaborationDraftRecord
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("session_id")]
    public long SessionId { get; init; }

    [JsonPropertyName("turn_id")]
    public long? TurnId { get; init; }

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("revision")]
    public int Revision { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }
}

public sealed record DenTaskUpdateRequest
{
    [JsonPropertyName("agent")]
    public string Agent { get; init; } = string.Empty;

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

    [JsonPropertyName("parent_id")]
    public int? ParentId { get; init; }
}
