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
    /// delegating to the terminal protocol. The actual send mechanism
    /// depends on the session's backend (tmux send-keys, PTY write, etc.)
    /// and is handled by the TerminalOperatorSessionService.
    /// </summary>
    private async Task DeliverToSessionInputAsync(
        OperatorSession session,
        string compiledText,
        CancellationToken cancellationToken)
    {
        // Publish a delivery-attempt event before sending.
        await _events.PublishAsync(
            DesktopSidecarProtocol.CollaborationDeliveryEvent,
            new CollaborationDeliveryEvent
            {
                SessionId = session.SessionId,
                Status = "attempting",
                CompiledTextLength = compiledText.Length,
                ObservedAt = FormatNow(),
            },
            cancellationToken).ConfigureAwait(false);

        // Delegate through the backend-neutral terminal service instead of
        // hardcoding tmux/direct-PTY details here.  The terminal service routes
        // to the current OperatorSession backend and re-validates backend
        // support before writing input.
        var deliveryPayload = $"[compiled-collaboration-response]\n{compiledText}\n[/compiled-collaboration-response]\n";
        var textBytes = Encoding.UTF8.GetBytes(deliveryPayload);
        var limits = new TerminalStreamLimits();
        if (textBytes.Length > limits.InputChunkMaxBytes)
        {
            throw new InvalidOperationException(
                $"Compiled response ({textBytes.Length} bytes) exceeds the {limits.InputChunkMaxBytes} byte per-command input limit.");
        }

        await _terminals.SendInputAsync(new TerminalSendInputRequest
        {
            SessionId = session.SessionId,
            InputId = $"collab_{Guid.NewGuid():N}",
            Encoding = "utf8",
            Data = deliveryPayload,
            ByteCount = textBytes.Length,
            ExpectedLeaseGeneration = session.LeaseGeneration,
        }, cancellationToken).ConfigureAwait(false);
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
/// Thrown when the delivery backend is not available for direct service-level delivery.
/// The bridge handler catches this and falls back to the terminal protocol path.
/// </summary>
public sealed class DeliveryBackendNotAvailableException : Exception
{
    public DeliveryBackendNotAvailableException(string message) : base(message) { }
    public DeliveryBackendNotAvailableException(string message, Exception inner) : base(message, inner) { }
}
