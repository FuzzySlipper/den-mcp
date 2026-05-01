using System.Text.Json.Serialization;
using System.Text.Json;

namespace DenMcp.Desktop.Sidecar;

// ── Request / response DTOs for the Messages bridge command ────────────

public sealed record MessagesSnapshotRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("thread_id")]
    public long? ThreadId { get; init; }

    [JsonPropertyName("since")]
    public string? Since { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 20;

    [JsonPropertyName("unread_for")]
    public string? UnreadFor { get; init; }
}

public sealed record MessagesSnapshot
{
    [JsonPropertyName("snapshot_id")]
    public required string SnapshotId { get; init; }

    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("thread_id")]
    public long? ThreadId { get; init; }

    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<MessagesMessageRow> Messages { get; init; } = [];

    [JsonPropertyName("thread_root")]
    public MessagesMessageRow? ThreadRoot { get; init; }

    [JsonPropertyName("unread_count")]
    public int UnreadCount { get; init; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }

    [JsonPropertyName("freshness")]
    public MessagesFreshness Freshness { get; init; } = new();
}

public sealed record MessagesMessageRow
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("sender")]
    public string Sender { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    [JsonPropertyName("metadata")]
    public JsonElement? Metadata { get; init; }

    [JsonPropertyName("metadata_type")]
    public string? MetadataType { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("thread_id")]
    public long? ThreadId { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("is_unread")]
    public bool IsUnread { get; init; }

    [JsonPropertyName("content_summary")]
    public string ContentSummary { get; init; } = string.Empty;
}

public sealed record MessagesFreshness
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
