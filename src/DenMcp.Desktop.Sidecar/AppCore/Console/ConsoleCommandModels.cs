using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Descriptor for a registered safe built-in console command.
/// </summary>
public sealed record ConsoleCommandDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>
    /// Whether this command requires a project/task/workspace target to be meaningful.
    /// </summary>
    [JsonPropertyName("needsTarget")]
    public bool NeedsTarget { get; init; }
}

/// <summary>
/// Request to run a non-interactive console command through the sidecar/runtime.
/// Command semantics live here (C#), not in React.
/// </summary>
public sealed record ConsoleCommandRunRequest
{
    /// <summary>Command name from the registry (e.g. "refresh", "git-status").</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>Optional target project ID.</summary>
    [JsonPropertyName("projectId")]
    public string? ProjectId { get; init; }

    /// <summary>Optional target task ID.</summary>
    [JsonPropertyName("taskId")]
    public int? TaskId { get; init; }

    /// <summary>Optional target workspace ID.</summary>
    [JsonPropertyName("workspaceId")]
    public string? WorkspaceId { get; init; }

    /// <summary>Optional target session ID.</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

/// <summary>
/// A single structured output line produced during console command execution.
/// Every line has a level, timestamp, source, and message.
/// </summary>
public sealed record ConsoleCommandLine
{
    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// Response from running a console command.
/// Structured output lines carry the complete result; history is maintained on the client.
/// </summary>
public sealed record ConsoleCommandRunResponse
{
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("lines")]
    public required IReadOnlyList<ConsoleCommandLine> Lines { get; init; }
}

/// <summary>
/// Response listing all available console commands.
/// </summary>
public sealed record ConsoleCommandListResponse
{
    [JsonPropertyName("commands")]
    public required IReadOnlyList<ConsoleCommandDefinition> Commands { get; init; }
}
