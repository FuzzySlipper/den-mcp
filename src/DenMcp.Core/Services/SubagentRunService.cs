using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface ISubagentRunService
{
    Task<List<SubagentRunSummary>> ListAsync(SubagentRunListOptions options);
    Task<SubagentRunDetail?> GetAsync(string runId, SubagentRunListOptions options);
}

public sealed class SubagentRunService : ISubagentRunService
{
    private readonly IAgentStreamRepository _stream;

    public SubagentRunService(IAgentStreamRepository stream) => _stream = stream;

    public async Task<List<SubagentRunSummary>> ListAsync(SubagentRunListOptions options)
    {
        var entries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            StreamKind = AgentStreamKind.Ops,
            Limit = Math.Clamp(options.SourceLimit, 1, 200)
        });

        return entries
            .Where(entry => entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            .Select(entry => new { Entry = entry, RunId = TextMetadata(entry, "run_id") })
            .Where(item => !string.IsNullOrWhiteSpace(item.RunId))
            .GroupBy(item => item.RunId!)
            .Select(group => BuildSummary(group.Key, group.Select(item => item.Entry)))
            .OrderByDescending(summary => summary.Latest.CreatedAt)
            .ThenByDescending(summary => summary.Latest.Id)
            .Take(Math.Clamp(options.Limit, 1, 50))
            .ToList();
    }

    public async Task<SubagentRunDetail?> GetAsync(string runId, SubagentRunListOptions options)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var entries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            StreamKind = AgentStreamKind.Ops,
            MetadataRunId = runId,
            Limit = Math.Clamp(options.SourceLimit, 1, 200)
        });

        var events = entries
            .Where(entry => entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToList();

        if (events.Count == 0)
            return null;

        return new SubagentRunDetail
        {
            Summary = BuildSummary(runId, events),
            Events = events
        };
    }

    private static SubagentRunSummary BuildSummary(string runId, IEnumerable<AgentStreamEntry> entries)
    {
        var sorted = entries
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToList();
        var latest = sorted[^1];
        var started = sorted.FirstOrDefault(entry => entry.EventType == "subagent_started");

        return new SubagentRunSummary
        {
            RunId = runId,
            State = StateFromEvent(latest.EventType),
            Latest = latest,
            Started = started,
            Role = TextMetadata(latest, "role") ?? TextMetadata(started, "role"),
            TaskId = latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = latest.ProjectId ?? started?.ProjectId,
            Backend = TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            OutputStatus = TextMetadata(latest, "output_status"),
            TimeoutKind = TextMetadata(latest, "timeout_kind"),
            DurationMs = IntMetadata(latest, "duration_ms"),
            ArtifactDir = ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = sorted.Count
        };
    }

    private static string StateFromEvent(string eventType) => eventType switch
    {
        "subagent_started" => "running",
        "subagent_fallback_started" => "retrying",
        "subagent_completed" => "complete",
        "subagent_timeout" => "timeout",
        "subagent_aborted" => "aborted",
        "subagent_failed" => "failed",
        _ => "unknown"
    };

    private static string? TextMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetMetadataProperty(entry, propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;
    }

    private static int? IntMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetMetadataProperty(entry, propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? ArtifactDir(AgentStreamEntry? entry)
    {
        if (!TryGetMetadataProperty(entry, "artifacts", out var artifacts) ||
            artifacts.ValueKind != JsonValueKind.Object ||
            !artifacts.TryGetProperty("dir", out var dir) ||
            dir.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(dir.GetString()) ? null : dir.GetString();
    }

    private static bool TryGetMetadataProperty(
        AgentStreamEntry? entry,
        string propertyName,
        out JsonElement property)
    {
        property = default;
        return entry?.Metadata is { } metadata &&
            metadata.ValueKind == JsonValueKind.Object &&
            metadata.TryGetProperty(propertyName, out property);
    }
}
