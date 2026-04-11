using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface IRoutingService
{
    /// <summary>
    /// Load the routing config for a project. Returns in-memory fallback if no document exists.
    /// Never creates or modifies documents as a side effect.
    /// </summary>
    Task<RoutingConfig> GetRoutingConfigAsync(string projectId);

    /// <summary>
    /// Find the first trigger that matches a dispatch event, or null if none match.
    /// </summary>
    RoutingTrigger? MatchTrigger(RoutingConfig config, DispatchEvent evt);

    /// <summary>
    /// Resolve the target agent identity from a trigger's dispatch_to value.
    /// Handles role lookup, "{recipient}" interpolation, and literal agent names.
    /// </summary>
    string? ResolveAgent(RoutingConfig config, RoutingTrigger trigger, DispatchEvent evt);

    /// <summary>
    /// Interpolate a prompt template with event context values.
    /// </summary>
    string InterpolateTemplate(string template, DispatchEvent evt);
}

public sealed class RoutingService : IRoutingService
{
    private readonly IDocumentRepository _docs;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public RoutingService(IDocumentRepository docs) => _docs = docs;

    public async Task<RoutingConfig> GetRoutingConfigAsync(string projectId)
    {
        var doc = await _docs.GetAsync(projectId, "dispatch-routing");
        if (doc is null)
            return DefaultConfig;

        try
        {
            return JsonSerializer.Deserialize<RoutingConfig>(doc.Content, JsonOptions)
                   ?? DefaultConfig;
        }
        catch (JsonException)
        {
            // Malformed document — return fallback rather than crashing dispatch detection
            return DefaultConfig;
        }
    }

    public RoutingTrigger? MatchTrigger(RoutingConfig config, DispatchEvent evt)
    {
        foreach (var trigger in config.Triggers)
        {
            if (Matches(trigger, evt))
                return trigger;
        }
        return null;
    }

    public string? ResolveAgent(RoutingConfig config, RoutingTrigger trigger, DispatchEvent evt)
    {
        var target = trigger.DispatchTo;

        // {recipient} interpolation — use the message's explicit recipient
        if (target == "{recipient}")
            return evt.Recipient;

        // Role lookup — check if DispatchTo is a known role name
        if (config.Roles.TryGetValue(target, out var agentFromRole))
            return agentFromRole;

        // Literal agent identity
        return target;
    }

    public string InterpolateTemplate(string template, DispatchEvent evt)
    {
        return template
            .Replace("{project_id}", evt.ProjectId)
            .Replace("{task_id}", evt.TaskId?.ToString() ?? "")
            .Replace("{task_title}", evt.TaskTitle ?? "")
            .Replace("{branch}", evt.Branch ?? $"task/{evt.TaskId}-*")
            .Replace("{sender}", evt.Sender ?? "")
            .Replace("{message_type}", evt.MessageType ?? "")
            .Replace("{to_status}", evt.ToStatus ?? "")
            .Replace("{from_status}", evt.FromStatus ?? "");
    }

    private static bool Matches(RoutingTrigger trigger, DispatchEvent evt)
    {
        // Event kind must match
        if (!string.Equals(trigger.Event, evt.EventKind, StringComparison.OrdinalIgnoreCase))
            return false;

        // Task status transition predicates
        if (trigger.ToStatus is not null &&
            !string.Equals(trigger.ToStatus, evt.ToStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        if (trigger.FromStatus is not null &&
            !string.Equals(trigger.FromStatus, evt.FromStatus, StringComparison.OrdinalIgnoreCase))
            return false;

        // Message type predicate
        if (trigger.MessageType is not null &&
            !string.Equals(trigger.MessageType, evt.MessageType, StringComparison.OrdinalIgnoreCase))
            return false;

        // Recipient presence predicate
        if (trigger.HasRecipient == true && evt.Recipient is null)
            return false;

        return true;
    }

    /// <summary>
    /// Built-in fallback config covering the standard review/feedback cycle.
    /// Used when a project has no dispatch-routing document.
    /// </summary>
    internal static readonly RoutingConfig DefaultConfig = new()
    {
        Roles = new Dictionary<string, string>
        {
            ["implementer"] = "claude-code",
            ["reviewer"] = "codex"
        },
        Triggers =
        [
            // Task moved to review → dispatch reviewer
            new RoutingTrigger
            {
                Event = DispatchEvent.TaskStatusChanged,
                ToStatus = "review",
                DispatchTo = "reviewer",
                PromptTemplate = "Review task #{task_id} ({task_title}) on branch {branch}. Run `git diff main...HEAD` to see changes."
            },
            // Task moved from review back to planned → dispatch implementer for feedback
            new RoutingTrigger
            {
                Event = DispatchEvent.TaskStatusChanged,
                ToStatus = "planned",
                FromStatus = "review",
                DispatchTo = "implementer",
                PromptTemplate = "Task #{task_id} ({task_title}) has review feedback. Check messages for details and address the findings on branch {branch}."
            },
            // Message with explicit recipient → dispatch to that recipient
            new RoutingTrigger
            {
                Event = DispatchEvent.MessageReceived,
                HasRecipient = true,
                DispatchTo = "{recipient}",
                PromptTemplate = "You have a message on {project_id} from {sender}. Check your messages and respond."
            }
        ],
        Defaults = new RoutingDefaults
        {
            AutoApprove = false,
            ExpiryMinutes = 1440
        }
    };
}
