using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Den.Bridge.Protocol;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Delivers compiled collaboration responses following the Den-post-first
/// + capability-gated delivery pattern from task #915.
///
/// Flow:
/// 1. Load collaboration session from Den (segments + annotations).
/// 2. Compile response text if not already provided.
/// 3. Post the compiled response to Den as a draft (Den-post-first).
/// 4. Resolve the target OperatorSession for live delivery.
/// 5. Check OperatorSession capabilities (can_send_input).
/// 6. Deliver the compiled text to the session's input buffer.
/// 7. Record delivery result as a Den session event for auditability.
/// </summary>
public sealed class CollaborationResponseDeliveryService
{
    private readonly OperatorSessionRegistry _registry;
    private readonly DenHttpClient _den;
    private readonly OperatorRuntimeService _runtime;
    private readonly TerminalOperatorSessionService _terminals;
    private readonly IOperatorRuntimeEventSink _events;
    private readonly Func<DateTimeOffset> _now;

    public CollaborationResponseDeliveryService(
        OperatorSessionRegistry registry,
        DenHttpClient den,
        OperatorRuntimeService runtime,
        TerminalOperatorSessionService terminals,
        IOperatorRuntimeEventSink events,
        Func<DateTimeOffset>? now = null)
    {
        _registry = registry;
        _den = den;
        _runtime = runtime;
        _terminals = terminals;
        _events = events;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CollaborationSendCompiledResponseResponse> DeliverAsync(
        CollaborationSendCompiledResponseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var requestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "desktop-operator" : request.RequestedBy;

        // Step 1-2: Load collaboration session and compile if needed.
        var compiledText = request.CompiledText;
        long? denDraftId = null;
        string? projectId = null;
        string? denPostError = null;

        // Load session from Den to get projectId and compile if needed.
        CollaborationSessionData? sessionData = null;
        try
        {
            sessionData = await LoadSessionAsync(settings.DenBaseUrl, request.SessionId, cancellationToken).ConfigureAwait(false);
            if (sessionData is not null)
            {
                projectId = sessionData.ProjectId;
            }
        }
        catch (Exception ex) when (ex is DenHttpClientException or JsonException or TaskCanceledException)
        {
            // If we already have compiled text, we can still attempt delivery.
            // Otherwise, this is a failure.
            if (string.IsNullOrWhiteSpace(compiledText))
            {
                return BuildErrorResponse(
                    request.SessionId,
                    request.TargetSessionId,
                    $"Unable to load collaboration session {request.SessionId}: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(compiledText) && sessionData is not null)
        {
            compiledText = CompileFromSessionData(sessionData);
        }

        if (string.IsNullOrWhiteSpace(compiledText))
        {
            return BuildErrorResponse(
                request.SessionId,
                request.TargetSessionId,
                "No annotations found to compile and no compiled_text provided.");
        }

        // Step 3: Post to Den (Den-post-first).
        if (request.PostToDen && !string.IsNullOrWhiteSpace(projectId))
        {
            try
            {
                var draft = await _den.CreateCollaborationDraftAsync(
                    settings.DenBaseUrl,
                    projectId,
                    request.SessionId,
                    new CreateCollaborationDraftApiRequest
                    {
                        TurnId = sessionData?.TurnId,
                        Content = compiledText,
                        CreatedBy = requestedBy,
                    },
                    cancellationToken).ConfigureAwait(false);
                denDraftId = draft.Id;
            }
            catch (Exception ex) when (ex is DenHttpClientException or JsonException or TaskCanceledException)
            {
                denPostError = ex.Message;
            }
        }

        var denPost = new CollaborationDenPostRecord
        {
            Posted = denDraftId is not null,
            DraftId = denDraftId,
            ProjectId = projectId,
            Error = denPostError,
        };

        // Step 4: Resolve target OperatorSession for live delivery.
        var targetSessionId = request.TargetSessionId
            ?? sessionData?.DesktopOperatorSessionId
            ?? sessionData?.PiSessionId;

        if (string.IsNullOrWhiteSpace(targetSessionId))
        {
            return new CollaborationSendCompiledResponseResponse
            {
                CompiledText = compiledText,
                DenPost = denPost,
                Delivery = new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.NoLiveSession,
                    Reason = "No target session ID specified and collaboration session has no associated operator session.",
                },
                SessionId = request.SessionId,
            };
        }

        // Step 5-6: Check session capabilities and deliver.
        var session = _registry.Get(targetSessionId);
        if (session is null)
        {
            return new CollaborationSendCompiledResponseResponse
            {
                CompiledText = compiledText,
                DenPost = denPost,
                Delivery = new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.NoLiveSession,
                    TargetSessionId = targetSessionId,
                    Reason = $"OperatorSession '{targetSessionId}' not found in local registry.",
                },
                SessionId = request.SessionId,
                TargetSessionId = targetSessionId,
            };
        }

        // Check for stale/offline states.
        if (session.Status is OperatorSessionStatus.Stale)
        {
            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.SessionStale,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = false,
                    Reason = "Target session is stale; it was not found during the last rediscovery.",
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }

        if (session.Status is OperatorSessionStatus.SourceOffline)
        {
            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.SessionOffline,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = false,
                    Reason = "Target session's source instance is offline.",
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }

        // Check compiled-response authority plus terminal input capability.
        // Renderer button visibility is not authority; app-core re-checks the
        // live OperatorSession capability state at execution time.
        if (!session.Capabilities.CanDeliverCompiledResponse || !session.Capabilities.CanSendInput)
        {
            var reason = session.Capabilities.Reason
                ?? (!session.Capabilities.CanDeliverCompiledResponse
                    ? "Session does not have can_deliver_compiled_response capability."
                    : "Session does not have can_send_input capability.");
            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.CapabilityDenied,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = false,
                    Reason = reason,
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }

        // Attempt live delivery via terminal input.
        try
        {
            await DeliverToSessionInputAsync(session, compiledText, cancellationToken).ConfigureAwait(false);

            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.Delivered,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = true,
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }
        catch (DeliveryTooLargeException ex)
        {
            // Response exceeds safe threshold: Den save already succeeded, return draft-only fallback.
            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.DraftOnlyFallback,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = false,
                    Reason = ex.Message,
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RecordDeliveryResultAsync(
                request.SessionId, compiledText, denPost,
                new CollaborationDeliveryRecord
                {
                    Status = CollaborationDeliveryStatus.Failed,
                    TargetSessionId = targetSessionId,
                    TargetSessionStatus = session.Status,
                    CanDeliver = true,
                    Error = ex.Message,
                    Reason = $"Live delivery failed: {ex.Message}",
                },
                projectId, requestedBy, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deliver compiled text to a session's input. This is backend-agnostic:
    /// it uses the OperatorSession model and checks can_send_input before
    /// delegating to the terminal protocol.
    ///
    /// Size strategy:
    /// - If the framed payload fits within InputChunkMaxBytes, send as a single chunk.
    /// - If it exceeds InputChunkMaxBytes but is below DraftOnlyThresholdBytes,
    ///   split into multiple chunks each within InputChunkMaxBytes, each with
    ///   part-numbered delimiters.
    /// - If it exceeds DraftOnlyThresholdBytes, throw with a clear message so
    ///   the caller can return a draft-only-fallback result.
    /// </summary>
    private async Task DeliverToSessionInputAsync(
        OperatorSession session,
        string compiledText,
        CancellationToken cancellationToken)
    {
        var limits = new TerminalStreamLimits();
        var maxChunkBytes = limits.InputChunkMaxBytes;

        // Build the single-chunk framed payload.
        var singlePayload = $"{CollaborationDelimiterProtocol.OpenTag}\n{compiledText}\n{CollaborationDelimiterProtocol.CloseTag}\n";
        var singleBytes = Encoding.UTF8.GetBytes(singlePayload);

        if (singleBytes.Length <= maxChunkBytes)
        {
            // Single-chunk delivery: fits within terminal input limit.
            await PublishDeliveryAttemptEvent(session, compiledText.Length, "single_chunk", cancellationToken).ConfigureAwait(false);
            await SendInputAsync(session, singlePayload, singleBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Check if the response exceeds the draft-only safety threshold.
        if (singleBytes.Length > CollaborationDelimiterProtocol.DraftOnlyThresholdBytes)
        {
            throw new DeliveryTooLargeException(
                $"Compiled response ({singleBytes.Length} bytes) exceeds the {CollaborationDelimiterProtocol.DraftOnlyThresholdBytes / 1024} KiB safe delivery threshold. " +
                $"The response has been saved to Den as a draft. Please send it manually or through a different channel.");
        }

        // Chunked delivery: split the compiled text into chunks that fit within the input limit.
        var chunks = BuildDeliveryChunks(compiledText, maxChunkBytes);
        await PublishDeliveryAttemptEvent(session, compiledText.Length, $"chunked:{chunks.Count}_parts", cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < chunks.Count; i++)
        {
            await SendInputAsync(session, chunks[i].Payload, chunks[i].Bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Split compiled text into framed chunks, each within maxChunkBytes.
    /// Each chunk is wrapped in part-numbered delimiters.
    ///
    /// UTF-8 boundary safety: when the raw byte split point lands inside
    /// a multi-byte character (continuation byte 0x80..0xBF), the boundary
    /// is backed up to the last leading byte so each chunk decodes cleanly
    /// without U+FFFD replacement characters.
    /// </summary>
    private static List<(string Payload, byte[] Bytes)> BuildDeliveryChunks(string compiledText, int maxChunkBytes)
    {
        var textBytes = Encoding.UTF8.GetBytes(compiledText);
        var chunks = new List<(string Payload, byte[] Bytes)>();

        // Conservative overhead per chunk for delimiters + part attribute.
        var delimiterOverhead = 120;
        var safeTextBytesPerChunk = maxChunkBytes - delimiterOverhead;
        if (safeTextBytesPerChunk <= 0)
        {
            throw new InvalidOperationException($"Input chunk limit ({maxChunkBytes} bytes) is too small for delivery framing.");
        }

        // Pre-pass: compute chunk boundaries on valid UTF-8 character edges
        // so each chunk decodes without replacement characters.
        var offsets = new List<int>();
        var currentOffset = 0;
        while (currentOffset < textBytes.Length)
        {
            var candidateEnd = Math.Min(currentOffset + safeTextBytesPerChunk, textBytes.Length);
            if (candidateEnd < textBytes.Length)
            {
                // Back up if the candidate end lands on a UTF-8 continuation byte
                // (0x80..0xBF). Continuation bytes cannot start a valid character.
                while (candidateEnd > currentOffset && IsUtf8ContinuationByte(textBytes[candidateEnd]))
                {
                    candidateEnd--;
                }
            }
            offsets.Add(candidateEnd);
            currentOffset = candidateEnd;
        }

        var totalParts = offsets.Count;

        var partIndex = 1;
        var previousOffset = 0;
        for (var i = 0; i < offsets.Count; i++)
        {
            var endOffset = offsets[i];
            var chunkSize = endOffset - previousOffset;

            var chunkBytes = new byte[chunkSize];
            Array.Copy(textBytes, previousOffset, chunkBytes, 0, chunkSize);
            var chunkTextDecoded = Encoding.UTF8.GetString(chunkBytes);

            var openTag = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                CollaborationDelimiterProtocol.ChunkOpenTagFormat,
                partIndex, totalParts);
            var payload = $"{openTag}\n{chunkTextDecoded}\n{CollaborationDelimiterProtocol.CloseTag}\n";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            chunks.Add((payload, payloadBytes));
            previousOffset = endOffset;
            partIndex++;
        }

        return chunks;
    }

    /// <summary>
    /// Returns true if the byte is a UTF-8 continuation byte (0x80..0xBF).
    /// Continuation bytes have the pattern 10xxxxxx.
    /// </summary>
    private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;

    private async Task SendInputAsync(
        OperatorSession session,
        string payload,
        byte[] payloadBytes,
        CancellationToken cancellationToken)
    {
        await _terminals.SendInputAsync(new TerminalSendInputRequest
        {
            SessionId = session.SessionId,
            InputId = $"collab_{Guid.NewGuid():N}",
            Encoding = "utf8",
            Data = payload,
            ByteCount = payloadBytes.Length,
            ExpectedLeaseGeneration = session.LeaseGeneration,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PublishDeliveryAttemptEvent(
        OperatorSession session,
        int compiledTextLength,
        string strategy,
        CancellationToken cancellationToken)
    {
        // Publish a delivery-attempt event before sending.
        await _events.PublishAsync(
            DesktopSidecarProtocol.CollaborationDeliveryEvent,
            new CollaborationDeliveryEvent
            {
                SessionId = session.SessionId,
                Status = "attempting",
                CompiledTextLength = compiledTextLength,
                ObservedAt = FormatNow(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<CollaborationSessionData?> LoadSessionAsync(
        string denBaseUrl,
        long sessionId,
        CancellationToken cancellationToken)
    {
        // Use the Den HTTP API to load the collaboration session.
        // This is a simplified loader that gets the session with its turns,
        // segments, and annotations.
        var response = await _den.GetCollaborationSessionAsync(
            denBaseUrl, sessionId, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static string CompileFromSessionData(CollaborationSessionData data)
    {
        if (data.Segments.Count == 0 || data.Annotations.Count == 0)
        {
            return string.Empty;
        }

        return CollaborationResponseCompiler.Compile(data.Segments, data.Annotations);
    }

    private async Task<CollaborationSendCompiledResponseResponse> RecordDeliveryResultAsync(
        long sessionId,
        string compiledText,
        CollaborationDenPostRecord denPost,
        CollaborationDeliveryRecord delivery,
        string? projectId,
        string requestedBy,
        CancellationToken cancellationToken)
    {
        // Record delivery result as a Den session event for auditability.
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            try
            {
                var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                await _den.PublishSessionEventAsync(
                    settings.DenBaseUrl,
                    projectId,
                    new AppendDesktopSessionEventRequest
                    {
                        TaskId = null,
                        SourceInstanceId = settings.SourceInstanceId,
                        SessionId = $"collab-session-{sessionId}",
                        EventType = $"collaboration.response_delivery_{delivery.Status}",
                        Payload = BridgeJson.Serialize(new
                        {
                            session_id = sessionId,
                            target_session_id = delivery.TargetSessionId,
                            delivery_status = delivery.Status,
                            den_draft_id = denPost.DraftId,
                            compiled_text_length = compiledText.Length,
                            can_deliver = delivery.CanDeliver,
                            reason = delivery.Reason,
                        }),
                        RequestedBy = requestedBy,
                        Reason = $"Collaboration response delivery: {delivery.Status}",
                        ObservedAt = _now().UtcDateTime,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Audit event publication is best-effort.
            }
        }

        // Publish bridge event for UI observability.
        await _events.PublishAsync(
            DesktopSidecarProtocol.CollaborationDeliveryEvent,
            new CollaborationDeliveryEvent
            {
                SessionId = delivery.TargetSessionId ?? $"collab-session-{sessionId}",
                Status = delivery.Status,
                CompiledTextLength = compiledText.Length,
                Reason = delivery.Reason,
                ObservedAt = FormatNow(),
            },
            cancellationToken).ConfigureAwait(false);

        return new CollaborationSendCompiledResponseResponse
        {
            CompiledText = compiledText,
            DenPost = denPost,
            Delivery = delivery,
            SessionId = sessionId,
            TargetSessionId = delivery.TargetSessionId,
        };
    }

    private CollaborationSendCompiledResponseResponse BuildErrorResponse(
        long sessionId,
        string? targetSessionId,
        string error)
    {
        return new CollaborationSendCompiledResponseResponse
        {
            CompiledText = string.Empty,
            DenPost = new CollaborationDenPostRecord { Error = error },
            Delivery = new CollaborationDeliveryRecord
            {
                Status = CollaborationDeliveryStatus.Failed,
                Error = error,
                Reason = error,
            },
            SessionId = sessionId,
            TargetSessionId = targetSessionId,
        };
    }

    private string FormatNow() => _now().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

/// <summary>
/// Bridge event for delivery lifecycle observability.
/// </summary>
public sealed record CollaborationDeliveryEvent
{
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("compiled_text_length")]
    public int CompiledTextLength { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("observed_at")]
    public required string ObservedAt { get; init; }
}

/// <summary>
/// Data loaded from a Den collaboration session for compilation.
/// </summary>
public sealed record CollaborationSessionData
{
    [JsonPropertyName("id")]
    public required long SessionId { get; init; }

    [JsonPropertyName("project_id")]
    public required string ProjectId { get; init; }

    [JsonPropertyName("desktop_operator_session_id")]
    public string? DesktopOperatorSessionId { get; init; }

    [JsonPropertyName("pi_session_id")]
    public string? PiSessionId { get; init; }

    public long? TurnId { get; init; }

    public IReadOnlyList<CollaborationSegment> Segments { get; init; } = [];
    public IReadOnlyList<CollaborationAnnotation> Annotations { get; init; } = [];
}

/// <summary>
/// Thrown when the delivery payload exceeds the safe delivery threshold.
/// The caller should return a draft-only-fallback result instead of failing.
/// </summary>
public sealed class DeliveryTooLargeException : Exception
{
    public DeliveryTooLargeException(string message) : base(message) { }
    public DeliveryTooLargeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when the delivery backend is not available for direct service-level delivery.
/// The bridge handler catches this and falls back to the terminal protocol path.
/// </summary>
public sealed class DeliveryBackendNotAvailableException : Exception
{
    public DeliveryBackendNotAvailableException(string message) : base(message) { }
    public DeliveryBackendNotAvailableException(string message, Exception inner) : base(message, inner) { }
}
