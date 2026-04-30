using System.Text.Json.Serialization;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Terminal stream/control protocol DTOs — backend-neutral bridge contract
/// defined by #945. These DTOs are the typed bridge contract before concrete
/// terminal backends (#909/#911) are added.
///
/// Naming convention uses the dotted-name pattern (den.terminal.*) per R945-4.
/// Legacy den:// names are used only in compatibility shims, not here.
/// </summary>

// ── Create ────────────────────────────────────────────────────────────────

public sealed record TerminalCreateSessionRequest
{
    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    /// <summary>Only tmux is implemented in task #909; zellij remains future/optional.</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = OperatorSessionBackend.Tmux;
}

public sealed record TerminalCreateSessionResponse
{
    [JsonPropertyName("session")]
    public required TerminalSessionSummary Session { get; init; }
}

// ── Attach ────────────────────────────────────────────────────────────────

public sealed record TerminalAttachRequest
{
    [JsonPropertyName("terminal_protocol_version")]
    public string TerminalProtocolVersion { get; init; } = "1.0";

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Attach mode: terminal_stream, activity_only, or external_attach_info.
    /// Per R945-5:
    ///   terminal_stream      — full terminal output stream with input/resize control
    ///   activity_only        — structured activity summaries only, no raw stream
    ///   external_attach_info — return attach instructions/info without streaming
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "terminal_stream";

    [JsonPropertyName("viewport")]
    public TerminalViewport? Viewport { get; init; }

    [JsonPropertyName("replay")]
    public TerminalReplaySpec? Replay { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }
}

public sealed record TerminalAttachResponse
{
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("attached_at")]
    public string AttachedAt { get; init; } = string.Empty;

    [JsonPropertyName("start_cursor")]
    public string StartCursor { get; init; } = string.Empty;

    [JsonPropertyName("replay_available_from")]
    public string ReplayAvailableFrom { get; init; } = string.Empty;

    [JsonPropertyName("replay_gap")]
    public bool ReplayGap { get; init; }

    [JsonPropertyName("capabilities")]
    public TerminalAttachCapabilities Capabilities { get; init; } = new();

    /// <summary>Backend-reported viewport limits per R945-1.</summary>
    [JsonPropertyName("viewport_limits")]
    public TerminalViewportLimits? ViewportLimits { get; init; }

    [JsonPropertyName("limits")]
    public TerminalStreamLimits Limits { get; init; } = new();

    [JsonPropertyName("external_attach")]
    public TerminalExternalAttachInfo? ExternalAttach { get; init; }
}

public sealed record TerminalExternalAttachInfo
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    /// <summary>
    /// Operator-facing command text only. Renderers may display/copy this opaque string,
    /// but must not auto-execute it; a future attach button must use a typed app-core
    /// command instead of running this text through a shell.
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record TerminalAttachCapabilities
{
    [JsonPropertyName("can_send_input")]
    public bool CanSendInput { get; init; }

    [JsonPropertyName("can_resize")]
    public bool CanResize { get; init; }

    [JsonPropertyName("can_detach")]
    public bool CanDetach { get; init; }

    [JsonPropertyName("can_terminate")]
    public bool CanTerminate { get; init; }

    [JsonPropertyName("can_stream_terminal")]
    public bool CanStreamTerminal { get; init; }
}

/// <summary>
/// Backend-reported viewport limits per R945-1.
/// Instead of assuming universal 1..500 cols/rows, backends report their actual bounds.
/// </summary>
public sealed record TerminalViewportLimits
{
    [JsonPropertyName("min_cols")]
    public int MinCols { get; init; } = 1;

    [JsonPropertyName("max_cols")]
    public int MaxCols { get; init; } = 500;

    [JsonPropertyName("min_rows")]
    public int MinRows { get; init; } = 1;

    [JsonPropertyName("max_rows")]
    public int MaxRows { get; init; } = 500;
}

/// <summary>
/// Stream flow-control limits. direct_pty tracks per-stream unacked bytes,
/// emits terminal backpressure events at AckAfterBytes or when unacked
/// output remains pending for AckAfterMillis, reports paused heartbeats while
/// ack-required, and clears pressure on ack_output. Snapshot-only
/// backends that cannot throttle (for example current tmux capture) must still
/// validate ack_output and document active queue enforcement as a live-backend
/// responsibility.
/// </summary>
public sealed record TerminalStreamLimits
{
    [JsonPropertyName("output_chunk_max_bytes")]
    public int OutputChunkMaxBytes { get; init; } = 65_536;

    [JsonPropertyName("input_chunk_max_bytes")]
    public int InputChunkMaxBytes { get; init; } = 16_384;

    [JsonPropertyName("session_replay_max_bytes")]
    public int SessionReplayMaxBytes { get; init; } = 1_048_576;

    [JsonPropertyName("subscriber_queue_max_bytes")]
    public int SubscriberQueueMaxBytes { get; init; } = 262_144;

    [JsonPropertyName("ack_after_bytes")]
    public int AckAfterBytes { get; init; } = 262_144;

