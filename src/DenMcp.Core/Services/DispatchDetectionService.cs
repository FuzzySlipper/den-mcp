using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Services;

public interface IDispatchDetectionService
{
    /// <summary>
    /// Check if a newly created message should trigger a dispatch.
    /// </summary>
    Task OnMessageCreatedAsync(Message message);

    /// <summary>
    /// Check if a task status change should trigger a dispatch.
    /// </summary>
    Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy);
}

public sealed class DispatchDetectionService : IDispatchDetectionService
{
    private readonly IRoutingService _routing;
    private readonly IDispatchRepository _dispatches;
    private readonly IPromptGenerationService _prompts;
    private readonly ILogger<DispatchDetectionService> _logger;

    public DispatchDetectionService(
        IRoutingService routing,
        IDispatchRepository dispatches,
        IPromptGenerationService prompts,
        ILogger<DispatchDetectionService> logger)
    {
        _routing = routing;
        _dispatches = dispatches;
        _prompts = prompts;
        _logger = logger;
    }

    public async Task OnMessageCreatedAsync(Message message)
    {
        // Extract structured metadata
        string? messageType = null;
        string? recipient = null;
        string? branch = null;

        if (message.Metadata is JsonElement meta)
        {
            if (meta.TryGetProperty("type", out var typeEl))
                messageType = typeEl.GetString();
            if (meta.TryGetProperty("recipient", out var recipientEl))
                recipient = recipientEl.GetString();
            if (meta.TryGetProperty("branch", out var branchEl))
                branch = branchEl.GetString();
        }

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = message.ProjectId,
            MessageId = message.Id,
            MessageType = messageType,
            Recipient = recipient,
            Sender = message.Sender,
            TaskId = message.TaskId,
            Branch = branch,
            MessageContent = message.Content
        };

        // If task-attached, enrich with task title
        if (message.TaskId.HasValue)
        {
            // We don't load the task here to avoid circular deps — the prompt service does that.
            // TaskTitle will be populated by the caller or left null (prompt generation handles it).
        }

        await TryCreateDispatchAsync(evt, DispatchTriggerType.Message, message.Id);
    }

    public async Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy)
    {
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = task.ProjectId,
            TaskId = task.Id,
            TaskTitle = task.Title,
            FromStatus = fromStatus,
            ToStatus = toStatus,
            Sender = changedBy
        };

        await TryCreateDispatchAsync(evt, DispatchTriggerType.TaskStatus, task.Id);
    }

    private async Task TryCreateDispatchAsync(DispatchEvent evt, DispatchTriggerType triggerType, int triggerId)
    {
        var configResult = await _routing.GetRoutingConfigAsync(evt.ProjectId);
        if (!configResult.IsValid)
        {
            _logger.LogWarning("Skipping dispatch detection for {ProjectId}: {Error}",
                evt.ProjectId, configResult.ValidationError);
            return;
        }

        var trigger = _routing.MatchTrigger(configResult.Config, evt);
        if (trigger is null)
            return; // No trigger matched — not a dispatchable event

        var targetAgent = _routing.ResolveAgent(configResult.Config, trigger, evt);
        if (targetAgent is null)
        {
            _logger.LogWarning("Trigger matched but target agent resolved to null for {ProjectId} event {EventKind}",
                evt.ProjectId, evt.EventKind);
            return;
        }

        // Don't dispatch to the same agent that caused the event
        if (string.Equals(targetAgent, evt.Sender, StringComparison.OrdinalIgnoreCase))
            return;

        // Generate prompt
        var promptResult = await _prompts.GenerateAsync(evt, trigger, configResult.Config);

        var expiryMinutes = configResult.Config.Defaults.ExpiryMinutes;
        var dedupKey = DispatchEntry.BuildDedupKey(triggerType, triggerId, targetAgent);

        var entry = new DispatchEntry
        {
            ProjectId = evt.ProjectId,
            TargetAgent = targetAgent,
            TriggerType = triggerType,
            TriggerId = triggerId,
            TaskId = evt.TaskId,
            Summary = promptResult.Summary,
            ContextPrompt = promptResult.ContextPrompt,
            DedupKey = dedupKey,
            ExpiresAt = DateTime.UtcNow.AddMinutes(expiryMinutes)
        };

        var (dispatch, created) = await _dispatches.CreateIfAbsentAsync(entry);
        if (created)
        {
            _logger.LogInformation("Created dispatch #{DispatchId}: {Summary}",
                dispatch.Id, dispatch.Summary);
        }
    }
}
