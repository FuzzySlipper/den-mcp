using System.Text;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace DenMcp.Desktop.Sidecar;

public sealed class DirectPtyOperatorSessionService : IAsyncDisposable, IDisposable
{
    private static readonly TerminalViewportLimits DirectPtyViewportLimits = new() { MinCols = 1, MaxCols = 500, MinRows = 1, MaxRows = 500 };
    private readonly IDirectPtyBackend _backend;
    private readonly OperatorSessionRegistry _registry;
    private readonly IOperatorRuntimeEventSink _events;
    private readonly OperatorSettingsService _settingsService;
    private readonly DenHttpClient _den;
    private readonly Func<DateTimeOffset> _now;
    private readonly TerminalStreamLimits _limits;
    private readonly Dictionary<string, IDirectPtyProcess> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectPtyStreamState> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperatorSessionActivityBuffer> _buffers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectPtyHeartbeatLoop> _heartbeatLoops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectPtyBackpressureTimer> _backpressureTimers = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public DirectPtyOperatorSessionService(
        IDirectPtyBackend backend,
        OperatorSessionRegistry registry,
        IOperatorRuntimeEventSink events,
        OperatorSettingsService settingsService,
        DenHttpClient den,
        Func<DateTimeOffset>? now = null,
        TerminalStreamLimits? limits = null)
    {
        _backend = backend;
        _registry = registry;
        _events = events;
        _settingsService = settingsService;
        _den = den;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _limits = limits ?? new TerminalStreamLimits();
    }