    [JsonPropertyName("ack_after_millis")]
    public int AckAfterMillis { get; init; } = 500;

    [JsonPropertyName("heartbeat_interval_ms")]
    public int HeartbeatIntervalMs { get; init; } = 5000;
}

public sealed record TerminalViewport
{
    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }
}

public sealed record TerminalReplaySpec
{
    [JsonPropertyName("after_cursor")]
    public string? AfterCursor { get; init; }

    [JsonPropertyName("max_bytes")]
    public int MaxBytes { get; init; } = 262_144;

    [JsonPropertyName("max_chunks")]
    public int MaxChunks { get; init; } = 200;
}

// ── Detach ────────────────────────────────────────────────────────────────

public sealed record TerminalDetachRequest
{
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record TerminalDetachResponse
{
    [JsonPropertyName("detached")]
    public bool Detached { get; init; }

    [JsonPropertyName("backend_preserved")]
    public bool BackendPreserved { get; init; }
}

// ── Send Input ────────────────────────────────────────────────────────────

public sealed record TerminalSendInputRequest
{
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("input_id")]
    public string? InputId { get; init; }

    /// <summary>utf8 or base64.</summary>
    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = "utf8";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("byte_count")]
    public int ByteCount { get; init; }

    [JsonPropertyName("expected_lease_generation")]
    public long? ExpectedLeaseGeneration { get; init; }
}

public sealed record TerminalSendInputResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("input_id")]
    public string? InputId { get; init; }

    [JsonPropertyName("written_bytes")]
    public int WrittenBytes { get; init; }
}

// ── Resize ────────────────────────────────────────────────────────────────

public sealed record TerminalResizeRequest
{
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }
}

public sealed record TerminalResizeResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("cols")]
    public int Cols { get; init; }

    [JsonPropertyName("rows")]
    public int Rows { get; init; }
}

// ── Terminate ─────────────────────────────────────────────────────────────

public sealed record TerminalTerminateRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    /// <summary>interrupt, graceful, kill, backend_default.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "graceful";

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("expected_lease_generation")]
    public long? ExpectedLeaseGeneration { get; init; }

    [JsonPropertyName("requested_by")]
    public string? RequestedBy { get; init; }
}

public sealed record TerminalTerminateResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    [JsonPropertyName("terminal_event_id")]
    public string? TerminalEventId { get; init; }
}

// ── Reconnect ─────────────────────────────────────────────────────────────

public sealed record TerminalReconnectRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("previous_stream_id")]
    public string? PreviousStreamId { get; init; }

    [JsonPropertyName("last_seen_cursor")]
    public string? LastSeenCursor { get; init; }

    [JsonPropertyName("viewport")]
    public TerminalViewport? Viewport { get; init; }
}

// ── Ack Output ────────────────────────────────────────────────────────────

public sealed record TerminalAckOutputRequest
{
    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("ack_cursor")]
    public string? AckCursor { get; init; }

    [JsonPropertyName("received_bytes")]
    public int ReceivedBytes { get; init; }
}

public sealed record TerminalAckOutputResponse
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; init; }
}

// ── Read Activity ─────────────────────────────────────────────────────────

public sealed record TerminalReadActivityRequest
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    /// <summary>
    /// Cursor string from a previous read_activity response, or null to read
    /// from the start of the current bounded RecentActivity snapshot. New
    /// read-activity cursors use the act_v1_* content-identity format and
    /// remain valid across registry refreshes while the referenced activity
    /// item is still retained; legacy cur_N index cursors are accepted only
    /// for backward compatibility.
    /// </summary>
    [JsonPropertyName("after_cursor")]
    public string? AfterCursor { get; init; }

    [JsonPropertyName("limit")]
    public int Limit { get; init; } = 50;
}

public sealed record TerminalReadActivityResponse
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<TerminalActivityItem> Items { get; init; } = [];

    /// <summary>
    /// Cursor for the last returned activity item. Clients may pass it as
    /// after_cursor on the next poll even when truncated is false.
    /// </summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

public sealed record TerminalActivityItem
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; init; }
}

// ── List Sessions ─────────────────────────────────────────────────────────

