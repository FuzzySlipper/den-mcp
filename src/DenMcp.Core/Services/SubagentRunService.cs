using System.Text.Json;
using System.Text;
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
    private const int MaxArtifactTailBytes = 64 * 1024;
    private readonly IAgentStreamRepository _stream;
    private readonly IAgentRunRepository _runs;

    public SubagentRunService(IAgentStreamRepository stream, IAgentRunRepository runs)
    {
        _stream = stream;
        _runs = runs;
    }

    public async Task<List<SubagentRunSummary>> ListAsync(SubagentRunListOptions options)
    {
        var summariesByRunId = new Dictionary<string, SubagentRunSummary>(StringComparer.Ordinal);

        foreach (var record in await _runs.ListAsync(options))
        {
            var summary = await BuildSummaryAsync(record);
            if (summary is not null)
                summariesByRunId[summary.RunId] = summary;
        }

        foreach (var summary in await ListStreamSummariesAsync(options))
        {
            if (!summariesByRunId.TryGetValue(summary.RunId, out var existing) || summary.Latest.Id > existing.Latest.Id)
            {
                _ = await _runs.RebuildFromStreamAsync(summary.RunId);
                summariesByRunId[summary.RunId] = summary;
            }
        }

        return summariesByRunId.Values
            .Where(summary => MatchesStateFilter(summary.State, options.State))
            .OrderByDescending(summary => summary.Latest.CreatedAt)
            .ThenByDescending(summary => summary.Latest.Id)
            .Take(Math.Clamp(options.Limit, 1, 50))
            .ToList();
    }

    public async Task<SubagentRunDetail?> GetAsync(string runId, SubagentRunListOptions options)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return null;

        var events = await LoadStreamEventsAsync(runId, options);
        var record = events.Count > 0
            ? await _runs.RebuildFromStreamAsync(runId)
            : await _runs.GetAsync(runId, options);

        SubagentRunSummary? summary = null;
        if (record is not null && MatchesStateFilter(record.State, options.State))
            summary = await BuildSummaryAsync(record);

        summary ??= events.Count > 0 ? BuildSummary(runId, events) : null;
        if (summary is null || !MatchesStateFilter(summary.State, options.State))
            return null;

        var artifacts = await ReadArtifactsAsync(runId, summary);
        return new SubagentRunDetail
        {
            Summary = summary,
            Events = events,
            WorkEvents = BuildWorkEvents(events, artifacts),
            Artifacts = artifacts
        };
    }

    private async Task<List<SubagentRunSummary>> ListStreamSummariesAsync(SubagentRunListOptions options)
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
            .Where(summary => MatchesStateFilter(summary.State, options.State))
            .ToList();
    }

    private async Task<List<AgentStreamEntry>> LoadStreamEventsAsync(string runId, SubagentRunListOptions options)
    {
        var entries = await _stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = options.ProjectId,
            TaskId = options.TaskId,
            StreamKind = AgentStreamKind.Ops,
            MetadataRunId = runId,
            Limit = Math.Clamp(options.SourceLimit, 1, 200)
        });

        return entries
            .Where(entry => entry.EventType.StartsWith("subagent_", StringComparison.Ordinal))
            .OrderBy(entry => entry.CreatedAt)
            .ThenBy(entry => entry.Id)
            .ToList();
    }

    private async Task<SubagentRunSummary?> BuildSummaryAsync(AgentRunRecord record)
    {
        var latest = record.LatestStreamEntryId is null ? null : await _stream.GetByIdAsync(record.LatestStreamEntryId.Value);
        if (latest is null)
            return null;

        var started = record.StartedStreamEntryId is null ? null : await _stream.GetByIdAsync(record.StartedStreamEntryId.Value);
        return BuildSummary(record, latest, started);
    }

    private static SubagentRunSummary BuildSummary(AgentRunRecord record, AgentStreamEntry latest, AgentStreamEntry? started)
    {
        return new SubagentRunSummary
        {
            RunId = record.RunId,
            State = record.State,
            Schema = TextMetadata(latest, "schema") ?? TextMetadata(started, "schema"),
            SchemaVersion = IntMetadata(latest, "schema_version") ?? IntMetadata(started, "schema_version"),
            Latest = latest,
            Started = started,
            Role = record.Role ?? TextMetadata(latest, "role") ?? TextMetadata(started, "role"),
            TaskId = record.TaskId ?? latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = record.ProjectId ?? latest.ProjectId ?? started?.ProjectId,
            Backend = record.Backend ?? TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = record.Model ?? TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            OutputStatus = record.OutputStatus ?? TextMetadata(latest, "output_status"),
            TimeoutKind = record.TimeoutKind ?? TextMetadata(latest, "timeout_kind"),
            InfrastructureFailureReason = record.InfrastructureFailureReason ?? TextMetadata(latest, "infrastructure_failure_reason"),
            InfrastructureWarningReason = record.InfrastructureWarningReason ?? TextMetadata(latest, "infrastructure_warning_reason"),
            ExitCode = record.ExitCode ?? IntMetadata(latest, "exit_code"),
            Signal = record.Signal ?? TextMetadata(latest, "signal"),
            Pid = record.Pid ?? IntMetadata(latest, "pid"),
            StderrPreview = TextMetadata(latest, "stderr_preview"),
            FallbackModel = record.FallbackModel ?? TextMetadata(latest, "fallback_model"),
            FallbackFromModel = record.FallbackFromModel ?? TextMetadata(latest, "fallback_from_model"),
            FallbackFromExitCode = record.FallbackFromExitCode ?? IntMetadata(latest, "fallback_from_exit_code"),
            HeartbeatCount = record.HeartbeatCount,
            AssistantOutputCount = record.AssistantOutputCount,
            LastHeartbeatAt = record.LastHeartbeatAt,
            LastAssistantOutputAt = record.LastAssistantOutputAt,
            DurationMs = record.DurationMs ?? IntMetadata(latest, "duration_ms"),
            ArtifactDir = record.ArtifactDir ?? ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = record.EventCount
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
        var processStarted = sorted.LastOrDefault(entry => entry.EventType == "subagent_process_started");
        var fallbackStarted = sorted.LastOrDefault(entry => entry.EventType == "subagent_fallback_started");
        var heartbeats = sorted.Where(entry => entry.EventType == "subagent_heartbeat").ToList();
        var assistantOutputs = sorted.Where(entry => entry.EventType == "subagent_assistant_output").ToList();
        var lastHeartbeat = heartbeats.LastOrDefault();
        var lastAssistantOutput = assistantOutputs.LastOrDefault();

        return new SubagentRunSummary
        {
            RunId = runId,
            State = StateFromEvent(latest.EventType),
            Schema = TextMetadata(latest, "schema") ?? TextMetadata(started, "schema"),
            SchemaVersion = IntMetadata(latest, "schema_version") ?? IntMetadata(started, "schema_version"),
            Latest = latest,
            Started = started,
            Role = TextMetadata(latest, "role") ?? TextMetadata(started, "role"),
            TaskId = latest.TaskId ?? started?.TaskId ?? IntMetadata(latest, "task_id") ?? IntMetadata(started, "task_id"),
            ProjectId = latest.ProjectId ?? started?.ProjectId,
            Backend = TextMetadata(latest, "backend") ?? TextMetadata(started, "backend"),
            Model = TextMetadata(latest, "model") ?? TextMetadata(started, "model"),
            OutputStatus = TextMetadata(latest, "output_status"),
            TimeoutKind = TextMetadata(latest, "timeout_kind"),
            InfrastructureFailureReason = TextMetadata(latest, "infrastructure_failure_reason"),
            InfrastructureWarningReason = TextMetadata(latest, "infrastructure_warning_reason"),
            ExitCode = IntMetadata(latest, "exit_code"),
            Signal = TextMetadata(latest, "signal"),
            Pid = IntMetadata(latest, "pid") ?? EventIntMetadata(processStarted, "pid"),
            StderrPreview = TextMetadata(latest, "stderr_preview"),
            FallbackModel = TextMetadata(latest, "fallback_model") ?? TextMetadata(fallbackStarted, "fallback_model"),
            FallbackFromModel = TextMetadata(latest, "fallback_from_model") ?? TextMetadata(fallbackStarted, "failed_model"),
            FallbackFromExitCode = IntMetadata(latest, "fallback_from_exit_code") ?? IntMetadata(fallbackStarted, "failed_exit_code"),
            HeartbeatCount = heartbeats.Count,
            AssistantOutputCount = assistantOutputs.Count,
            LastHeartbeatAt = lastHeartbeat?.CreatedAt,
            LastAssistantOutputAt = lastAssistantOutput?.CreatedAt,
            DurationMs = IntMetadata(latest, "duration_ms"),
            ArtifactDir = ArtifactDir(latest) ?? ArtifactDir(started),
            EventCount = sorted.Count
        };
    }

    private static string StateFromEvent(string eventType) => eventType switch
    {
        "subagent_started" => "running",
        "subagent_process_started" => "running",
        "subagent_heartbeat" => "running",
        "subagent_assistant_output" => "running",
        "subagent_prompt_echo_detected" => "running",
        _ when eventType.StartsWith("subagent_work_", StringComparison.Ordinal) => "running",
        "subagent_fallback_started" => "retrying",
        "subagent_abort_requested" => "aborting",
        "subagent_rerun_requested" => "rerun_requested",
        "subagent_rerun_accepted" => "rerun_accepted",
        "subagent_rerun_unavailable" => "failed",
        "subagent_completed" => "complete",
        "subagent_timeout" => "timeout",
        "subagent_startup_timeout" => "timeout",
        "subagent_terminal_drain_timeout" => "timeout",
        "subagent_aborted" => "aborted",
        "subagent_abort" => "aborted",
        "subagent_failed" => "failed",
        "subagent_spawn_error" => "failed",
        _ => "unknown"
    };

    private static bool MatchesStateFilter(string state, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        return filter.ToLowerInvariant() switch
        {
            "active" => state is "running" or "retrying" or "aborting" or "rerun_requested",
            "problem" => state is "failed" or "timeout" or "aborted" or "unknown",
            "complete" => state == "complete",
            _ => state.Equals(filter, StringComparison.OrdinalIgnoreCase)
        };
    }

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

    private static int? EventIntMetadata(AgentStreamEntry? entry, string propertyName)
    {
        if (!TryGetEventMetadataProperty(entry, propertyName, out var property))
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

    private static List<JsonElement> BuildWorkEvents(
        IReadOnlyList<AgentStreamEntry> streamEvents,
        SubagentRunArtifactSnapshot? artifacts)
    {
        var sessionEvents = ParseSessionWorkEvents(artifacts?.SessionTail);
        if (sessionEvents.Count > 0)
            return sessionEvents;

        var artifactEvents = ParseWorkEvents(artifacts?.EventsTail);
        if (artifactEvents.Count > 0)
            return artifactEvents;

        return streamEvents
            .Select(StreamWorkEvent)
            .Where(workEvent => workEvent.HasValue)
            .Select(workEvent => workEvent!.Value)
            .TakeLast(80)
            .ToList();
    }

    private static List<JsonElement> ParseSessionWorkEvents(string? sessionJsonl)
    {
        if (string.IsNullOrWhiteSpace(sessionJsonl))
            return [];

        var events = new List<JsonElement>();
        foreach (var line in sessionJsonl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "...")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var workEvent = NormalizeSessionEntry(doc.RootElement);
                if (workEvent.HasValue)
                    events.Add(workEvent.Value);
            }
            catch (JsonException)
            {
                // The artifact snapshot may begin mid-line when tailing a large file.
            }
        }

        return events.TakeLast(80).ToList();
    }

    private static JsonElement? NormalizeSessionEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || !TryGetString(entry, "type", out var type))
            return null;

        return type switch
        {
            "session" => WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_session",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_session",
                ["session_id"] = StringOrNull(entry, "id"),
                ["cwd"] = StringOrNull(entry, "cwd"),
                ["version"] = NumberOrNull(entry, "version")
            }),
            "message" => NormalizeSessionMessageEntry(entry),
            "compaction" => WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_compaction",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_compaction",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["text_preview"] = Preview(StringOrNull(entry, "summary")),
                ["tokens_before"] = NumberOrNull(entry, "tokensBefore"),
                ["first_kept_entry_id"] = StringOrNull(entry, "firstKeptEntryId")
            }),
            "branch_summary" => WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_branch_summary",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_branch_summary",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["from_id"] = StringOrNull(entry, "fromId"),
                ["text_preview"] = Preview(StringOrNull(entry, "summary"))
            }),
            "custom" => WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_custom",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_custom",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["custom_type"] = StringOrNull(entry, "customType"),
                ["result_preview"] = Preview(JsonPreview(entry, "data"))
            }),
            "custom_message" => WorkEvent(new Dictionary<string, object?>
            {
                ["type"] = "subagent.work_custom_message",
                ["ts"] = TimestampMs(entry),
                ["source_type"] = "session_tree_custom_message",
                ["entry_id"] = StringOrNull(entry, "id"),
                ["parent_id"] = StringOrNull(entry, "parentId"),
                ["custom_type"] = StringOrNull(entry, "customType"),
                ["text_preview"] = Preview(ContentText(entry.TryGetProperty("content", out var content) ? content : default)),
                ["display"] = BoolOrNull(entry, "display")
            }),
            _ => null
        };
    }

    private static JsonElement? NormalizeSessionMessageEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetString(message, "role", out var role))
            return null;
        if (role == "user")
            return null; // Avoid surfacing raw prompts in the Den work feed.

        var common = new Dictionary<string, object?>
        {
            ["ts"] = TimestampMs(entry),
            ["entry_id"] = StringOrNull(entry, "id"),
            ["parent_id"] = StringOrNull(entry, "parentId"),
            ["role"] = role
        };

        switch (role)
        {
            case "assistant":
                common["type"] = "subagent.work_message_end";
                common["source_type"] = "session_tree_message";
                common["provider"] = StringOrNull(message, "provider");
                common["model"] = StringOrNull(message, "model");
                common["text_preview"] = Preview(ContentText(message.TryGetProperty("content", out var content) ? content : default, includeThinking: false));
                common["text_chars"] = ContentTextLength(message.TryGetProperty("content", out content) ? content : default, includeThinking: false);
                common["thinking_chars"] = ContentTextLength(message.TryGetProperty("content", out content) ? content : default, includeThinking: true, thinkingOnly: true);
                common["content_types"] = ContentTypes(message.TryGetProperty("content", out content) ? content : default);
                common["tool_calls"] = ToolCalls(message.TryGetProperty("content", out content) ? content : default);
                common["stop_reason"] = StringOrNull(message, "stopReason");
                return WorkEvent(common);
            case "toolResult":
                common["type"] = "subagent.work_tool_end";
                common["source_type"] = "session_tree_tool_result";
                common["tool_call_id"] = StringOrNull(message, "toolCallId");
                common["tool_name"] = StringOrNull(message, "toolName");
                common["result_preview"] = Preview(ContentText(message.TryGetProperty("content", out var toolContent) ? toolContent : default) ?? JsonPreview(message, "content"));
                common["is_error"] = BoolOrNull(message, "isError") ?? false;
                return WorkEvent(common);
            case "bashExecution":
                common["type"] = "subagent.work_bash_execution";
                common["source_type"] = "session_tree_bash_execution";
                common["tool_name"] = "bash";
                common["args_preview"] = Preview(StringOrNull(message, "command"));
                common["result_preview"] = Preview(StringOrNull(message, "output"));
                common["exit_code"] = NumberOrNull(message, "exitCode");
                common["cancelled"] = BoolOrNull(message, "cancelled");
                common["truncated"] = BoolOrNull(message, "truncated");
                common["is_error"] = NumberOrNull(message, "exitCode") is not null and not 0;
                return WorkEvent(common);
            case "custom":
                common["type"] = "subagent.work_custom_message";
                common["source_type"] = "session_tree_custom_message";
                common["custom_type"] = StringOrNull(message, "customType");
                common["text_preview"] = Preview(ContentText(message.TryGetProperty("content", out var customContent) ? customContent : default));
                common["display"] = BoolOrNull(message, "display");
                return WorkEvent(common);
            case "branchSummary":
                common["type"] = "subagent.work_branch_summary";
                common["source_type"] = "session_tree_branch_summary_message";
                common["from_id"] = StringOrNull(message, "fromId");
                common["text_preview"] = Preview(StringOrNull(message, "summary"));
                return WorkEvent(common);
            case "compactionSummary":
                common["type"] = "subagent.work_compaction";
                common["source_type"] = "session_tree_compaction_message";
                common["text_preview"] = Preview(StringOrNull(message, "summary"));
                common["tokens_before"] = NumberOrNull(message, "tokensBefore");
                return WorkEvent(common);
            default:
                return null;
        }
    }

    private static JsonElement WorkEvent(Dictionary<string, object?> payload)
    {
        var compact = payload
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        return JsonSerializer.SerializeToElement(compact);
    }

    private static List<Dictionary<string, object?>>? ToolCalls(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var calls = new List<Dictionary<string, object?>>();
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type) || type.GetString() != "toolCall")
                continue;
            calls.Add(new Dictionary<string, object?>
            {
                ["id"] = StringOrNull(part, "id"),
                ["name"] = StringOrNull(part, "name"),
                ["args_preview"] = Preview(JsonPreview(part, "arguments"))
            });
        }

        return calls.Count == 0 ? null : calls;
    }

    private static List<string>? ContentTypes(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var types = content.EnumerateArray()
            .Select(part => StringOrNull(part, "type"))
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.Ordinal)
            .Cast<string>()
            .ToList();
        return types.Count == 0 ? null : types;
    }

    private static string? ContentText(JsonElement content, bool includeThinking = true, bool thinkingOnly = false)
    {
        if (content.ValueKind == JsonValueKind.String)
            return thinkingOnly ? null : content.GetString();
        if (content.ValueKind != JsonValueKind.Array)
            return null;

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (!part.TryGetProperty("type", out var type))
                continue;
            var typeName = type.GetString();
            if (typeName == "text" && !thinkingOnly && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                parts.Add(text.GetString()!);
            if (typeName == "thinking" && includeThinking && part.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.String)
                parts.Add(thinking.GetString()!);
        }

        return parts.Count == 0 ? null : string.Join("", parts);
    }

    private static int? ContentTextLength(JsonElement content, bool includeThinking = true, bool thinkingOnly = false)
    {
        var text = ContentText(content, includeThinking, thinkingOnly);
        return text is null ? null : text.Length;
    }

    private static long? TimestampMs(JsonElement entry)
    {
        if (!TryGetString(entry, "timestamp", out var value) || !DateTimeOffset.TryParse(value, out var timestamp))
            return null;
        return timestamp.ToUnixTimeMilliseconds();
    }

    private static string? Preview(string? value, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maxChars ? normalized : string.Concat(normalized.AsSpan(0, maxChars), "…");
    }

    private static string? JsonPreview(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
            ? property.GetRawText()
            : null;
    }

    private static string? StringOrNull(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName, out var value) ? value : null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static long? NumberOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            return null;
        return property.TryGetInt64(out var value) ? value : null;
    }

    private static bool? BoolOrNull(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static List<JsonElement> ParseWorkEvents(string? eventsJsonl)
    {
        if (string.IsNullOrWhiteSpace(eventsJsonl))
            return [];

        var events = new List<JsonElement>();
        foreach (var line in eventsJsonl.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed == "...")
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (IsSubagentWorkEvent(doc.RootElement))
                    events.Add(doc.RootElement.Clone());
            }
            catch (JsonException)
            {
                // The artifact snapshot may begin mid-line when tailing a large file.
            }
        }

        return events.TakeLast(80).ToList();
    }

    private static JsonElement? StreamWorkEvent(AgentStreamEntry entry)
    {
        if (!entry.EventType.StartsWith("subagent_work_", StringComparison.Ordinal) ||
            !TryGetMetadataProperty(entry, "event", out var eventMetadata) ||
            !IsSubagentWorkEvent(eventMetadata))
        {
            return null;
        }

        return eventMetadata.Clone();
    }

    private static bool IsSubagentWorkEvent(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString()?.StartsWith("subagent.work_", StringComparison.Ordinal) == true;
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

    private static bool TryGetEventMetadataProperty(
        AgentStreamEntry? entry,
        string propertyName,
        out JsonElement property)
    {
        property = default;
        return TryGetMetadataProperty(entry, "event", out var eventMetadata) &&
            eventMetadata.ValueKind == JsonValueKind.Object &&
            eventMetadata.TryGetProperty(propertyName, out property);
    }

    private static async Task<SubagentRunArtifactSnapshot?> ReadArtifactsAsync(string runId, SubagentRunSummary summary)
    {
        if (string.IsNullOrWhiteSpace(summary.ArtifactDir))
            return null;

        var artifactDir = summary.ArtifactDir;
        if (!IsSafeArtifactDir(runId, artifactDir))
        {
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = false,
                ReadError = "Artifact directory did not match the expected den-subagent-runs/<run_id> shape."
            };
        }

        try
        {
            var sessionFilePath = FindSessionFile(artifactDir);
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = true,
                StatusJson = await ReadArtifactTailAsync(artifactDir, "status.json"),
                EventsTail = await ReadArtifactTailAsync(artifactDir, "events.jsonl"),
                StdoutTail = await ReadArtifactTailAsync(artifactDir, "stdout.jsonl"),
                StderrTail = await ReadArtifactTailAsync(artifactDir, "stderr.log"),
                SessionFilePath = sessionFilePath,
                SessionTail = sessionFilePath is null ? null : await ReadArtifactPathTailAsync(artifactDir, sessionFilePath)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SubagentRunArtifactSnapshot
            {
                Dir = artifactDir,
                Readable = false,
                ReadError = ex.Message
            };
        }
    }

    private static bool IsSafeArtifactDir(string runId, string artifactDir)
    {
        try
        {
            var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
            var parent = Directory.GetParent(fullDir);
            return Path.GetFileName(fullDir).Equals(runId, StringComparison.Ordinal) &&
                parent?.Name.Equals("den-subagent-runs", StringComparison.Ordinal) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindSessionFile(string artifactDir)
    {
        var sessionDir = Path.Combine(artifactDir, "sessions");
        if (!Directory.Exists(sessionDir))
            return null;

        var fullArtifactDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        var dirWithSeparator = $"{fullArtifactDir}{Path.DirectorySeparatorChar}";
        return Directory.EnumerateFiles(sessionDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Where(path => path.StartsWith(dirWithSeparator, StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault();
    }

    private static Task<string?> ReadArtifactTailAsync(string artifactDir, string fileName)
    {
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        return ReadArtifactPathTailAsync(fullDir, Path.Combine(fullDir, fileName));
    }

    private static async Task<string?> ReadArtifactPathTailAsync(string artifactDir, string filePath)
    {
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifactDir));
        var fullPath = Path.GetFullPath(filePath);
        var dirWithSeparator = $"{fullDir}{Path.DirectorySeparatorChar}";
        if (!fullPath.StartsWith(dirWithSeparator, StringComparison.Ordinal) || !File.Exists(fullPath))
            return null;

        await using var stream = File.OpenRead(fullPath);
        var bytesToRead = (int)Math.Min(stream.Length, MaxArtifactTailBytes);
        if (bytesToRead == 0)
            return string.Empty;

        stream.Seek(-bytesToRead, SeekOrigin.End);
        var buffer = new byte[bytesToRead];
        var read = await stream.ReadAsync(buffer);
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        return stream.Length > MaxArtifactTailBytes ? $"...\n{text}" : text;
    }
}