    public async Task<OperatorSession> CreateAsync(TerminalCreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Backend, OperatorSessionBackend.DirectPty, StringComparison.OrdinalIgnoreCase))
        {
            throw Unsupported("create_session", "Only direct_pty OperatorSessions are implemented by this backend.");
        }

        var settings = _settingsService.Load();
        var sessionId = $"pty:{Guid.NewGuid():N}";
        IDirectPtyProcess process;
        try
        {
            process = await _backend.SpawnAsync(new DirectPtyStartInfo
            {
                SessionId = sessionId,
                Title = request.Title,
                Cwd = request.Cwd,
                Cols = 120,
                Rows = 32,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not BridgeHandlerException && !cancellationToken.IsCancellationRequested)
        {
            throw new BridgeHandlerException(
                "terminal.create_session.backend_unavailable",
                $"direct PTY backend failed to start: {ex.Message}",
                BridgeErrorCategories.Unavailable,
                retryable: true);
        }

        lock (_lock)
        {
            _processes[sessionId] = process;
        }

        process.OutputReceived += (_, bytes) => _ = HandleOutputAsync(sessionId, bytes, null, null, CancellationToken.None);
        process.Exited += (_, args) => _ = HandleExitAsync(sessionId, args, CancellationToken.None);

        var now = _now().UtcDateTime;
        var caps = IsAgentSession(null, NullIfBlank(request.ProjectId), request.TaskId)
            ? DirectPtyAgentCapabilities()
            : DirectPtyCapabilities();
        var session = new OperatorSession
        {
            SessionId = sessionId,
            GlobalRef = $"den-desktop://{settings.SourceInstanceId}/{sessionId}",
            Title = request.Title ?? "Direct PTY",
            DisplayName = request.Title ?? "Direct PTY",
            ProjectId = NullIfBlank(request.ProjectId),
            TaskId = request.TaskId,
            WorkspaceId = request.WorkspaceId,
            Cwd = request.Cwd,
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            BackendRef = process.ProcessId is null ? sessionId : $"pid:{process.ProcessId}",
            Status = OperatorSessionStatus.Running,
            CurrentCommand = "direct PTY shell",
            Capabilities = caps,
            CreatedAt = now,
            StartedAt = now,
            LastObservedAt = now,
            LastActivityAt = now,
            SourceInstanceId = settings.SourceInstanceId,
            SourceDisplayName = settings.SourceDisplayName,
            UpdatedAt = now,
        };

        session = _registry.Register(session);
        await PublishSessionEventsAsync(session, "session.created", new { backend = OperatorSessionBackend.DirectPty, persistence = "sidecar_owned" }, null, null, cancellationToken).ConfigureAwait(false);
        await PublishStatusEventsAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    public async Task<TerminalAttachResponse> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireDirectSession(request.SessionId, s => s.Capabilities.CanAttach, "attach");
        if (!string.Equals(request.Mode, "terminal_stream", StringComparison.Ordinal) && !string.Equals(request.Mode, "activity_only", StringComparison.Ordinal))
        {
            throw Unsupported("attach", "direct_pty supports terminal_stream and activity_only attach modes.");
        }

        var streamId = $"stream_{Guid.NewGuid():N}";
        var viewport = request.Viewport ?? new TerminalViewport { Cols = 120, Rows = 32 };
        ValidateViewport(viewport.Cols, viewport.Rows);
        var stream = new DirectPtyStreamState(streamId, session.SessionId, request.ClientId, viewport.Cols, viewport.Rows);
        lock (_lock)
        {
            _streams[streamId] = stream;
        }

        if (session.Capabilities.CanResize)
        {
            ProcessFor(session.SessionId).Resize(viewport.Cols, viewport.Rows);
        }

        var buffer = BufferFor(session.SessionId);
        var after = ParseCursor(request.Replay?.AfterCursor);
        var maxChunks = Math.Clamp(request.Replay?.MaxChunks ?? 200, 1, 200);
        var replay = buffer.ReadAfter(after, maxChunks);
        if (replay.Chunks.Count > 0 && string.Equals(request.Mode, "terminal_stream", StringComparison.Ordinal))
        {
            await PublishOutputChunksAsync(streamId, session.SessionId, replay.Chunks, cancellationToken, originOverride: "replay").ConfigureAwait(false);
        }

        var response = new TerminalAttachResponse
        {
            StreamId = streamId,
            SessionId = session.SessionId,
            AttachedAt = Format(_now()),
            StartCursor = $"cur_{replay.NextCursor:D12}",
            ReplayAvailableFrom = $"cur_{replay.AvailableFrom:D12}",
            ReplayGap = replay.ReplayGap,
            Capabilities = ToAttachCapabilities(session.Capabilities),
            ViewportLimits = DirectPtyViewportLimits,
            Limits = _limits,
        };

        await _events.PublishAsync(DesktopSidecarProtocol.TerminalReplayCompleteEvent, new TerminalReplayCompleteEvent
        {
            StreamId = streamId,
            SessionId = session.SessionId,
            FromCursor = request.Replay?.AfterCursor,
            ToCursor = response.StartCursor,
            ReplayGap = replay.ReplayGap,
            DroppedBytesBeforeStart = replay.DroppedBytesBeforeStart,
        }, cancellationToken).ConfigureAwait(false);

        await PublishSessionEventsAsync(session, "session.attached", new { stream_id = streamId, mode = request.Mode, raw_stream = request.Mode == "terminal_stream" }, null, null, cancellationToken).ConfigureAwait(false);
        StartHeartbeatLoop(streamId, session.SessionId);
        return response;
    }

    public async Task<TerminalDetachResponse> DetachAsync(TerminalDetachRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireDirectSession(request.SessionId, s => s.Capabilities.CanDetach, "detach");
        DirectPtyHeartbeatLoop? heartbeatLoop;
        DirectPtyBackpressureTimer? backpressureTimer;
        lock (_lock)
        {
            _streams.Remove(request.StreamId);
            heartbeatLoop = RemoveHeartbeatLoopLocked(request.StreamId);
            backpressureTimer = RemoveBackpressureTimerLocked(request.StreamId);
        }

        await StopHeartbeatLoopAsync(heartbeatLoop).ConfigureAwait(false);
        await StopBackpressureTimerAsync(backpressureTimer).ConfigureAwait(false);
        await PublishSessionEventsAsync(session, "session.detached", new { stream_id = request.StreamId }, null, request.Reason, cancellationToken).ConfigureAwait(false);
        return new TerminalDetachResponse { Detached = true, BackendPreserved = true };
    }

    public async Task<TerminalSendInputResponse> SendInputAsync(TerminalSendInputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireDirectSession(request.SessionId, s => s.Capabilities.CanSendInput, "send_input");
        RequireAttachedStream(request.StreamId, session.SessionId, "send_input");
        var bytes = DecodeInput(request);
        if (bytes.Length > _limits.InputChunkMaxBytes)
        {
            throw Invalid("Terminal input exceeds the 16 KiB per-command limit.");
        }

        await ProcessFor(session.SessionId).WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        var activity = AppendActivity(session, "input", $"terminal input ({bytes.Length} bytes)");
        _registry.Register(activity with { LastActivityAt = _now().UtcDateTime, LastObservedAt = _now().UtcDateTime });
        await PublishSessionEventsAsync(session, "session.input_sent", new { byte_count = bytes.Length, input_id = request.InputId }, null, null, cancellationToken).ConfigureAwait(false);
        return new TerminalSendInputResponse { Accepted = true, InputId = request.InputId, WrittenBytes = bytes.Length };
    }

    public Task<TerminalResizeResponse> ResizeAsync(TerminalResizeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var session = RequireDirectSession(request.SessionId, s => s.Capabilities.CanResize, "resize");
        RequireAttachedStream(request.StreamId, session.SessionId, "resize");
        ValidateViewport(request.Cols, request.Rows);

        ProcessFor(session.SessionId).Resize(request.Cols, request.Rows);
        lock (_lock)
        {
            if (request.StreamId is not null && _streams.TryGetValue(request.StreamId, out var stream))
            {
                _streams[request.StreamId] = stream with { Cols = request.Cols, Rows = request.Rows };
            }
        }

        _registry.Register(session with { LastObservedAt = _now().UtcDateTime });
        return Task.FromResult(new TerminalResizeResponse { Accepted = true, Cols = request.Cols, Rows = request.Rows });
    }

    public async Task<TerminalTerminateResponse> TerminateAsync(TerminalTerminateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var needsKill = string.Equals(request.Mode, "kill", StringComparison.OrdinalIgnoreCase);
        var session = RequireDirectSession(request.SessionId, s => needsKill ? s.Capabilities.CanKill : s.Capabilities.CanTerminate, "terminate");
        await ProcessFor(session.SessionId).TerminateAsync(request.Mode, cancellationToken).ConfigureAwait(false);
        var eventId = $"evt_terminal_{Guid.NewGuid():N}";
        await PublishSessionEventsAsync(session, "session.terminate_requested", new { event_id = eventId, mode = request.Mode }, request.RequestedBy, request.Reason, cancellationToken).ConfigureAwait(false);
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

    public async Task<TerminalAckOutputResponse> AckOutputAsync(TerminalAckOutputRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDirectSession(request.SessionId, s => s.Capabilities.CanStreamTerminal, "ack_output");
        DirectPtyBackpressureTimer? backpressureTimer = null;
        lock (_lock)
        {
            if (request.StreamId is not null && _streams.TryGetValue(request.StreamId, out var stream))
            {
                _streams[request.StreamId] = stream with { UnackedBytes = 0, BackpressureEmitted = false };
                backpressureTimer = RemoveBackpressureTimerLocked(request.StreamId);
            }
        }

        await StopBackpressureTimerAsync(backpressureTimer).ConfigureAwait(false);
        return new TerminalAckOutputResponse { Accepted = true };
    }

    public IReadOnlyList<LocalSessionSnapshot> BuildSnapshotListForDen()
    {
        var snapshots = new List<LocalSessionSnapshot>();
        foreach (var session in _registry.List(backend: OperatorSessionBackend.DirectPty))
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

    public async ValueTask DisposeAsync()
    {
        IDirectPtyProcess[] processes;
        DirectPtyHeartbeatLoop[] heartbeatLoops;
        DirectPtyBackpressureTimer[] backpressureTimers;
        lock (_lock)
        {
            processes = _processes.Values.ToArray();
            heartbeatLoops = _heartbeatLoops.Values.ToArray();
            backpressureTimers = _backpressureTimers.Values.ToArray();
            _processes.Clear();
            _streams.Clear();
            _heartbeatLoops.Clear();
            _backpressureTimers.Clear();
        }

        foreach (var heartbeatLoop in heartbeatLoops)
        {
            await StopHeartbeatLoopAsync(heartbeatLoop).ConfigureAwait(false);
        }

        foreach (var backpressureTimer in backpressureTimers)
        {
            await StopBackpressureTimerAsync(backpressureTimer).ConfigureAwait(false);
        }

        foreach (var process in processes)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task HandleOutputAsync(string sessionId, byte[] bytes, int? cols, int? rows, CancellationToken cancellationToken)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        OperatorSession? updated = null;
        var session = _registry.Get(sessionId);
        if (session is not null)
        {
            updated = _registry.Register(AppendActivity(session, "output", $"terminal output ({bytes.Length} bytes)") with
            {
                LastActivityAt = _now().UtcDateTime,
                LastObservedAt = _now().UtcDateTime,
                Status = OperatorSessionStatus.Running,
            });
        }

        var chunks = BufferFor(sessionId).Append(bytes, "live", cols, rows);
        EnsurePublishableOutputChunks(chunks);
        DirectPtyStreamState[] streams;
        lock (_lock)
        {
            streams = _streams.Values.Where(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal)).ToArray();
        }

        var addedBytes = chunks.Sum(c => c.ByteCount);
        foreach (var stream in streams)
        {
            await PublishOutputChunksAsync(stream.StreamId, sessionId, chunks, cancellationToken).ConfigureAwait(false);
            DirectPtyStreamState next;
            var publishByteBackpressure = false;
            DirectPtyBackpressureTimer? backpressureTimer = null;
            lock (_lock)
            {
                if (!_streams.TryGetValue(stream.StreamId, out var current))
                {
                    continue;
                }

                var unackedBytes = current.UnackedBytes + addedBytes;
                var backpressureEmitted = current.BackpressureEmitted;
                if (unackedBytes >= _limits.AckAfterBytes)
                {
                    publishByteBackpressure = true;
                    backpressureEmitted = true;
                    backpressureTimer = RemoveBackpressureTimerLocked(stream.StreamId);
                }
                else if (current.UnackedBytes == 0 && unackedBytes > 0 && !backpressureEmitted)
                {
                    EnsureBackpressureTimerLocked(stream.StreamId, sessionId);
                }

                next = current with { UnackedBytes = unackedBytes, BackpressureEmitted = backpressureEmitted };
                _streams[stream.StreamId] = next;
            }

            if (backpressureTimer is not null)
            {
                await StopBackpressureTimerAsync(backpressureTimer).ConfigureAwait(false);
            }

            if (publishByteBackpressure)
            {
                await PublishBackpressureAsync(sessionId, stream.StreamId, next.UnackedBytes, cancellationToken).ConfigureAwait(false);
            }
        }

        if (updated is not null)
        {
            await PublishStatusEventsAsync(updated, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleExitAsync(string sessionId, DirectPtyExitedEventArgs args, CancellationToken cancellationToken)
    {
        var session = _registry.Get(sessionId);
        if (session is null)
        {
            return;
        }

        var exited = _registry.Register(session with
        {
            Status = args.Reason.StartsWith("pty_read_failed", StringComparison.Ordinal) ? OperatorSessionStatus.Failed : OperatorSessionStatus.Exited,
            ExitedAt = _now().UtcDateTime,
            ExitCode = args.ExitCode,
            FailureReason = args.Reason.StartsWith("pty_read_failed", StringComparison.Ordinal) ? args.Reason : null,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("direct PTY session has exited.", canReadActivity: true),
        });

        DirectPtyStreamState[] streams;
        DirectPtyHeartbeatLoop[] heartbeatLoops;
        DirectPtyBackpressureTimer[] backpressureTimers;
        lock (_lock)
        {
            streams = _streams.Values.Where(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal)).ToArray();
            foreach (var stream in streams)
            {
                _streams.Remove(stream.StreamId);
            }

            heartbeatLoops = RemoveHeartbeatLoopsLocked(streams.Select(s => s.StreamId));
            backpressureTimers = RemoveBackpressureTimersLocked(streams.Select(s => s.StreamId));
            _processes.Remove(sessionId);
        }

        foreach (var heartbeatLoop in heartbeatLoops)
        {
            await StopHeartbeatLoopAsync(heartbeatLoop).ConfigureAwait(false);
        }

        foreach (var backpressureTimer in backpressureTimers)
        {
            await StopBackpressureTimerAsync(backpressureTimer).ConfigureAwait(false);
        }

        foreach (var stream in streams)
        {
            await _events.PublishAsync(DesktopSidecarProtocol.TerminalExitEvent, new TerminalExitEvent
            {
                SessionId = sessionId,
                StreamId = stream.StreamId,
                ExitCode = args.ExitCode,
                Reason = args.Reason,
                ExitedAt = Format(_now()),
            }, cancellationToken).ConfigureAwait(false);
        }

        await PublishSessionEventsAsync(exited, "session.exited", new { exit_code = args.ExitCode, reason = args.Reason }, null, null, cancellationToken).ConfigureAwait(false);
        await PublishStatusEventsAsync(exited, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishOutputChunksAsync(string streamId, string sessionId, IReadOnlyList<TerminalOutputChunk> chunks, CancellationToken cancellationToken, string? originOverride = null)
    {
        EnsurePublishableOutputChunks(chunks);
        foreach (var chunk in chunks)
        {
            await _events.PublishAsync(DesktopSidecarProtocol.TerminalOutputEvent, new TerminalOutputEvent
            {
                StreamId = streamId,
                SessionId = sessionId,
                TerminalSequence = chunk.Sequence,
                StreamCursor = chunk.StreamCursor,
                ChunkId = chunk.ChunkId,
                Origin = originOverride ?? chunk.Origin,
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

    private void EnsurePublishableOutputChunks(IReadOnlyList<TerminalOutputChunk> chunks)
    {
        var maxBytes = EffectiveOutputChunkMaxBytes();
        var oversized = chunks.FirstOrDefault(chunk => chunk.ByteCount > maxBytes || chunk.Data.Length > maxBytes);
        if (oversized is not null)
        {
            throw new InvalidOperationException($"Terminal output chunk '{oversized.ChunkId}' exceeds the configured {nameof(TerminalStreamLimits.OutputChunkMaxBytes)} limit ({maxBytes} bytes).");
        }
    }

    private int EffectiveOutputChunkMaxBytes()
    {
        return _limits.OutputChunkMaxBytes > 0
            ? _limits.OutputChunkMaxBytes
            : OperatorSessionActivityBuffer.DefaultOutputChunkMaxBytes;
    }

    private async ValueTask PublishBackpressureAsync(string sessionId, string streamId, int queueBytes, CancellationToken cancellationToken)
    {
        await _events.PublishAsync(DesktopSidecarProtocol.TerminalBackpressureEvent, new TerminalBackpressureEvent
        {
            SessionId = sessionId,
            StreamId = streamId,
            State = "throttled",
            QueueBytes = queueBytes,
            DroppedBytes = BufferFor(sessionId).GetStats().DroppedBytesBeforeStart,
            NextAction = "ack_required",
        }, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureBackpressureTimerLocked(string streamId, string sessionId)
    {
        if (_backpressureTimers.ContainsKey(streamId))
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Max(1, _limits.AckAfterMillis));
        var cancellation = new CancellationTokenSource();
        var task = RunBackpressureTimerAsync(streamId, sessionId, delay, cancellation);
        _backpressureTimers[streamId] = new DirectPtyBackpressureTimer(cancellation, task);
    }

    private async Task RunBackpressureTimerAsync(string streamId, string sessionId, TimeSpan delay, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);

            int queueBytes;
            lock (_lock)
            {
                if (!_streams.TryGetValue(streamId, out var stream)
                    || !string.Equals(stream.SessionId, sessionId, StringComparison.Ordinal)
                    || stream.UnackedBytes <= 0
                    || stream.BackpressureEmitted)
                {
                    return;
                }

                queueBytes = stream.UnackedBytes;
                _streams[streamId] = stream with { BackpressureEmitted = true };
            }

            await PublishBackpressureAsync(sessionId, streamId, queueBytes, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected when output is acked, detached, exited, or the service is disposed.
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            // Backpressure is advisory to the local bridge; do not crash the PTY session on a transient publish failure.
        }
        finally
        {
            var removed = false;
            lock (_lock)
            {
                if (_backpressureTimers.TryGetValue(streamId, out var current) && ReferenceEquals(current.Cancellation, cancellation))
                {
                    _backpressureTimers.Remove(streamId);
                    removed = true;
                }
            }

            if (removed)
            {
                cancellation.Dispose();
            }
        }
    }

    private void StartHeartbeatLoop(string streamId, string sessionId)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, _limits.HeartbeatIntervalMs));
        var cancellation = new CancellationTokenSource();
        var task = RunHeartbeatLoopAsync(streamId, sessionId, interval, cancellation);
        DirectPtyHeartbeatLoop? previous;
        lock (_lock)
        {
            previous = RemoveHeartbeatLoopLocked(streamId);
            _heartbeatLoops[streamId] = new DirectPtyHeartbeatLoop(cancellation, task);
        }

        _ = StopHeartbeatLoopAsync(previous);
    }

    private async Task RunHeartbeatLoopAsync(string streamId, string sessionId, TimeSpan interval, CancellationTokenSource cancellation)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                if (!await PublishHeartbeatAsync(streamId, sessionId, cancellation.Token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected during detach/exit/dispose.
        }
        catch (Exception) when (!cancellation.IsCancellationRequested)
        {
            // Heartbeats are a liveness aid; do not let a transient bridge publish failure crash the PTY session.
        }
        finally
        {
            var removed = false;
            lock (_lock)
            {
                if (_heartbeatLoops.TryGetValue(streamId, out var current) && ReferenceEquals(current.Cancellation, cancellation))
                {
                    _heartbeatLoops.Remove(streamId);
                    removed = true;
                }
            }

            if (removed)
            {
                cancellation.Dispose();
            }
        }
    }

    private async ValueTask<bool> PublishHeartbeatAsync(string streamId, string sessionId, CancellationToken cancellationToken)
    {
        DirectPtyStreamState stream;
        lock (_lock)
        {
            if (!_streams.TryGetValue(streamId, out stream!) || !string.Equals(stream.SessionId, sessionId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        var session = _registry.Get(sessionId);
        if (session is null || !string.Equals(session.Status, OperatorSessionStatus.Running, StringComparison.Ordinal))
        {
            return false;
        }

        var stats = BufferFor(sessionId).GetStats();
        await _events.PublishAsync(DesktopSidecarProtocol.TerminalHeartbeatEvent, new TerminalHeartbeatEvent
        {
            SessionId = sessionId,
            StreamId = streamId,
            StreamCursor = $"cur_{stats.NewestSequence:D12}",
            BackendStatus = session.Status,
            LastActivityAt = Format(session.LastActivityAt),
            QueueBytes = stream.UnackedBytes,
            Paused = stream.BackpressureEmitted || stream.UnackedBytes >= _limits.AckAfterBytes,
        }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async ValueTask StopHeartbeatLoopAsync(DirectPtyHeartbeatLoop? heartbeatLoop)
    {
        if (heartbeatLoop is null)
        {
            return;
        }

        await heartbeatLoop.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await heartbeatLoop.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            heartbeatLoop.Cancellation.Dispose();
        }
    }

    private DirectPtyHeartbeatLoop? RemoveHeartbeatLoopLocked(string streamId)
    {
        if (_heartbeatLoops.Remove(streamId, out var heartbeatLoop))
        {
            return heartbeatLoop;
        }

        return null;
    }

    private DirectPtyHeartbeatLoop[] RemoveHeartbeatLoopsLocked(IEnumerable<string> streamIds)
    {
        var heartbeatLoops = new List<DirectPtyHeartbeatLoop>();
        foreach (var streamId in streamIds)
        {
            if (RemoveHeartbeatLoopLocked(streamId) is { } heartbeatLoop)
            {
                heartbeatLoops.Add(heartbeatLoop);
            }
        }

        return heartbeatLoops.ToArray();
    }

    private static async ValueTask StopBackpressureTimerAsync(DirectPtyBackpressureTimer? backpressureTimer)
    {
        if (backpressureTimer is null)
        {
            return;
        }

        await backpressureTimer.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await backpressureTimer.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
        finally
        {
            backpressureTimer.Cancellation.Dispose();
        }
    }

    private DirectPtyBackpressureTimer? RemoveBackpressureTimerLocked(string streamId)
    {
        if (_backpressureTimers.Remove(streamId, out var backpressureTimer))
        {
            return backpressureTimer;
        }

        return null;
    }

    private DirectPtyBackpressureTimer[] RemoveBackpressureTimersLocked(IEnumerable<string> streamIds)
    {
        var backpressureTimers = new List<DirectPtyBackpressureTimer>();
        foreach (var streamId in streamIds)
        {
            if (RemoveBackpressureTimerLocked(streamId) is { } backpressureTimer)
            {
                backpressureTimers.Add(backpressureTimer);
            }
        }

        return backpressureTimers.ToArray();
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
            Sessions = _registry.List(kind: OperatorSessionKind.Terminal).Select(TerminalSessionSummaryProjection.FromSession).ToList(),
            Count = _registry.List(kind: OperatorSessionKind.Terminal).Count,
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
            // Den event publication is best-effort; raw terminal bytes never leave the local bridge.
        }
    }

    private OperatorSession RequireDirectSession(string sessionId, Func<OperatorSession, bool> predicate, string action)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw Invalid("session_id is required.");
        }

        var session = _registry.Get(sessionId) ?? throw NotFound(sessionId);
        if (!string.Equals(session.Backend, OperatorSessionBackend.DirectPty, StringComparison.Ordinal))
        {
            throw Unsupported(action, "Session is not direct_pty-backed.");
        }

        if (!predicate(session))
        {
            throw Unsupported(action, session.Capabilities.Reason ?? "Capability is disabled for this session.");
        }

        return session;
    }

    private IDirectPtyProcess ProcessFor(string sessionId)
    {
        lock (_lock)
        {
            if (_processes.TryGetValue(sessionId, out var process))
            {
                return process;
            }
        }

        throw new BridgeHandlerException("terminal.session.stale", $"direct PTY process for session '{sessionId}' is no longer available.", BridgeErrorCategories.Unavailable, retryable: true);
    }

    private void RequireAttachedStream(string? streamId, string sessionId, string action)
    {
        if (string.IsNullOrWhiteSpace(streamId))
        {
            throw Invalid($"stream_id is required for {action}.");
        }

        lock (_lock)
        {
            if (_streams.TryGetValue(streamId, out var stream) && string.Equals(stream.SessionId, sessionId, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw Invalid($"stream_id '{streamId}' is not attached to session '{sessionId}'.");
    }

    private OperatorSessionActivityBuffer BufferFor(string sessionId)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(sessionId, out var buffer))
            {
                buffer = new OperatorSessionActivityBuffer(
                    maxBytes: _limits.SessionReplayMaxBytes,
                    outputChunkMaxBytes: _limits.OutputChunkMaxBytes,
                    maxQueuedSubscriberBytes: _limits.SubscriberQueueMaxBytes);
                _buffers[sessionId] = buffer;
            }

            return buffer;
        }
    }

    private OperatorSession AppendActivity(OperatorSession session, string kind, string summary)
    {
        var next = session.RecentActivity.Concat([
            new OperatorSessionActivityItem
            {
                Kind = kind,
                Summary = summary,
                Timestamp = Format(_now()),
            },
        ]).TakeLast(50).ToArray();
        return session with { RecentActivity = next };
    }

    private static OperatorSessionCapabilities DirectPtyCapabilities()
    {
        return OperatorSessionCapabilities.FullControl() with
        {
            CanOpenExternalAttach = false,
            // CanDeliverCompiledResponse defaults false from FullControl().
            // Only set true for agent-kind sessions via DirectPtyAgentCapabilities().
            RequiresConfirmation = true,
            LeaseRequired = true,
            Constraints = "{\"backend_kind\":\"direct_pty\",\"persistence_kind\":\"process_owned\",\"ownership_kind\":\"sidecar_owned\",\"raw_stream_scope\":\"local_bridge_only\",\"backpressure_contract\":\"per_stream_unacked_bytes_throttled_until_ack\"}",
        };
    }

    /// <summary>
    /// Capabilities for direct-PTY sessions that are identified as agent-kind.
    /// These sessions are approved for compiled response delivery because they
    /// run agent software that can parse the delimiter protocol.
    /// </summary>
    private static OperatorSessionCapabilities DirectPtyAgentCapabilities()
    {
        return DirectPtyCapabilities() with
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
                session.Capabilities.CanReadActivity,
                session.Capabilities.CanStreamTerminal,
                persistence_kind = "process_owned",
                ownership_kind = "sidecar_owned",
                raw_stream_scope = "local_bridge_only",
            }),
            ControlCapabilities = BridgeJson.ToElement(new
            {
                can_attach = session.Capabilities.CanAttach,
                can_detach = session.Capabilities.CanDetach,
                can_send_input = session.Capabilities.CanSendInput,
                can_resize = session.Capabilities.CanResize,
                can_terminate = session.Capabilities.CanTerminate,
                can_stream_terminal = session.Capabilities.CanStreamTerminal,
            }),
            RecentActivity = BridgeJson.ToElement(new { items = session.RecentActivity }),
            ChildSessions = BridgeJson.ToElement(new { items = session.Children }),
            Warnings = session.Warnings,
            SourceInstanceId = session.SourceInstanceId,
            ObservedAt = Format(DateTimeOffset.UtcNow),
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

    private static byte[] DecodeInput(TerminalSendInputRequest request)
    {
        return request.Encoding switch
        {
            "utf8" => Encoding.UTF8.GetBytes(request.Data),
            "base64" => Convert.FromBase64String(request.Data),
            _ => throw Invalid("Terminal input encoding must be 'utf8' or 'base64'."),
        };
    }

    private static long ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        return long.TryParse(cursor.Replace("cur_", "", StringComparison.Ordinal), out var value) ? value : 0;
    }

    private static void ValidateViewport(int cols, int rows)
    {
        if (cols is < 1 or > 500 || rows is < 1 or > 500)
        {
            throw Invalid("Terminal viewport cols/rows must be within 1..500.");
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Determine whether a session qualifies as agent-kind for capability purposes.
    /// A session is considered agent-kind if it has a task association and project,
    /// which indicates it was created to run an agent (not a plain shell).
    /// This is a conservative heuristic; agent_identity/role are set later by
    /// the session observer and are not available at creation time.
    /// </summary>
    private static bool IsAgentSession(string? agentIdentity, string? projectId, long? taskId)
    {
        if (!string.IsNullOrWhiteSpace(agentIdentity)) return true;
        if (!string.IsNullOrWhiteSpace(projectId) && taskId.HasValue) return true;
        return false;
    }
    private static string Format(DateTime? dt) => dt is null ? string.Empty : new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    private static string Format(DateTimeOffset dt) => dt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    private static BridgeHandlerException NotFound(string sessionId) => new("terminal.session.not_found", $"Session '{sessionId}' not found in local registry.", BridgeErrorCategories.NotFound);
    private static BridgeHandlerException Unsupported(string action, string detail) => new($"terminal.{action}.unsupported", $"Terminal action '{action}' is not supported by this backend or session. {detail}", BridgeErrorCategories.UnsupportedCapability);
    private static BridgeHandlerException Invalid(string message) => new("terminal.request.invalid", message, BridgeErrorCategories.Validation);

    private sealed record DirectPtyStreamState(string StreamId, string SessionId, string? ClientId, int Cols, int Rows)
    {
        public int UnackedBytes { get; init; }
        public bool BackpressureEmitted { get; init; }
    }

    private sealed record DirectPtyHeartbeatLoop(CancellationTokenSource Cancellation, Task Task);
    private sealed record DirectPtyBackpressureTimer(CancellationTokenSource Cancellation, Task Task);
}
