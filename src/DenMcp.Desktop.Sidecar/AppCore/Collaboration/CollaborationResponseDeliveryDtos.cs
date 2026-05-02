using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bridge DTO for delivering a compiled collaboration response.
/// Follows the Den-post-first + capability-gated delivery pattern
/// specified by task #915 authority model and task #920 acceptance criteria.
/// </summary>

// ── Delimiter protocol constants ──────────────────────────────────────────
// The compiled-collaboration-response terminal delimiter protocol:
//
//   [compiled-collaboration-response]\n
//   <compiled markdown text>\n
//   [/compiled-collaboration-response]\n
//
// Delimiters are line-delimited to avoid collision with content. The opening
// and closing tags are constant strings that must not appear in the compiled
// text itself. If the compiled response contains a line that exactly matches
// a delimiter, the agent session must treat the content between the first
// opening and first closing delimiter as the payload.
//
// Size handling:
// - If the total framed payload (delimiters + content) fits within the
//   terminal input limit (16 KiB = InputChunkMaxBytes), it is delivered
//   as a single send-input operation.
// - If the payload exceeds InputChunkMaxBytes, it is split into chunks
//   each wrapped in its own delimiter pair with a part indicator:
//
//     [compiled-collaboration-response part="1/3"]\n
//     <chunk 1>\n
//     [/compiled-collaboration-response]\n
//
//   The receiving agent session reassembles by concatenating all parts
//   in order. The part attribute is 1-indexed.
// - If the response is too large for reliable delivery even with chunking
//   (above a safety threshold of 128 KiB), the delivery falls back to
//   draft-only: the compiled text is saved in Den and the delivery result
//   indicates the user should send it manually or via a different channel.
//
// Delimiter constants:
public static class CollaborationDelimiterProtocol
{
    /// <summary>Opening delimiter for a single-chunk delivery.</summary>
    public const string OpenTag = "[compiled-collaboration-response]";

    /// <summary>Closing delimiter for a single-chunk delivery.</summary>
    public const string CloseTag = "[/compiled-collaboration-response]";

    /// <summary>Opening delimiter template for a chunked delivery. Format: part index / total parts.</summary>
    public const string ChunkOpenTagFormat = "[compiled-collaboration-response part=\"{0}/{1}\"]";

    /// <summary>Maximum total payload size before falling back to draft-only (bytes).</summary>
    public const int DraftOnlyThresholdBytes = 128 * 1024;
}

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

    /// <summary>
    /// Response exceeds the safe delivery threshold and was saved to Den as draft only.
    /// The user should send the response manually or through a different channel.
    /// </summary>
    public const string DraftOnlyFallback = "draft_only_fallback";
}
