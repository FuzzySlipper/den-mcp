using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bridge DTO for delivering a compiled collaboration response.
/// Follows the Den-post-first + capability-gated delivery pattern
/// specified by task #915 authority model and task #920 acceptance criteria.
/// </summary>

// ── Request ───────────────────────────────────────────────────────────────

public sealed record CollaborationSendCompiledResponseRequest
{
    /// <summary>The collaboration session whose compiled response should be delivered.</summary>
    [JsonPropertyName("session_id")]
    public required long SessionId { get; init; }

    /// <summary>
    /// Optional explicit compiled response text. When null, the service
    /// loads segments and annotations from Den and compiles on the fly.
    /// </summary>
    [JsonPropertyName("compiled_text")]
    public string? CompiledText { get; init; }

    /// <summary>
    /// Optional explicit OperatorSession target for live delivery.
    /// When null, the service attempts to resolve the target from the
    /// collaboration session's desktop_operator_session_id or pi_session_id.
    /// </summary>
    [JsonPropertyName("target_session_id")]
    public string? TargetSessionId { get; init; }

    /// <summary>
    /// When true, the compiled response is posted to Den even if live
    /// delivery is not attempted or fails. Default: true (Den-post-first).
    /// </summary>
    [JsonPropertyName("post_to_den")]
    public bool PostToDen { get; init; } = true;

    /// <summary>Actor identity for audit records.</summary>
    [JsonPropertyName("requested_by")]
    public string? RequestedBy { get; init; }
}

// ── Response ──────────────────────────────────────────────────────────────

public sealed record CollaborationSendCompiledResponseResponse
{
    [JsonPropertyName("compiled_text")]
    public required string CompiledText { get; init; }

    [JsonPropertyName("den_post")]
    public CollaborationDenPostRecord DenPost { get; init; } = new();

    [JsonPropertyName("delivery")]
    public CollaborationDeliveryRecord Delivery { get; init; } = new() { Status = CollaborationDeliveryStatus.Skipped };

    [JsonPropertyName("session_id")]
    public long SessionId { get; init; }

    [JsonPropertyName("target_session_id")]
    public string? TargetSessionId { get; init; }
}

/// <summary>
/// Record of the Den-post step: the compiled response is always
/// saved to Den before any live delivery attempt.
/// </summary>
public sealed record CollaborationDenPostRecord
{
    [JsonPropertyName("posted")]
    public bool Posted { get; init; }

    [JsonPropertyName("draft_id")]
    public long? DraftId { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Record of the live delivery step: capability-gated through OperatorSession.
/// </summary>
public sealed record CollaborationDeliveryRecord
{
    /// <summary>Delivery outcome: delivered, no_live_session, session_stale, session_offline, capability_denied, failed, skipped.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("target_session_id")]
    public string? TargetSessionId { get; init; }

    [JsonPropertyName("target_session_status")]
    public string? TargetSessionStatus { get; init; }

    [JsonPropertyName("can_deliver")]
    public bool CanDeliver { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// Delivery status constants for structured failure states.
/// </summary>
public static class CollaborationDeliveryStatus
{
    /// <summary>Compiled response was successfully delivered to the live session's input.</summary>
    public const string Delivered = "delivered";

    /// <summary>No live OperatorSession was found for the delivery target.</summary>
    public const string NoLiveSession = "no_live_session";

    /// <summary>The target OperatorSession exists but is stale (not recently observed).</summary>
    public const string SessionStale = "session_stale";

    /// <summary>The target OperatorSession's source is offline.</summary>
    public const string SessionOffline = "session_offline";

    /// <summary>The target OperatorSession exists but does not have can_send_input capability.</summary>
    public const string CapabilityDenied = "capability_denied";

    /// <summary>Live delivery was explicitly skipped (post_to_den only or no target).</summary>
    public const string Skipped = "skipped";

    /// <summary>Live delivery was attempted but failed (backend error, transport failure).</summary>
    public const string Failed = "failed";
}
