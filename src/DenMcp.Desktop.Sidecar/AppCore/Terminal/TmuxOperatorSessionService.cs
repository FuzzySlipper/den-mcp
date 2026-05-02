using System.Text;
using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public sealed class TmuxOperatorSessionService
{
    private const string LabelPrefix = "@den.";
    private static readonly TerminalViewportLimits TmuxViewportLimits = new() { MinCols = 1, MaxCols = 500, MinRows = 1, MaxRows = 500 };

    private readonly ITmuxCommandRunner _tmux;
    private readonly OperatorSessionRegistry _registry;
    private readonly IOperatorRuntimeEventSink _events;
    private readonly OperatorSettingsService _settingsService;
    private readonly DenHttpClient _den;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, OperatorSessionActivityBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _streams = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>
    /// Diagnostic count of actively tracked tmux streams. Internal so test assemblies
    /// can assert lifecycle behavior without reflecting over private fields.
    /// </summary>
    internal int TrackedStreamCount
    {
        get
        {
            lock (_lock)
            {
                return _streams.Count;
            }
        }
    }

    public TmuxOperatorSessionService(
        ITmuxCommandRunner tmux,
        OperatorSessionRegistry registry,
        IOperatorRuntimeEventSink events,
        OperatorSettingsService settingsService,
        DenHttpClient den,
        Func<DateTimeOffset>? now = null)
    {
        _tmux = tmux;
        _registry = registry;
        _events = events;
        _settingsService = settingsService;
        _den = den;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<OperatorSession> CreateAsync(TerminalCreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Backend, OperatorSessionBackend.Tmux, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsupported("create_session", "Only tmux-backed OperatorSessions are implemented by this backend.");
        }

        if (string.IsNullOrWhiteSpace(request.ProjectId))
        {
            throw Invalid("project_id is required for tmux-backed sessions so snapshots/events can be published to Den.");
        }

        var settings = _settingsService.Load();
        var identity = TmuxSessionNaming.Create(settings.SourceInstanceId, request.ProjectId, request.TaskId, request.WorkspaceId, request.Title);
        var args = new List<string> { "new-session", "-d", "-s", identity.SessionName };
        if (!string.IsNullOrWhiteSpace(request.Cwd))
        {
            args.Add("-c");
            args.Add(request.Cwd.Trim());
        }

        var result = await _tmux.RunAsync(args, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw BackendUnavailable("create_session", result.Stderr);
        }

        await SetMetadataAsync(identity.SessionName, settings, request, cancellationToken).ConfigureAwait(false);
        var session = BuildSession(identity, settings, request.ProjectId, request.TaskId, request.WorkspaceId, request.Title, request.Cwd, []);
        session = _registry.Register(session);
        await PublishSessionEventsAsync(session, "session.created", new { backend = OperatorSessionBackend.Tmux, persistence = "tmux" }, null, null, cancellationToken).ConfigureAwait(false);
        await PublishStatusEventsAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<IReadOnlyList<OperatorSession>> RediscoverAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.Load();
        var result = await _tmux.RunAsync([
            "list-sessions",
            "-F",
            "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_activity}\t#{@den.source_instance_id}\t#{@den.project_id}\t#{@den.task_id}\t#{@den.workspace_id}\t#{@den.title}\t#{@den.cwd}",
        ], cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            MarkTmuxSessionsStale("tmux is unavailable or returned no session list: " + TrimError(result.Stderr));
            return [];
        }

        var foundNames = new HashSet<string>(StringComparer.Ordinal);
        var discovered = new List<OperatorSession>();
        foreach (var info in ParseListSessions(result.Stdout))
        {
            if (!string.Equals(info.SourceInstanceId, settings.SourceInstanceId, StringComparison.Ordinal)
                && !TmuxSessionNaming.LooksManaged(info.SessionName))
            {
                continue;
            }

            foundNames.Add(info.SessionName);
            var identity = TmuxSessionNaming.FromSessionName(info.SessionName);
            var warnings = string.Equals(info.SourceInstanceId, settings.SourceInstanceId, StringComparison.Ordinal)
                ? Array.Empty<string>()
                : new[] { "Discovered managed tmux session without matching source metadata; controls remain local-only until metadata is refreshed." };
            var session = BuildSession(
                identity,
                settings,
                NullIfBlank(info.ProjectId),
                ParseLong(info.TaskId),
                NullIfBlank(info.WorkspaceId),
                NullIfBlank(info.Title) ?? info.SessionName,
                NullIfBlank(info.Cwd),
                warnings,
                createdAt: FromUnixSeconds(info.CreatedAtEpoch),
                lastActivityAt: FromUnixSeconds(info.ActivityEpoch));
            discovered.Add(_registry.Register(session));
        }

        foreach (var session in _registry.List(backend: OperatorSessionBackend.Tmux))
        {
            var name = TmuxNameFromSession(session);
            if (name is not null && !foundNames.Contains(name) && session.Status is not OperatorSessionStatus.Stale and not OperatorSessionStatus.Exited)
            {
                var stale = session with
                {
                    Status = OperatorSessionStatus.Stale,
                    Capabilities = OperatorSessionCapabilities.ObserveOnly("tmux session was not found during rediscovery; controls are disabled until it reappears.", canReadActivity: session.RecentActivity.Count > 0),
                    LastObservedAt = _now().UtcDateTime,
                    Warnings = AppendWarning(session.Warnings, "tmux session was not found during rediscovery."),
                };
                _registry.Register(stale);
            }
        }

        return discovered;
    }

    public async Task<TerminalAttachResponse> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireTmuxSession(request.SessionId, s => s.Capabilities.CanAttach, "attach");
        if (request.Viewport is not null)
        {
            ValidateViewport(request.Viewport);
        }

        var now = Format(_now());
        var capabilities = ToAttachCapabilities(session.Capabilities);
        if (string.Equals(request.Mode, "external_attach_info", StringComparison.Ordinal))
        {
            var infoResponse = BuildAttachResponse(session, string.Empty, now, capabilities);
            await PublishSessionEventsAsync(session, "session.external_attach_info_requested", new { mode = request.Mode, raw_stream = false }, null, null, cancellationToken).ConfigureAwait(false);
            return infoResponse;
        }

        var streamId = $"stream_{Guid.NewGuid():N}";
        lock (_lock)
        {
            _streams[streamId] = session.SessionId;
        }

        var response = BuildAttachResponse(session, streamId, now, capabilities);

        if (string.Equals(request.Mode, "activity_only", StringComparison.Ordinal))
        {
            await PublishSessionEventsAsync(session, "session.attached", new { stream_id = streamId, mode = request.Mode, raw_stream = false }, null, null, cancellationToken).ConfigureAwait(false);
            return response;
        }

        if (!session.Capabilities.CanStreamTerminal)
        {
            throw Unsupported("attach", "This session does not support terminal_stream attach mode.");
        }

        var capture = await CaptureAsync(session, request.Viewport, cancellationToken).ConfigureAwait(false);
        if (capture.Length > 0)
        {
            var chunks = BufferFor(session.SessionId).Append(capture, "replay", request.Viewport?.Cols, request.Viewport?.Rows);
            await PublishOutputChunksAsync(streamId, session.SessionId, chunks, cancellationToken).ConfigureAwait(false);
            var last = chunks[^1];
            response = response with
            {
                StartCursor = last.StreamCursor,
                ReplayAvailableFrom = chunks[0].StreamCursor,
            };
        }

        await PublishSessionEventsAsync(session, "session.attached", new { stream_id = streamId, mode = request.Mode }, null, null, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async Task<TerminalDetachResponse> DetachAsync(TerminalDetachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireTmuxSession(request.SessionId, s => s.Capabilities.CanDetach, "detach");
        lock (_lock)
        {
            _streams.Remove(request.StreamId);
        }

        await PublishSessionEventsAsync(session, "session.detached", new { stream_id = request.StreamId }, null, request.Reason, cancellationToken).ConfigureAwait(false);
        return new TerminalDetachResponse { Detached = true, BackendPreserved = true };
    }

    public async Task<TerminalSendInputResponse> SendInputAsync(TerminalSendInputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireTmuxSession(request.SessionId, s => s.Capabilities.CanSendInput, "send_input");
        var bytes = DecodeInput(request);
        if (bytes.Length > new TerminalStreamLimits().InputChunkMaxBytes)
        {
            throw Invalid("Terminal input exceeds the 16 KiB per-command limit.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var target = TmuxNameFromSession(session) ?? throw Invalid("tmux backend reference is missing a session name.");
        var result = await _tmux.RunAsync(["send-keys", "-t", target, "-l", "--", text], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw BackendUnavailable("send_input", result.Stderr);
        }

        if (request.StreamId is not null)
        {
            var capture = await CaptureAsync(session, null, cancellationToken).ConfigureAwait(false);
            if (capture.Length > 0)
            {
                var chunks = BufferFor(session.SessionId).Append(capture, "live");
                await PublishOutputChunksAsync(request.StreamId, session.SessionId, chunks, cancellationToken).ConfigureAwait(false);
            }
        }

        await PublishSessionEventsAsync(session, "session.input_sent", new { byte_count = bytes.Length, input_id = request.InputId }, null, null, cancellationToken).ConfigureAwait(false);
        return new TerminalSendInputResponse { Accepted = true, InputId = request.InputId, WrittenBytes = bytes.Length };
    }

    public async Task<TerminalResizeResponse> ResizeAsync(TerminalResizeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireTmuxSession(request.SessionId, s => s.Capabilities.CanResize, "resize");
        if (request.Cols is < 1 or > 500 || request.Rows is < 1 or > 500)
        {
            throw Invalid("Terminal viewport cols/rows must be within 1..500.");
        }

        var target = TmuxNameFromSession(session) ?? throw Invalid("tmux backend reference is missing a session name.");
        await ResizeTmuxWindowAsync(target, new TerminalViewport { Cols = request.Cols, Rows = request.Rows }, cancellationToken).ConfigureAwait(false);

        await PublishSessionEventsAsync(session, "session.resize_requested", new { cols = request.Cols, rows = request.Rows }, null, null, cancellationToken).ConfigureAwait(false);
        return new TerminalResizeResponse { Accepted = true, Cols = request.Cols, Rows = request.Rows };
    }

    public async Task<TerminalTerminateResponse> TerminateAsync(TerminalTerminateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var needsKill = string.Equals(request.Mode, "kill", StringComparison.OrdinalIgnoreCase);
        var session = RequireTmuxSession(request.SessionId, s => needsKill ? s.Capabilities.CanKill : s.Capabilities.CanTerminate, "terminate");
        var target = TmuxNameFromSession(session) ?? throw Invalid("tmux backend reference is missing a session name.");
        var result = await _tmux.RunAsync(["kill-session", "-t", target], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw BackendUnavailable("terminate", result.Stderr);
        }

        var eventId = $"evt_terminal_{Guid.NewGuid():N}";
        var exited = _registry.Register(session with
        {
            Status = OperatorSessionStatus.Exited,
            ExitedAt = _now().UtcDateTime,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("tmux session has been terminated.", canReadActivity: session.RecentActivity.Count > 0),
        });
        await PublishSessionEventsAsync(exited, "session.terminate_completed", new { event_id = eventId, mode = request.Mode }, request.RequestedBy, request.Reason, cancellationToken).ConfigureAwait(false);
        await PublishStatusEventsAsync(exited, cancellationToken).ConfigureAwait(false);
        return new TerminalTerminateResponse { Accepted = true, Mode = request.Mode, TerminalEventId = eventId };
    }

    public Task<TerminalAttachResponse> ReconnectAsync(TerminalReconnectRequest request, CancellationToken cancellationToken = default)
    {
        return AttachAsync(new TerminalAttachRequest
        {
            SessionId = request.SessionId,
            Mode = "terminal_stream",
            Viewport = request.Viewport,
            Replay = new TerminalReplaySpec { AfterCursor = request.LastSeenCursor },
            ClientId = request.PreviousStreamId,
        }, cancellationToken);
    }

    public Task<TerminalAckOutputResponse> AckOutputAsync(TerminalAckOutputRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireTmuxSession(request.SessionId, s => s.Capabilities.CanStreamTerminal, "ack_output");
        // Current tmux support is snapshot-capture based and has no long-lived
        // per-subscriber queue to throttle. ack_output is still capability-
        // validated so callers can use the same contract; active tmux stream
        // backpressure belongs with the live tmux backend work in #909/#911.
        return Task.FromResult(new TerminalAckOutputResponse { Accepted = true });
    }

    public IReadOnlyList<LocalSessionSnapshot> BuildSnapshotListForDen()
    {
        var snapshots = new List<LocalSessionSnapshot>();
        foreach (var session in _registry.List(backend: OperatorSessionBackend.Tmux))
        {
            if (string.IsNullOrWhiteSpace(session.ProjectId))
            {
                continue;
            }

            snapshots.Add(new LocalSessionSnapshot
            {
                ProjectId = session.ProjectId,
                Request = BuildSnapshotRequest(session),
            });
        }

        return snapshots;
    }

    private async Task SetMetadataAsync(string sessionName, OperatorSettings settings, TerminalCreateSessionRequest request, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string?>
        {
            ["source_instance_id"] = settings.SourceInstanceId,
            ["project_id"] = request.ProjectId,
            ["task_id"] = request.TaskId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["workspace_id"] = request.WorkspaceId,
            ["title"] = request.Title,
            ["cwd"] = request.Cwd,
        };

        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            await _tmux.RunAsync(["set-option", "-t", sessionName, LabelPrefix + key, value], cancellationToken).ConfigureAwait(false);
        }
    }

    private OperatorSession RequireTmuxSession(string sessionId, Func<OperatorSession, bool> predicate, string action)
    {
        var session = _registry.Get(sessionId) ?? throw NotFound(sessionId);
        if (!string.Equals(session.Backend, OperatorSessionBackend.Tmux, StringComparison.Ordinal))
        {
            throw Unsupported(action, "Session is not tmux-backed.");
        }

        if (!predicate(session))
        {
            throw Unsupported(action, session.Capabilities.Reason ?? "Capability is disabled for this session.");
        }

        return session;
    }

    private async Task<byte[]> CaptureAsync(OperatorSession session, TerminalViewport? viewport, CancellationToken cancellationToken)
    {
        var target = TmuxNameFromSession(session) ?? throw Invalid("tmux backend reference is missing a session name.");
        var replayRows = 200;
        if (viewport is not null)
        {
            ValidateViewport(viewport);
            replayRows = viewport.Rows;
            await ResizeTmuxWindowAsync(target, viewport, cancellationToken).ConfigureAwait(false);
        }

        var result = await _tmux.RunAsync(["capture-pane", "-p", "-e", "-J", "-S", $"-{replayRows}", "-t", target], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw BackendUnavailable("capture", result.Stderr);
        }

        return Encoding.UTF8.GetBytes(result.Stdout);
    }

    private async Task ResizeTmuxWindowAsync(string target, TerminalViewport viewport, CancellationToken cancellationToken)
    {
        var result = await _tmux.RunAsync(["resize-window", "-t", target, "-x", viewport.Cols.ToString(), "-y", viewport.Rows.ToString()], cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw BackendUnavailable("resize", result.Stderr);
        }
    }

    private static void ValidateViewport(TerminalViewport viewport)
    {
        if (viewport.Cols is < 1 or > 500 || viewport.Rows is < 1 or > 500)
        {
            throw Invalid("Terminal viewport cols/rows must be within 1..500.");
        }
    }

    private async ValueTask PublishOutputChunksAsync(string streamId, string sessionId, IReadOnlyList<TerminalOutputChunk> chunks, CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            await _events.PublishAsync(DesktopSidecarProtocol.TerminalOutputEvent, new TerminalOutputEvent
            {
                StreamId = streamId,
                SessionId = sessionId,
                TerminalSequence = chunk.Sequence,
                StreamCursor = chunk.StreamCursor,
                ChunkId = chunk.ChunkId,
                Origin = chunk.Origin,
                Data = Convert.ToBase64String(chunk.Data),
                ByteCount = chunk.ByteCount,
                Cols = chunk.Cols,
                Rows = chunk.Rows,
                EmittedAt = Format(_now()),
                Truncated = chunk.Truncated,
                Redacted = chunk.Redacted,
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask PublishStatusEventsAsync(OperatorSession session, CancellationToken cancellationToken)
    {
        await _events.PublishAsync(DesktopSidecarProtocol.TerminalSessionStatusEvent, new TerminalSessionEvent
        {
            SessionId = session.SessionId,
            Status = session.Status,
            Capabilities = ToAttachCapabilities(session.Capabilities),
            Warnings = session.Warnings,
            ObservedAt = Format(_now()),
        }, cancellationToken).ConfigureAwait(false);
        await _events.PublishAsync(DesktopSidecarProtocol.TerminalSessionListEvent, new TerminalListSessionsResponse
        {
            Sessions = _registry.List(backend: OperatorSessionBackend.Tmux).Select(TerminalSessionSummaryProjection.FromSession).ToList(),
            Count = _registry.List(backend: OperatorSessionBackend.Tmux).Count,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishSessionEventsAsync(OperatorSession session, string eventType, object payload, string? requestedBy, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.ProjectId))
        {
            return;
        }

        var settings = _settingsService.Load();
        try
        {
            await _den.PublishSessionEventAsync(settings.DenBaseUrl, session.ProjectId, new AppendDesktopSessionEventRequest
            {
                TaskId = session.TaskId is { } taskId ? (int?)checked((int)taskId) : null,
                WorkspaceId = session.WorkspaceId,
                SourceInstanceId = session.SourceInstanceId,
                SessionId = session.SessionId,
                EventType = eventType,
                Payload = BridgeJson.Serialize(payload),
                RequestedBy = requestedBy,
                Reason = reason,
                ObservedAt = _now().UtcDateTime,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Den event publication is best-effort here; snapshots still carry current state.
        }
    }

    private void MarkTmuxSessionsStale(string warning)
    {
        foreach (var session in _registry.List(backend: OperatorSessionBackend.Tmux))
        {
            if (session.Status is OperatorSessionStatus.Exited)
            {
                continue;
            }

            _registry.Register(session with
            {
                Status = OperatorSessionStatus.Stale,
                Capabilities = OperatorSessionCapabilities.ObserveOnly(warning, canReadActivity: session.RecentActivity.Count > 0),
                Warnings = AppendWarning(session.Warnings, warning),
            });
        }
    }

    private OperatorSession BuildSession(
        TmuxSessionIdentity identity,
        OperatorSettings settings,
        string? projectId,
        long? taskId,
        string? workspaceId,
        string? title,
        string? cwd,
        IReadOnlyList<string> warnings,
        DateTime? createdAt = null,
        DateTime? lastActivityAt = null)
    {
        var now = _now().UtcDateTime;
        var caps = IsAgentSession(null, projectId, taskId)
            ? TmuxAgentCapabilities()
            : TmuxCapabilities();
        return new OperatorSession
        {
            SessionId = identity.SessionId,
            GlobalRef = $"den-desktop://{settings.SourceInstanceId}/{identity.SessionId}",
            Title = title ?? identity.SessionName,
            DisplayName = title ?? identity.SessionName,
            ProjectId = projectId,
            TaskId = taskId,
            WorkspaceId = workspaceId,
            Cwd = cwd,
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            CurrentCommand = "tmux session",
            Capabilities = caps,
            CreatedAt = createdAt ?? now,
            StartedAt = createdAt ?? now,
            LastObservedAt = now,
            LastActivityAt = lastActivityAt ?? now,
            SourceInstanceId = settings.SourceInstanceId,
            SourceDisplayName = settings.SourceDisplayName,
            Warnings = warnings,
            UpdatedAt = now,
        };
    }

    private static OperatorSessionCapabilities TmuxCapabilities()
    {
        return OperatorSessionCapabilities.FullControl() with
        {
            // CanDeliverCompiledResponse defaults false from FullControl().
            // Only set true for agent-kind sessions via TmuxAgentCapabilities().
            RequiresConfirmation = true,
            LeaseRequired = false,
            Constraints = "{\"backend_kind\":\"persistent_terminal\",\"persistence_kind\":\"tmux\",\"ownership_kind\":\"backend_persistent\",\"raw_stream_scope\":\"local_bridge_only\",\"tmux_capture_replay\":\"viewport_rows_limit_and_resize_window\",\"external_attach_command\":\"display_copy_only\",\"backpressure_contract\":\"snapshot_capture_ack_validated_live_stream_enforcement_deferred_to_909_911\"}",
        };
    }

    /// <summary>
    /// Capabilities for tmux sessions that are identified as agent-kind
    /// (have an agent_identity or role set). These sessions are approved
    /// for compiled response delivery because they run agent software that
    /// can parse the delimiter protocol.
    /// </summary>
    private static OperatorSessionCapabilities TmuxAgentCapabilities()
    {
        return TmuxCapabilities() with
        {
            CanDeliverCompiledResponse = true,
        };
    }

    private static DesktopSessionSnapshotRequest BuildSnapshotRequest(OperatorSession session)
    {
        return new DesktopSessionSnapshotRequest
        {
            TaskId = session.TaskId,
            WorkspaceId = session.WorkspaceId,
            SessionId = session.SessionId,
            ParentSessionId = session.ParentSessionId,
            AgentIdentity = session.AgentIdentity,
            Role = session.Role,
            CurrentCommand = session.CurrentCommand,
            CurrentPhase = session.Status,
            Title = session.Title,
            DisplayName = session.DisplayName,
            Cwd = session.Cwd,
            Kind = session.Kind,
            Backend = session.Backend,
            Status = session.Status,
            StartedAt = Format(session.StartedAt),
            LastActivityAt = Format(session.LastActivityAt),
            ExitedAt = Format(session.ExitedAt),
            ExitCode = session.ExitCode,
            SourceDisplayName = session.SourceDisplayName,
            Capabilities = BridgeJson.ToElement(new
            {
                session.Capabilities.CanAttach,
                session.Capabilities.CanDetach,
                session.Capabilities.CanSendInput,
                session.Capabilities.CanResize,
                session.Capabilities.CanTerminate,
                session.Capabilities.CanKill,
                session.Capabilities.CanReconnect,
                session.Capabilities.CanOpenExternalAttach,
                session.Capabilities.CanReadActivity,
                session.Capabilities.CanStreamTerminal,
                session.Capabilities.CanDeliverCompiledResponse,
                persistence_kind = "tmux",
                ownership_kind = "backend_persistent",
                raw_stream_scope = "local_bridge_only",
            }),
            ControlCapabilities = BridgeJson.ToElement(new
            {
                can_attach = session.Capabilities.CanAttach,
                can_detach = session.Capabilities.CanDetach,
                can_send_input = session.Capabilities.CanSendInput,
                can_resize = session.Capabilities.CanResize,
                can_terminate = session.Capabilities.CanTerminate,
                can_open_external_attach = session.Capabilities.CanOpenExternalAttach,
                can_stream_terminal = session.Capabilities.CanStreamTerminal,
            }),
            RecentActivity = BridgeJson.ToElement(new { items = session.RecentActivity }),
            ChildSessions = BridgeJson.ToElement(new { items = session.Children }),
            Warnings = session.Warnings,
            SourceInstanceId = session.SourceInstanceId,
            ObservedAt = Format(DateTimeOffset.UtcNow),
        };
    }

    private static IReadOnlyList<TmuxSessionInfo> ParseListSessions(string stdout)
    {
        var list = new List<TmuxSessionInfo>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 10)
            {
                continue;
            }

            list.Add(new TmuxSessionInfo
            {
                SessionName = parts[0],
                CreatedAtEpoch = parts[1],
                AttachedClients = parts[2],
                ActivityEpoch = parts[3],
                SourceInstanceId = parts[4],
                ProjectId = parts[5],
                TaskId = parts[6],
                WorkspaceId = parts[7],
                Title = parts[8],
                Cwd = parts[9],
            });
        }

        return list;
    }

    private static byte[] DecodeInput(TerminalSendInputRequest request)
    {
        return request.Encoding switch
        {
            "utf8" => Encoding.UTF8.GetBytes(request.Data),
            "base64" => Convert.FromBase64String(request.Data),
            _ => throw Invalid("Terminal input encoding must be 'utf8' or 'base64'."),
        };
    }

    private OperatorSessionActivityBuffer BufferFor(string sessionId)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(sessionId, out var buffer))
            {
                buffer = new OperatorSessionActivityBuffer();
                _buffers[sessionId] = buffer;
            }

            return buffer;
        }
    }

    private static TerminalAttachResponse BuildAttachResponse(OperatorSession session, string streamId, string attachedAt, TerminalAttachCapabilities capabilities)
    {
        return new TerminalAttachResponse
        {
            StreamId = streamId,
            SessionId = session.SessionId,
            AttachedAt = attachedAt,
            StartCursor = "cur_000000000000",
            ReplayAvailableFrom = "cur_000000000000",
            ReplayGap = false,
            Capabilities = capabilities,
            ViewportLimits = TmuxViewportLimits,
            Limits = new TerminalStreamLimits(),
            ExternalAttach = session.Capabilities.CanOpenExternalAttach
                ? new TerminalExternalAttachInfo
                {
                    Available = true,
                    Command = TmuxSessionNaming.ExternalAttachCommand(TmuxNameFromSession(session) ?? session.SessionId),
                    Description = "Display/copy-only tmux attach command text. Den Desktop must not auto-execute this string; any future attach action must route through a typed app-core command.",
                }
                : null,
        };
    }

    private static TerminalAttachCapabilities ToAttachCapabilities(OperatorSessionCapabilities caps)
    {
        return new TerminalAttachCapabilities
        {
            CanSendInput = caps.CanSendInput,
            CanResize = caps.CanResize,
            CanDetach = caps.CanDetach,
            CanTerminate = caps.CanTerminate,
            CanStreamTerminal = caps.CanStreamTerminal,
        };
    }

    private static string? TmuxNameFromSession(OperatorSession session)
    {
        if (string.IsNullOrWhiteSpace(session.BackendRef))
        {
            return null;
        }

        var slash = session.BackendRef.LastIndexOf('/');
        return slash >= 0 ? session.BackendRef[(slash + 1)..] : session.BackendRef;
    }

    private static string[] AppendWarning(IReadOnlyList<string> warnings, string warning)
    {
        return warnings.Concat([warning]).Distinct(StringComparer.Ordinal).Take(10).ToArray();
    }

    /// <summary>
    /// Determine whether a session qualifies as agent-kind for capability purposes.
    /// A session is considered agent-kind if it has a task association and project,
    /// which indicates it was created to run an agent (not a plain shell).
    /// This is a conservative heuristic; agent_identity/role are set later by
    /// the session observer and are not available at creation time.
    /// </summary>
    private static bool IsAgentSession(string? agentIdentity, string? projectId, long? taskId)
    {
        // If agent_identity is already set, it's definitively agent-kind.
        if (!string.IsNullOrWhiteSpace(agentIdentity)) return true;
        // Sessions with both project and task association are likely agent sessions.
        // Plain shell sessions typically don't have both set.
        if (!string.IsNullOrWhiteSpace(projectId) && taskId.HasValue) return true;
        return false;
    }

    private static DateTime? FromUnixSeconds(string? value)
    {
        return long.TryParse(value, out var seconds) && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string TrimError(string value) => string.IsNullOrWhiteSpace(value) ? "no details" : value.Trim();

    private static string Format(DateTime? dt) => dt is null ? string.Empty : new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static string Format(DateTimeOffset dt) => dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static BridgeHandlerException NotFound(string sessionId) => new("terminal.session.not_found", $"Session '{sessionId}' not found in local registry.", BridgeErrorCategories.NotFound);

    private static BridgeHandlerException Unsupported(string action, string detail) => new($"terminal.{action}.unsupported", $"Terminal action '{action}' is not supported by this backend or session. {detail}", BridgeErrorCategories.UnsupportedCapability);

    private static BridgeHandlerException BackendUnavailable(string action, string detail) => new($"terminal.{action}.backend_unavailable", $"tmux backend action '{action}' failed: {TrimError(detail)}", BridgeErrorCategories.Unavailable, retryable: true);

    private static BridgeHandlerException Invalid(string message) => new("terminal.request.invalid", message, BridgeErrorCategories.Validation);

    private sealed record TmuxSessionInfo
    {
        public required string SessionName { get; init; }
        public string? CreatedAtEpoch { get; init; }
        public string? AttachedClients { get; init; }
        public string? ActivityEpoch { get; init; }
        public string? SourceInstanceId { get; init; }
        public string? ProjectId { get; init; }
        public string? TaskId { get; init; }
        public string? WorkspaceId { get; init; }
        public string? Title { get; init; }
        public string? Cwd { get; init; }
    }
}

public static class TerminalSessionSummaryProjection
{
    public static TerminalSessionSummary FromSession(OperatorSession session)
    {
        return new TerminalSessionSummary
        {
            SessionId = session.SessionId,
            Title = session.Title,
            DisplayName = session.DisplayName,
            Kind = session.Kind,
            Backend = session.Backend,
            Status = session.Status,
            CurrentCommand = session.CurrentCommand,
            AgentIdentity = session.AgentIdentity,
            Role = session.Role,
            ProjectId = session.ProjectId,
            TaskId = session.TaskId,
            WorkspaceId = session.WorkspaceId,
            Cwd = session.Cwd,
            SourceInstanceId = session.SourceInstanceId,
            SourceDisplayName = session.SourceDisplayName,
            CanReadActivity = session.Capabilities.CanReadActivity,
            CanSendInput = session.Capabilities.CanSendInput,
            CanResize = session.Capabilities.CanResize,
            CanTerminate = session.Capabilities.CanTerminate,
            CanAttach = session.Capabilities.CanAttach,
            CanDetach = session.Capabilities.CanDetach,
            CanReconnect = session.Capabilities.CanReconnect,
            CanStreamTerminal = session.Capabilities.CanStreamTerminal,
            CanOpenExternalAttach = session.Capabilities.CanOpenExternalAttach,
            CanDeliverCompiledResponse = session.Capabilities.CanDeliverCompiledResponse,
            PersistenceKind = string.Equals(session.Backend, OperatorSessionBackend.Tmux, StringComparison.Ordinal) ? "tmux" : "process_owned",
            OwnershipKind = string.Equals(session.Backend, OperatorSessionBackend.Tmux, StringComparison.Ordinal) ? "backend_persistent" : "sidecar_owned",
            CreatedAt = Format(session.CreatedAt),
            LastObservedAt = Format(session.LastObservedAt),
            LastActivityAt = Format(session.LastActivityAt),
            ExitedAt = Format(session.ExitedAt),
            ExitCode = session.ExitCode,
            Warnings = session.Warnings,
        };
    }

    private static string? Format(DateTime? dt) => dt?.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
