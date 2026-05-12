using System.ComponentModel;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.CoreClient;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DenMcp.Server.Tools;

[McpServerToolType]
public sealed class MessageTools
{
    [McpServerTool(Name = "send_message"), Description("Send a message in a project. Can be project-level, attached to a task, or a reply in a thread.")]
    public static async Task<string> SendMessage(
        DenCoreClient coreClient,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, e.g. 'pi' or another manual agent identity.")] string sender,
        [Description("Message body (markdown).")] string content,
        [Description("Attach to a task by ID.")] int? task_id = null,
        [Description("Reply to an existing message (forms a thread).")] int? thread_id = null,
        [Description("Optional JSON metadata object or JSON-encoded string, e.g. {\"type\":\"review_request\"}.")] JsonElement? metadata = null,
        [Description("Optional canonical intent, e.g. review_feedback or handoff.")] string? intent = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var parsedIntent = ParseIntent(intent);
        Message created;
        try
        {
            created = await coreClient.SendMessageAsync(project_id, new
            {
                sender,
                content,
                task_id,
                thread_id,
                intent = parsedIntent,
                metadata = NormalizeMetadata(metadata)
            });
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
        return verbose
            ? JsonSerializer.Serialize(created, JsonOpts.Default)
            : ConciseResponse.SentMessage(created);
    }

    [McpServerTool(Name = "send_user_notification"), Description(
        "Send a user-facing notification message in a project. " +
        "Use this when you have noteworthy information for the user that should not require stopping the run or waiting for a final response. " +
        "Examples: server needs redeployment, a long-running task completed, a blocking issue needs user decision. " +
        "Notifications appear prominently in the Den Desktop Messages tab." +
        "Prefer this over send_message when the message is specifically for the user rather than general task tracking.")]
    public static async Task<string> SendUserNotification(
        DenCoreClient coreClient,
        [Description("Project ID.")] string project_id,
        [Description("Your agent identity, e.g. 'pi' or another manual agent identity.")] string sender,
        [Description("Notification body (markdown). Keep it concise and actionable.")] string content,
        [Description("Attach to a task by ID.")] int? task_id = null,
        [Description("Optional JSON metadata object or JSON-encoded string.")] JsonElement? metadata = null,
        [Description("Optional urgency hint: low, normal, or high. Defaults to normal.")] string? urgency = null,
        [Description("If true, return full JSON record instead of concise summary.")] bool verbose = false)
    {
        var normalizedUrgency = urgency?.ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            _ => "normal"
        };

        var mergedMetadata = MergeUrgencyIntoMetadata(NormalizeMetadata(metadata), normalizedUrgency, sender);

        Message created;
        try
        {
            created = await coreClient.SendMessageAsync(project_id, new
            {
                sender,
                content,
                task_id,
                intent = MessageIntent.Notification,
                metadata = mergedMetadata
            });
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
        return verbose
            ? JsonSerializer.Serialize(created, JsonOpts.Default)
            : ConciseResponse.SentMessage(created);
    }

    [McpServerTool(Name = "get_messages"), Description("Get messages in a project, with optional filters. Returns newest first.")]
    public static async Task<string> GetMessages(
        DenCoreClient coreClient,
        [Description("Project ID.")] string project_id,
        [Description("Filter to messages on a specific task.")] int? task_id = null,
        [Description("ISO datetime — only messages after this time.")] string? since = null,
        [Description("Agent identity — only unread messages for this agent.")] string? unread_for = null,
        [Description("Max messages to return. Default 20, max 100.")] int limit = 20,
        [Description("Optional canonical intent filter.")] string? intent = null)
    {
        try
        {
            var messages = await coreClient.GetMessagesAsync(project_id, task_id, since, unread_for, limit, intent);
            return JsonSerializer.Serialize(messages, JsonOpts.Default);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    [McpServerTool(Name = "get_thread"), Description("Get a complete message thread — the root message plus all replies in chronological order.")]
    public static async Task<string> GetThread(
        IMessageRepository repo,
        [Description("ID of the root message.")] int thread_id)
    {
        var thread = await repo.GetThreadAsync(thread_id);
        return JsonSerializer.Serialize(thread, JsonOpts.Default);
    }

    [McpServerTool(Name = "mark_read"), Description("Mark messages as read for an agent.")]
    public static async Task<string> MarkRead(
        DenCoreClient coreClient,
        [Description("Your agent identity.")] string agent,
        [Description("Comma-separated message IDs to mark as read.")] string message_ids)
    {
        var ids = message_ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse).ToArray();
        try
        {
            var result = await coreClient.MarkReadAsync(agent, ids);
            return JsonSerializer.Serialize(result, JsonOpts.Default);
        }
        catch (DenCoreException ex)
        {
            return DenCoreToolErrorFormatter.Format(ex);
        }
    }

    public static async Task<string> SendMessage(
        IMessageRepository repo,
        IDispatchDetectionService detection,
        ILogger<MessageTools> logger,
        string project_id,
        string sender,
        string content,
        int? task_id = null,
        int? thread_id = null,
        JsonElement? metadata = null,
        string? intent = null,
        bool verbose = false)
    {
        var parsedIntent = ParseIntent(intent);
        var created = await repo.CreateAsync(new Message
        {
            ProjectId = project_id,
            Sender = sender,
            Content = content,
            TaskId = task_id,
            ThreadId = thread_id,
            Intent = parsedIntent,
            Metadata = NormalizeMetadata(metadata)
        });
        try { await detection.OnMessageCreatedAsync(created); }
        catch (Exception ex) { logger.LogError(ex, "Dispatch detection failed for message {MessageId}", created.Id); }
        return verbose ? JsonSerializer.Serialize(created, JsonOpts.Default) : ConciseResponse.SentMessage(created);
    }

    public static async Task<string> SendUserNotification(
        IMessageRepository repo,
        IDispatchDetectionService detection,
        ILogger<MessageTools> logger,
        string project_id,
        string sender,
        string content,
        int? task_id = null,
        JsonElement? metadata = null,
        string? urgency = null,
        bool verbose = false)
    {
        var normalizedUrgency = urgency?.ToLowerInvariant() switch { "low" => "low", "high" => "high", _ => "normal" };
        var mergedMetadata = MergeUrgencyIntoMetadata(NormalizeMetadata(metadata), normalizedUrgency, sender);
        var created = await repo.CreateAsync(new Message
        {
            ProjectId = project_id,
            Sender = sender,
            Content = content,
            TaskId = task_id,
            Intent = MessageIntent.Notification,
            Metadata = mergedMetadata
        });
        try { await detection.OnMessageCreatedAsync(created); }
        catch (Exception ex) { logger.LogError(ex, "Dispatch detection failed for notification {MessageId}", created.Id); }
        return verbose ? JsonSerializer.Serialize(created, JsonOpts.Default) : ConciseResponse.SentMessage(created);
    }

    public static async Task<string> GetMessages(
        IMessageRepository repo,
        string project_id,
        int? task_id = null,
        string? since = null,
        string? unread_for = null,
        int limit = 20,
        string? intent = null)
    {
        DateTime? sinceDate = since is not null ? DateTime.Parse(since) : null;
        var parsedIntent = ParseIntent(intent);
        var messages = await repo.GetMessagesAsync(project_id, task_id, sinceDate, unread_for, limit, parsedIntent);
        return JsonSerializer.Serialize(messages, JsonOpts.Default);
    }

    public static async Task<string> MarkRead(IMessageRepository repo, string agent, string message_ids)
    {
        var ids = message_ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray();
        var count = await repo.MarkReadAsync(agent, ids);
        return JsonSerializer.Serialize(new { marked = count }, JsonOpts.Default);
    }

    private static MessageIntent? ParseIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return null;

        return EnumExtensions.ParseMessageIntent(intent);
    }

    private static JsonElement? NormalizeMetadata(JsonElement? metadata)
    {
        if (metadata is null)
            return null;

        if (metadata.Value.ValueKind == JsonValueKind.String)
        {
            var str = metadata.Value.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;
            return JsonSerializer.Deserialize<JsonElement>(str);
        }

        return metadata;
    }

    private static JsonElement? MergeUrgencyIntoMetadata(JsonElement? metadata, string urgency, string sender)
    {
        var obj = new System.Text.Json.Nodes.JsonObject();

        if (metadata.HasValue && metadata.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.Value.EnumerateObject())
            {
                obj[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }
        }

        obj["urgency"] = urgency;
        obj["source_sender"] = sender;

        return JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
    }
}
