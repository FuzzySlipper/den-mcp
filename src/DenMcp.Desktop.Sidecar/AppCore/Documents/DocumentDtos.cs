using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

// ── Bridge command DTOs for the Documents tab ──────────────────────────

public sealed record DocumentsListRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }
}

public sealed record DocumentsListResponse
{
    [JsonPropertyName("documents")]
    public IReadOnlyList<DocumentListItem> Documents { get; init; } = [];
}

public sealed record DocumentListItem
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("doc_type")]
    public required string DocType { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record DocumentGetRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }
}

public sealed record DocumentGetResponse
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("doc_type")]
    public required string DocType { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed record DocumentStoreRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("doc_type")]
    public string? DocType { get; init; }
}

public sealed record DocumentStoreResponse
{
    [JsonPropertyName("slug")]
    public required string Slug { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("created")]
    public bool Created { get; init; }
}
