using System.Text.Json;

namespace DenMcp.Core.Models;

public sealed class Message
{
    public int Id { get; set; }
    public required string ProjectId { get; set; }
    public int? TaskId { get; set; }
    public int? ThreadId { get; set; }
    public required string Sender { get; set; }
    public required string Content { get; set; }
    public MessageIntent? Intent { get; set; }
    public JsonElement? Metadata { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class Thread
{
    public required Message Root { get; set; }
    public required List<Message> Replies { get; set; }
}

public sealed class MessageFeedItem
{
    public required Message RootMessage { get; set; }
    public required Message LatestMessage { get; set; }
    public int ReplyCount { get; set; }
    public DateTime LatestActivityAt { get; set; }
}

public static class MessageIntentCompatibility
{
    private static readonly Dictionary<string, MessageIntent> LegacyTypeToIntent = new(StringComparer.Ordinal)
    {
        ["general"] = MessageIntent.General,
        ["comment"] = MessageIntent.General,
        ["note"] = MessageIntent.Note,
        ["status_update"] = MessageIntent.StatusUpdate,
        // Older merge-result messages already exist in live DBs under this type,
        // so keep backfill/read compatibility by treating them as status updates.
        ["merge_complete"] = MessageIntent.StatusUpdate,
        ["question"] = MessageIntent.Question,
        ["answer"] = MessageIntent.Answer,
        ["handoff"] = MessageIntent.Handoff,
        ["planning"] = MessageIntent.Handoff,
        ["planning_summary"] = MessageIntent.Handoff,
        ["review_request"] = MessageIntent.ReviewRequest,
        ["review_request_packet"] = MessageIntent.ReviewRequest,
        ["rereview_packet"] = MessageIntent.ReviewRequest,
        ["review_feedback"] = MessageIntent.ReviewFeedback,
        ["review_findings_packet"] = MessageIntent.ReviewFeedback,
        ["review_approval"] = MessageIntent.ReviewApproval,
        ["merge_request"] = MessageIntent.ReviewApproval,
        ["task_ready"] = MessageIntent.TaskReady,
        ["task_blocked"] = MessageIntent.TaskBlocked
    };

    public static MessageIntent ResolveWriteIntent(MessageIntent? explicitIntent, JsonElement? metadata)
    {
        var hasLegacyType = TryGetLegacyType(metadata, out var legacyType);
        var derivedIntent = hasLegacyType && legacyType is not null
            ? DeriveFromLegacyType(legacyType)
            : null;

        if (explicitIntent is MessageIntent intent)
        {
            if (derivedIntent is MessageIntent derived && derived != intent)
            {
                throw new InvalidOperationException(
                    $"Message intent '{intent.ToDbValue()}' conflicts with legacy metadata.type '{legacyType}'.");
            }

            return intent;
        }

        return derivedIntent ?? MessageIntent.General;
    }

    public static MessageIntent? DeriveFromMetadata(JsonElement? metadata)
    {
        if (!TryGetLegacyType(metadata, out var legacyType) || legacyType is null)
            return null;

        return DeriveFromLegacyType(legacyType);
    }

    public static MessageIntent? DeriveFromLegacyType(string? legacyType)
    {
        if (string.IsNullOrWhiteSpace(legacyType))
            return null;

        return LegacyTypeToIntent.TryGetValue(legacyType, out var intent) ? intent : null;
    }

    public static bool TryGetLegacyType(JsonElement? metadata, out string? legacyType)
    {
        legacyType = null;
        if (metadata is not JsonElement element ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        legacyType = typeElement.GetString();
        return !string.IsNullOrWhiteSpace(legacyType);
    }

    public static bool TryGetSubtype(JsonElement? metadata, string key, out string? subtype)
    {
        subtype = null;
        if (metadata is not JsonElement element ||
            element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(key, out var subtypeElement) ||
            subtypeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        subtype = subtypeElement.GetString();
        return !string.IsNullOrWhiteSpace(subtype);
    }

}