public sealed record TerminalListSessionsRequest
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("backend")]
    public string? Backend { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record TerminalListSessionsResponse
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<TerminalSessionSummary> Sessions { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed record TerminalSessionSummary
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("current_command")]
    public string? CurrentCommand { get; init; }

    [JsonPropertyName("agent_identity")]
    public string? AgentIdentity { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("task_id")]
    public long? TaskId { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("source_instance_id")]
    public string? SourceInstanceId { get; init; }

    [JsonPropertyName("source_display_name")]
    public string? SourceDisplayName { get; init; }

    [JsonPropertyName("can_read_activity")]
    public bool CanReadActivity { get; init; }

    [JsonPropertyName("can_send_input")]
    public bool CanSendInput { get; init; }

    [JsonPropertyName("can_resize")]
    public bool CanResize { get; init; }

    [JsonPropertyName("can_terminate")]
    public bool CanTerminate { get; init; }

    [JsonPropertyName("can_attach")]
    public bool CanAttach { get; init; }

    [JsonPropertyName("can_detach")]
    public bool CanDetach { get; init; }

    [JsonPropertyName("can_reconnect")]
    public bool CanReconnect { get; init; }

    [JsonPropertyName("can_stream_terminal")]
    public bool CanStreamTerminal { get; init; }

    [JsonPropertyName("can_open_external_attach")]
    public bool CanOpenExternalAttach { get; init; }

    [JsonPropertyName("can_deliver_compiled_response")]
    public bool CanDeliverCompiledResponse { get; init; }

    [JsonPropertyName("persistence_kind")]
    public string? PersistenceKind { get; init; }

    [JsonPropertyName("ownership_kind")]
    public string? OwnershipKind { get; init; }

    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; init; }

    [JsonPropertyName("last_observed_at")]
    public string? LastObservedAt { get; init; }

    [JsonPropertyName("last_activity_at")]
    public string? LastActivityAt { get; init; }

    [JsonPropertyName("exited_at")]
    public string? ExitedAt { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

// ── Session status/capability changed event ───────────────────────────────

public sealed record TerminalOutputEvent
{
    [JsonPropertyName("terminal_protocol_version")]
    public string TerminalProtocolVersion { get; init; } = "1.0";

    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("terminal_sequence")]
    public long TerminalSequence { get; init; }

    [JsonPropertyName("stream_cursor")]
    public required string StreamCursor { get; init; }

    [JsonPropertyName("chunk_id")]
    public required string ChunkId { get; init; }

    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = "base64";

    [JsonPropertyName("data")]
    public required string Data { get; init; }

    [JsonPropertyName("byte_count")]
    public int ByteCount { get; init; }

    [JsonPropertyName("cols")]
    public int? Cols { get; init; }

    [JsonPropertyName("rows")]
    public int? Rows { get; init; }

    [JsonPropertyName("emitted_at")]
    public string EmittedAt { get; init; } = string.Empty;

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("redacted")]
    public bool Redacted { get; init; }
}

public sealed record TerminalSessionEvent
{
    [JsonPropertyName("terminal_protocol_version")]
    public string TerminalProtocolVersion { get; init; } = "1.0";

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("capabilities")]
    public TerminalAttachCapabilities? Capabilities { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("observed_at")]
    public string? ObservedAt { get; init; }
}

public sealed record TerminalReplayCompleteEvent
{
    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("from_cursor")]
    public string? FromCursor { get; init; }

    [JsonPropertyName("to_cursor")]
    public string? ToCursor { get; init; }

    [JsonPropertyName("replay_gap")]
    public bool ReplayGap { get; init; }

    [JsonPropertyName("dropped_bytes_before_start")]
    public long DroppedBytesBeforeStart { get; init; }
}

public sealed record TerminalExitEvent
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("exit_signal")]
    public int? ExitSignal { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "process_exited";

    [JsonPropertyName("exited_at")]
    public string ExitedAt { get; init; } = string.Empty;
}

public sealed record TerminalProtocolErrorEvent
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    [JsonPropertyName("details")]
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

public sealed record TerminalHeartbeatEvent
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("stream_cursor")]
    public string? StreamCursor { get; init; }

    [JsonPropertyName("backend_status")]
    public string BackendStatus { get; init; } = "running";

    [JsonPropertyName("last_activity_at")]
    public string? LastActivityAt { get; init; }

    [JsonPropertyName("queue_bytes")]
    public int QueueBytes { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }
}

public sealed record TerminalBackpressureEvent
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("stream_id")]
    public string? StreamId { get; init; }

    [JsonPropertyName("state")]
    public string State { get; init; } = "normal";

    [JsonPropertyName("queue_bytes")]
    public int QueueBytes { get; init; }

    [JsonPropertyName("dropped_bytes")]
    public long DroppedBytes { get; init; }

    [JsonPropertyName("next_action")]
    public string? NextAction { get; init; }
}

// ── Error DTO ─────────────────────────────────────────────────────────────

public sealed record TerminalErrorResult
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "unsupported_capability";

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("details")]
    public string? Details { get; init; }

    public static TerminalErrorResult Unsupported(string action, string detail)
    {
        return new TerminalErrorResult
        {
            Category = "unsupported_capability",
            Code = $"terminal.{action}.unsupported",
            Message = $"Terminal action '{action}' is not supported by this backend or session. {detail}",
            Details = detail,
        };
    }

    public static TerminalErrorResult NotFound(string sessionId)
    {
        return new TerminalErrorResult
        {
            Category = "not_found",
            Code = "terminal.session.not_found",
            Message = $"Session '{sessionId}' not found in local registry.",
        };
    }

    public static TerminalErrorResult LeaseConflict(string message)
    {
        return new TerminalErrorResult
        {
            Category = "conflict",
            Code = "terminal.lease.conflict",
            Message = message,
        };
    }

    public static TerminalErrorResult InvalidRequest(string message)
    {
        return new TerminalErrorResult
        {
            Category = "validation",
            Code = "terminal.request.invalid",
            Message = message,
        };
    }
}
