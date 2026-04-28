using System.Text.Json;

namespace DenMcp.Core.Models;

public enum DesktopSnapshotState
{
    Ok,
    PathNotVisible,
    NotGitRepository,
    GitError,
    SourceOffline,
    Missing
}

public sealed class DesktopGitSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string RootPath { get; set; }
    public DesktopSnapshotState State { get; set; } = DesktopSnapshotState.Ok;
    public string? Branch { get; set; }
    public bool IsDetached { get; set; }
    public string? HeadSha { get; set; }
    public string? Upstream { get; set; }
    public int? Ahead { get; set; }
    public int? Behind { get; set; }
    public GitDirtyCounts DirtyCounts { get; set; } = new();
    public List<GitFileStatus> ChangedFiles { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool Truncated { get; set; }
    public required string SourceInstanceId { get; set; }
    public string? SourceDisplayName { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }
    public string FreshnessStatus => IsStale ? "stale" : "fresh";
}

public sealed class DesktopGitSnapshotListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? RootPath { get; set; }
    public DesktopSnapshotState? State { get; set; }
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);
    public int Limit { get; set; } = 50;
}

public sealed class DesktopGitSnapshotLatestResult
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? RootPath { get; set; }
    public string? SourceInstanceId { get; set; }
    public required DesktopSnapshotState State { get; set; }
    public required bool IsStale { get; set; }
    public required string FreshnessStatus { get; set; }
    public DesktopGitSnapshot? Snapshot { get; set; }
}

public sealed class DesktopDiffSnapshotLatestResult
{
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? RootPath { get; set; }
    public string? Path { get; set; }
    public string? SourceInstanceId { get; set; }
    public required DesktopSnapshotState State { get; set; }
    public required bool IsStale { get; set; }
    public required string FreshnessStatus { get; set; }
    public DesktopDiffSnapshot? Snapshot { get; set; }
}

public sealed class DesktopDiffSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string RootPath { get; set; }
    public string? Path { get; set; }
    public string? BaseRef { get; set; }
    public string? HeadRef { get; set; }
    public int MaxBytes { get; set; }
    public bool Staged { get; set; }
    public string Diff { get; set; } = string.Empty;
    public bool Truncated { get; set; }
    public bool Binary { get; set; }
    public List<string> Warnings { get; set; } = [];
    public required string SourceInstanceId { get; set; }
    public string? SourceDisplayName { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }
}

public sealed class DesktopSessionSnapshot
{
    public long Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public required string SessionId { get; set; }
    public string? ParentSessionId { get; set; }
    public string? AgentIdentity { get; set; }
    public string? Role { get; set; }
    public string? CurrentCommand { get; set; }
    public string? CurrentPhase { get; set; }
    public JsonElement? RecentActivity { get; set; }
    public JsonElement? ChildSessions { get; set; }
    public JsonElement? ControlCapabilities { get; set; }
    public List<string> Warnings { get; set; } = [];
    public required string SourceInstanceId { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsStale { get; set; }
    public int FreshnessSeconds { get; set; }
}

public sealed class DesktopSessionSnapshotListOptions
{
    public string? ProjectId { get; set; }
    public int? TaskId { get; set; }
    public string? WorkspaceId { get; set; }
    public string? SourceInstanceId { get; set; }
    public string? SessionId { get; set; }
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);
    public int Limit { get; set; } = 50;
}
