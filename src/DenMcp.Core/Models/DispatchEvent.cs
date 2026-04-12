namespace DenMcp.Core.Models;

/// <summary>
/// Normalized event that routing triggers match against.
/// Created by the detection layer from raw message/task events.
/// </summary>
public sealed class DispatchEvent
{
    /// <summary>"task_status_changed" or "message_received"</summary>
    public required string EventKind { get; set; }

    public required string ProjectId { get; set; }

    // --- Task status change fields ---

    /// <summary>The task status being transitioned TO (e.g. "review").</summary>
    public string? ToStatus { get; set; }

    /// <summary>The task status being transitioned FROM (e.g. "in_progress").</summary>
    public string? FromStatus { get; set; }

    public int? TaskId { get; set; }
    public string? TaskTitle { get; set; }

    // --- Message fields ---

    /// <summary>Message metadata "type" value (e.g. "review_request", "review_feedback").</summary>
    public string? MessageType { get; set; }

    /// <summary>Explicit recipient from message metadata, if present.</summary>
    public string? Recipient { get; set; }

    /// <summary>The agent/user who sent the message or changed the task status.</summary>
    public string? Sender { get; set; }

    /// <summary>The message ID that triggered this event (for message events).</summary>
    public int? MessageId { get; set; }

    /// <summary>The triggering message content, for inclusion in prompts.</summary>
    public string? MessageContent { get; set; }

    /// <summary>Branch name from message metadata, if present.</summary>
    public string? Branch { get; set; }

    public const string TaskStatusChanged = "task_status_changed";
    public const string MessageReceived = "message_received";
}
