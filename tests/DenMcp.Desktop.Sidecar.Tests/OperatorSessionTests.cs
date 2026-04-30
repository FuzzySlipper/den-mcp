using System.Text;
using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar.Tests;

public class OperatorSessionRegistryTests
{
    [Fact]
    public void Registry_RegistersAndRetrievesSession()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);

        var session = registry.Register(new OperatorSession
        {
            SessionId = "pty:test-1",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "desktop-test",
            Capabilities = OperatorSessionCapabilities.FullControl(),
            CreatedAt = now,
        });

        Assert.Equal("pty:test-1", session.SessionId);
        Assert.Equal(1, session.Sequence);

        var retrieved = registry.Get("pty:test-1");
        Assert.NotNull(retrieved);
        Assert.Equal(session.SessionId, retrieved!.SessionId);

        registry.Remove("pty:test-1");
        Assert.Null(registry.Get("pty:test-1"));
    }

    [Fact]
    public void Registry_ListsSessionsWithFilters()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);

        registry.Register(new OperatorSession
        {
            SessionId = "pty:1", Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty, Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test", CreatedAt = now,
        });
        registry.Register(new OperatorSession
        {
            SessionId = "pi-artifact:1", Kind = OperatorSessionKind.ArtifactObserver,
            Backend = OperatorSessionBackend.PiArtifact, Status = OperatorSessionStatus.Exited,
            SourceInstanceId = "test", CreatedAt = now,
        });
        registry.Register(new OperatorSession
        {
            SessionId = "tmux:1", Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.Tmux, Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test", CreatedAt = now,
        });

        Assert.Equal(3, registry.Count());
        Assert.Single(registry.List(kind: OperatorSessionKind.ArtifactObserver));
        Assert.Equal(2, registry.List(kind: OperatorSessionKind.Terminal).Count);
        Assert.Single(registry.List(backend: OperatorSessionBackend.Tmux));
        Assert.Single(registry.List(status: OperatorSessionStatus.Exited));
    }

    [Fact]
    public void Registry_RegisterFromPiSnapshot_ComputesObserveOnlyCapabilities()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);

        // A Pi artifact snapshot with recent activity (can_read_activity = true)
        var activityItems = JsonSerializer.SerializeToElement(new
        {
            schema = "den_desktop_recent_activity",
            schema_version = 1,
            items = new[]
            {
                new { kind = "assistant_tool_call", role = "assistant", tool = "bash", summary = "git status", timestamp = "2026-04-29T11:59:00.000Z" },
                new { kind = "tool_result", role = "toolResult", tool = "bash", summary = "git status output", timestamp = "2026-04-29T11:59:01.000Z" },
            },
        });
        var capabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities_v2", schema_version = 1 });
        var controlCapabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities", schema_version = 1 });

        var snapshot = new LocalSessionSnapshot
        {
            ProjectId = "den-mcp",
            ArtifactRoot = "/tmp/runs/run-1",
            Request = new DesktopSessionSnapshotRequest
            {
                SessionId = "pi-artifact:1",
                Title = "run-1",
                DisplayName = "coder",
                Cwd = "/repo",
                Kind = "artifact_observer",
                Backend = "pi_artifact",
                Status = "running",
                AgentIdentity = "pi",
                Role = "coder",
                TaskId = 999,
                CurrentCommand = "bash",
                CurrentPhase = "running",
                StartedAt = "2026-04-29T11:00:00.000Z",
                LastActivityAt = "2026-04-29T11:59:01.000Z",
                SourceInstanceId = "desktop-test",
                SourceDisplayName = "Desktop Test",
                ObservedAt = "2026-04-29T12:00:00.000Z",
                RecentActivity = activityItems,
                Capabilities = capabilities,
                ControlCapabilities = controlCapabilities,
                Warnings = [],
            },
        };

        var session = registry.RegisterFromPiSnapshot(snapshot);

        // R907-4: can_read_activity computed from recent activity
        Assert.True(session.Capabilities.CanReadActivity);
        Assert.False(session.Capabilities.CanAttach);
        Assert.False(session.Capabilities.CanSendInput);
        Assert.False(session.Capabilities.CanTerminate);
        Assert.False(session.Capabilities.CanStreamTerminal);
        Assert.Contains("Artifact-observer", session.Capabilities.Reason, StringComparison.Ordinal);
        Assert.Equal(OperatorSessionKind.ArtifactObserver, session.Kind);
        Assert.Equal(OperatorSessionBackend.PiArtifact, session.Backend);
        Assert.Equal(OperatorSessionStatus.Running, session.Status);
        Assert.Equal(2, session.RecentActivity.Count);
        Assert.Equal("bash", session.RecentActivity[0].Tool);
    }

    [Fact]
    public void Registry_RegisterFromPiSnapshot_CanReadActivityFalseWhenNoActivity()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        var emptyActivity = JsonSerializer.SerializeToElement(new { schema = "den_desktop_recent_activity", schema_version = 1, items = Array.Empty<object>() });
        var capabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities_v2", schema_version = 1 });
        var controlCapabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities", schema_version = 1 });

        var snapshot = new LocalSessionSnapshot
        {
            ProjectId = "den-mcp",
            ArtifactRoot = "/tmp/runs/run-empty",
            Request = new DesktopSessionSnapshotRequest
            {
                SessionId = "pi-artifact:empty",
                Title = "run-empty",
                Kind = "artifact_observer",
                Backend = "pi_artifact",
                Status = "exited",
                AgentIdentity = "pi",
                SourceInstanceId = "desktop-test",
                SourceDisplayName = "Desktop Test",
                ObservedAt = "2026-04-29T12:00:00.000Z",
                RecentActivity = emptyActivity,
                Capabilities = capabilities,
                ControlCapabilities = controlCapabilities,
                Warnings = [],
            },
        };

        var session = registry.RegisterFromPiSnapshot(snapshot);
        Assert.False(session.Capabilities.CanReadActivity);
        Assert.Empty(session.RecentActivity);
    }
}

public class OperatorSessionLeaseStoreTests
{
    [Fact]
    public void LeaseStore_AcquireAndHeartbeatAndRelease()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        var result = store.TryAcquire("tmux:/tmp/tmux-1000/default:0:1", "pty:1", "desktop-instance-1");
        Assert.True(result.Acquired);
        Assert.NotNull(result.Lease);
        Assert.Equal("desktop-instance-1", result.Lease!.OwnerId);
        Assert.Equal(1, result.Lease.Generation);

        // Heartbeat (generation does NOT increment on heartbeat; only on re-acquire)
        var heartbeat = store.Heartbeat("tmux:/tmp/tmux-1000/default:0:1", "desktop-instance-1");
        Assert.NotNull(heartbeat);
        Assert.Equal(1, heartbeat!.Generation);

        // Release
        Assert.True(store.Release("tmux:/tmp/tmux-1000/default:0:1", "desktop-instance-1"));
        Assert.Null(store.GetLease("tmux:/tmp/tmux-1000/default:0:1"));
    }

    [Fact]
    public void LeaseStore_ConflictWhenDifferentOwner()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        var result1 = store.TryAcquire("tmux-socket/session", "pty:1", "instance-a");
        Assert.True(result1.Acquired);

        // Different owner -> conflict
        var result2 = store.TryAcquire("tmux-socket/session", "pty:2", "instance-b");
        Assert.False(result2.Acquired);
        Assert.True(result2.Conflict);
        Assert.NotNull(result2.ConflictingLease);
        Assert.Equal("instance-a", result2.ConflictingLease!.OwnerId);
    }

    [Fact]
    public void LeaseStore_ReAcquireBySameOwner()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        store.TryAcquire("target", "pty:1", "owner", durationSeconds: 60);
        var result2 = store.TryAcquire("target", "pty:2", "owner", durationSeconds: 60);
        Assert.True(result2.Acquired);
        Assert.NotNull(result2.Lease);
        Assert.Equal("owner", result2.Lease!.OwnerId);
        Assert.Equal("pty:2", result2.Lease.SessionId);
        // Generation increments
        Assert.Equal(2, result2.Lease.Generation);
    }

    [Fact]
    public void LeaseStore_ExpiredLeaseReplaced()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        // Acquire with 0 seconds (immediately expired)
        var result = store.TryAcquire("target", "pty:1", "owner-a", durationSeconds: 0);
        Assert.True(result.Acquired);

        // Time passes... well, with 0 seconds the lease is already expired
        // Immediately try to acquire with different owner
        // Since the lease is expired, it should be replaced
        var result2 = store.TryAcquire("target", "pty:2", "owner-b", durationSeconds: 60);
        Assert.True(result2.Acquired);
        Assert.Equal("owner-b", result2.Lease!.OwnerId);
    }

    [Fact]
    public void LeaseStore_GetLeaseReturnsNullForExpired()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        store.TryAcquire("target", "pty:1", "owner", durationSeconds: 0);
        Assert.Null(store.GetLease("target"));
    }

    [Fact]
    public void LeaseStore_ListLeasesCleansExpired()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var store = new OperatorSessionLeaseStore(() => now);

        store.TryAcquire("live", "pty:1", "owner", durationSeconds: 60);
        store.TryAcquire("dead", "pty:2", "owner", durationSeconds: 0);

        var leases = store.ListLeases();
        Assert.Single(leases);
        Assert.Equal("live", leases[0].TargetKey);
    }
}

public class OperatorSessionActivityBufferTests
{
    [Fact]
    public void ActivityBuffer_AppendAndRead()
    {
        var buffer = new OperatorSessionActivityBuffer(maxBytes: 100000, maxChunks: 100);

        var data = "hello world"u8.ToArray();
        var chunks = buffer.Append(data);

        Assert.Single(chunks);
        Assert.Equal(1, chunks[0].Sequence);
        Assert.Equal(11, chunks[0].ByteCount);

        var read = buffer.ReadAfter(0);
        Assert.Single(read.Chunks);
        Assert.Equal(1, read.Chunks[0].Sequence);
        Assert.False(read.ReplayGap);

        var readAfter = buffer.ReadAfter(1);
        Assert.Empty(readAfter.Chunks);
    }

    [Fact]
    public void ActivityBuffer_SplitsOversizedChunks_PerR945_2()
    {
        var buffer = new OperatorSessionActivityBuffer(maxBytes: 100000, maxChunks: 100, outputChunkMaxBytes: 64);

        var data = new byte[200];
        for (var i = 0; i < 200; i++) data[i] = (byte)(i % 256);

        var chunks = buffer.Append(data);

        // Should split into 4 chunks: 64 + 64 + 64 + 8
        Assert.Equal(4, chunks.Count);
        Assert.Equal(1, chunks[0].Sequence);
        Assert.Equal(64, chunks[0].ByteCount);
        Assert.Equal(2, chunks[1].Sequence);
        Assert.Equal(64, chunks[1].ByteCount);
        Assert.Equal(3, chunks[2].Sequence);
        Assert.Equal(64, chunks[2].ByteCount);
        Assert.Equal(4, chunks[3].Sequence);
        Assert.Equal(8, chunks[3].ByteCount);
    }

    [Fact]
    public void ActivityBuffer_EvictionDropsOldestChunks()
    {
        var buffer = new OperatorSessionActivityBuffer(maxBytes: 200, maxChunks: 5, outputChunkMaxBytes: 100);

        // Add 10 small chunks that exceed maxChunks=5
        for (var i = 0; i < 10; i++)
        {
            buffer.Append(new byte[] { (byte)i });
        }

        var stats = buffer.GetStats();
        Assert.Equal(5, stats.ChunkCount); // evicted down to 5
        // The first 5 chunks (sequences 1-5) should have been evicted
        Assert.Equal(6, stats.OldestSequence);
        Assert.Equal(10, stats.NewestSequence);
        Assert.True(stats.DroppedBytesBeforeStart > 0);
    }

    [Fact]
    public void ActivityBuffer_ReadAfterWithEvictionReturnsNewerChunks()
    {
        var buffer = new OperatorSessionActivityBuffer(maxBytes: 200, maxChunks: 2);
        buffer.Append("a"u8.ToArray());
        buffer.Append("b"u8.ToArray());
        buffer.Append("c"u8.ToArray());

        // maxChunks=2 means only seq 2 and 3 remain (seq 1 evicted)
        var read = buffer.ReadAfter(0);
        Assert.Equal(2, read.Chunks.Count);
        Assert.Equal(2, read.Chunks[0].Sequence);
        Assert.Equal(3, read.Chunks[1].Sequence);
        Assert.False(read.ReplayGap);

        // Read past newest: empty result, no gap
        var pastEnd = buffer.ReadAfter(3);
        Assert.Empty(pastEnd.Chunks);
        Assert.False(pastEnd.ReplayGap);
    }

    [Fact]
    public void ActivityBuffer_GetStatsReturnsCorrectState()
    {
        var buffer = new OperatorSessionActivityBuffer(maxBytes: 100000, maxChunks: 100);
        buffer.Append("hello"u8.ToArray());
        buffer.Append("world"u8.ToArray());

        var stats = buffer.GetStats();
        Assert.Equal(2, stats.ChunkCount);
        Assert.Equal(10, stats.TotalBytes);
        Assert.Equal(2, stats.NextSequence); // next_sequence was 0, then 1, then 2 for two appends
    }

    [Fact]
    public void ActivityBuffer_AppendEmptyReturnsEmpty()
    {
        var buffer = new OperatorSessionActivityBuffer();
        var chunks = buffer.Append(Array.Empty<byte>());
        Assert.Empty(chunks);
    }
}

public class TerminalBridgeHandlerTests
{
    [Fact]
    public async Task TerminalListSessionsHandler_ReturnsEmptyWhenNoSessions()
    {
        var registry = new OperatorSessionRegistry();
        var handler = new TerminalListSessionsHandler(registry);

        var result = await handler.HandleAsync(
            new TerminalListSessionsRequest(),
            TestContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result!.Sessions);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public async Task TerminalListSessionsHandler_ReturnsRegisteredSessions()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        registry.Register(new OperatorSession
        {
            SessionId = "pty:test",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.FullControl(),
            CreatedAt = now,
        });

        var handler = new TerminalListSessionsHandler(registry);
        var result = await handler.HandleAsync(
            new TerminalListSessionsRequest(),
            TestContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(result!.Sessions);
        Assert.Equal("pty:test", result.Sessions[0].SessionId);
        Assert.True(result.Sessions[0].CanSendInput);
        Assert.True(result.Sessions[0].CanAttach);
        Assert.True(result.Sessions[0].CanTerminate);
    }

    [Fact]
    public async Task TerminalListSessionsHandler_FiltersByKind()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        registry.Register(new OperatorSession
        {
            SessionId = "pty:1", Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty, Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test", CreatedAt = now,
            Capabilities = new OperatorSessionCapabilities(),
        });
        registry.Register(new OperatorSession
        {
            SessionId = "artifact:1", Kind = OperatorSessionKind.ArtifactObserver,
            Backend = OperatorSessionBackend.PiArtifact, Status = OperatorSessionStatus.Exited,
            SourceInstanceId = "test", CreatedAt = now,
            Capabilities = new OperatorSessionCapabilities(),
        });

        var handler = new TerminalListSessionsHandler(registry);
        var result = await handler.HandleAsync(
            new TerminalListSessionsRequest { Kind = OperatorSessionKind.ArtifactObserver },
            TestContext(),
            CancellationToken.None);

        Assert.Single(result!.Sessions);
        Assert.Equal("artifact:1", result.Sessions[0].SessionId);
    }

    [Fact]
    public async Task TerminalReadActivityHandler_ReturnsActivityForObserverSession()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        registry.Register(new OperatorSession
        {
            SessionId = "pi-artifact:1",
            Kind = OperatorSessionKind.ArtifactObserver,
            Backend = OperatorSessionBackend.PiArtifact,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.ObserveOnly("test", canReadActivity: true),
            CreatedAt = now,
            RecentActivity =
            [
                new OperatorSessionActivityItem { Kind = "assistant_tool_call", Tool = "bash", Summary = "git status", Timestamp = "2026-04-29T11:59:00Z" },
                new OperatorSessionActivityItem { Kind = "tool_result", Tool = "bash", Summary = "output", Timestamp = "2026-04-29T11:59:01Z" },
            ],
        });

        var handler = new TerminalReadActivityHandler(registry);
        var result = await handler.HandleAsync(
            new TerminalReadActivityRequest { SessionId = "pi-artifact:1", Limit = 10 },
            TestContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("pi-artifact:1", result!.SessionId);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("bash", result.Items[0].Tool);
    }

    [Fact]
    public async Task TerminalReadActivityHandler_ThrowsForUnknownSession()
    {
        var registry = new OperatorSessionRegistry();
        var handler = new TerminalReadActivityHandler(registry);

        var ex = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            handler.HandleAsync(
                new TerminalReadActivityRequest { SessionId = "nonexistent" },
                TestContext(),
                CancellationToken.None).AsTask());

        Assert.Equal("terminal.session.not_found", ex.Code);
        Assert.Equal("not_found", ex.Category);
    }

    [Fact]
    public async Task TerminalReadActivityHandler_ThrowsForNoReadActivityCapability()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        registry.Register(new OperatorSession
        {
            SessionId = "blocked",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.FullControl() with { CanReadActivity = false },
            CreatedAt = now,
        });

        var handler = new TerminalReadActivityHandler(registry);
        var ex = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            handler.HandleAsync(
                new TerminalReadActivityRequest { SessionId = "blocked" },
                TestContext(),
                CancellationToken.None).AsTask());

        Assert.Equal("terminal.read_activity.unsupported", ex.Code);
        Assert.Equal("unsupported_capability", ex.Category);
    }

    [Theory]
    [InlineData("attach")]
    [InlineData("detach")]
    [InlineData("send_input")]
    [InlineData("resize")]
    [InlineData("terminate")]
    [InlineData("reconnect")]
    [InlineData("ack_output")]
    public async Task TerminalControlStubs_ReturnUnsupportedCapability(string action)
    {
        var ex = await ActStubAsync(action);
        Assert.NotNull(ex);
        Assert.Equal("unsupported_capability", ex!.Category);
    }

    private static async Task<BridgeHandlerException?> ActStubAsync(string action)
    {
        var ctx = TestContext();
        try
        {
            switch (action)
            {
                case "attach":
                    await new TerminalAttachHandler().HandleAsync(
                        new TerminalAttachRequest { SessionId = "test" }, ctx, CancellationToken.None);
                    break;
                case "detach":
                    await new TerminalDetachHandler().HandleAsync(
                        new TerminalDetachRequest { StreamId = "stream", SessionId = "test" }, ctx, CancellationToken.None);
                    break;
                case "send_input":
                    await new TerminalSendInputHandler().HandleAsync(
                        new TerminalSendInputRequest { SessionId = "test", Data = "echo ok" }, ctx, CancellationToken.None);
                    break;
                case "resize":
                    await new TerminalResizeHandler().HandleAsync(
                        new TerminalResizeRequest { SessionId = "test", Cols = 80, Rows = 24 }, ctx, CancellationToken.None);
                    break;
                case "terminate":
                    await new TerminalTerminateHandler().HandleAsync(
                        new TerminalTerminateRequest { SessionId = "test" }, ctx, CancellationToken.None);
                    break;
                case "reconnect":
                    await new TerminalReconnectHandler().HandleAsync(
                        new TerminalReconnectRequest { SessionId = "test" }, ctx, CancellationToken.None);
                    break;
                case "ack_output":
                    await new TerminalAckOutputHandler().HandleAsync(
                        new TerminalAckOutputRequest { SessionId = "test" }, ctx, CancellationToken.None);
                    break;
                default:
                    return null;
            }
        }
        catch (BridgeHandlerException ex)
        {
            return ex;
        }

        return null;
    }

    private static BridgeRequestContext TestContext()
    {
        return new BridgeRequestContext("req_test", BridgeCorrelation.Empty, (_, _) => ValueTask.CompletedTask);
    }
}

public class TerminalBridgeDtosSerializationTests
{
    [Fact]
    public void TerminalAttachRequest_SerializesRoundTrip()
    {
        var request = new TerminalAttachRequest
        {
            SessionId = "pty:test-1",
            Mode = "terminal_stream",
            Viewport = new TerminalViewport { Cols = 120, Rows = 32 },
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxBytes = 65536, MaxChunks = 20 },
            ClientId = "test-client",
        };

        var json = BridgeJson.Serialize(request);
        Assert.Contains("pty:test-1", json, StringComparison.Ordinal);
        Assert.Contains("terminal_stream", json, StringComparison.Ordinal);
        Assert.Contains("120", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalAttachResponse_IncludesViewportLimits_PerR945_1()
    {
        var response = new TerminalAttachResponse
        {
            StreamId = "stream_01h",
            SessionId = "pty:test-1",
            AttachedAt = "2026-04-29T00:00:00.000Z",
            StartCursor = "cur_000000000001",
            ReplayAvailableFrom = "cur_000000000001",
            Capabilities = new TerminalAttachCapabilities { CanSendInput = true },
            ViewportLimits = new TerminalViewportLimits { MinCols = 1, MaxCols = 500, MinRows = 1, MaxRows = 500 },
            Limits = new TerminalStreamLimits(),
        };

        var json = BridgeJson.Serialize(response);
        Assert.Contains("viewport_limits", json, StringComparison.Ordinal);
        Assert.Contains("\"max_cols\":500", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalSendInputRequest_SupportsBase64Encoding()
    {
        var request = new TerminalSendInputRequest
        {
            SessionId = "pty:test-1",
            StreamId = "stream_01h",
            InputId = "in_01h",
            Encoding = "base64",
            Data = "SGVsbG8=",
            ByteCount = 5,
        };

        var json = BridgeJson.Serialize(request);
        Assert.Contains("base64", json, StringComparison.Ordinal);
        Assert.Contains("SGVsbG8=", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalListSessionsResponse_Serializes()
    {
        var response = new TerminalListSessionsResponse
        {
            Sessions =
            [
                new TerminalSessionSummary
                {
                    SessionId = "pty:test-1",
                    Title = "test session",
                    Kind = "terminal",
                    Backend = "direct_pty",
                    Status = "running",
                    CanReadActivity = true,
                    CanSendInput = true,
                    CanAttach = true,
                },
            ],
            Count = 1,
        };

        var json = BridgeJson.Serialize(response);
        Assert.Contains("pty:test-1", json, StringComparison.Ordinal);
        Assert.Contains("direct_pty", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalAttachResponse_RoundTripsFullPayload()
    {
        var response = new TerminalAttachResponse
        {
            StreamId = "stream_01h",
            SessionId = "pty:test-1",
            AttachedAt = "2026-04-29T00:00:00.000Z",
            StartCursor = "cur_000000000001",
            ReplayAvailableFrom = "cur_000000000001",
            ReplayGap = false,
            Capabilities = new TerminalAttachCapabilities
            {
                CanSendInput = true,
                CanResize = true,
                CanDetach = false,
                CanTerminate = true,
                CanStreamTerminal = true,
            },
            ViewportLimits = new TerminalViewportLimits { MinCols = 1, MaxCols = 500, MinRows = 1, MaxRows = 500 },
            Limits = new TerminalStreamLimits
            {
                OutputChunkMaxBytes = 65536,
                InputChunkMaxBytes = 16384,
                SessionReplayMaxBytes = 1048576,
                SubscriberQueueMaxBytes = 262144,
                AckAfterBytes = 262144,
                HeartbeatIntervalMs = 5000,
            },
        };

        var json = BridgeJson.Serialize(response);
        Assert.Contains("stream_01h", json, StringComparison.Ordinal);
        Assert.Contains("viewport_limits", json, StringComparison.Ordinal);
        Assert.Contains("\"max_cols\":500", json, StringComparison.Ordinal);
        Assert.Contains("\"min_rows\":1", json, StringComparison.Ordinal);
        Assert.Contains("capabilities", json, StringComparison.Ordinal);
        Assert.Contains("\"can_send_input\":true", json, StringComparison.Ordinal);
        Assert.Contains("limits", json, StringComparison.Ordinal);
        Assert.Contains("\"heartbeat_interval_ms\":5000", json, StringComparison.Ordinal);

        // Roundtrip: deserialize back
        var deserialized = BridgeJson.Deserialize<TerminalAttachResponse>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(response.StreamId, deserialized!.StreamId);
        Assert.Equal(response.SessionId, deserialized.SessionId);
        Assert.Equal(response.Capabilities.CanSendInput, deserialized.Capabilities.CanSendInput);
        Assert.Equal(response.Capabilities.CanStreamTerminal, deserialized.Capabilities.CanStreamTerminal);
        Assert.NotNull(deserialized.ViewportLimits);
        Assert.Equal(500, deserialized.ViewportLimits!.MaxCols);
        Assert.NotNull(deserialized.Limits);
        Assert.Equal(5000, deserialized.Limits.HeartbeatIntervalMs);
    }

    [Fact]
    public void TerminalReconnect_ReturnsAttachResponse()
    {
        // Reconnect handler returns TerminalAttachResponse (same schema)
        var attachResponse = new TerminalAttachResponse
        {
            StreamId = "reconnect_stream",
            SessionId = "pty:reconnect-1",
            AttachedAt = "2026-04-29T00:00:00.000Z",
            StartCursor = "cur_000000000005",
            Capabilities = new TerminalAttachCapabilities { CanSendInput = true, CanStreamTerminal = true },
            Limits = new TerminalStreamLimits(),
        };

        var json = BridgeJson.Serialize(attachResponse);
        Assert.Contains("reconnect_stream", json, StringComparison.Ordinal);

        var deserialized = BridgeJson.Deserialize<TerminalAttachResponse>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(attachResponse.StreamId, deserialized!.StreamId);
    }
}

public class TerminalProtocolConformanceFixtureTests
{
    /// <summary>
    /// Executable conformance fixture for the #945 terminal protocol.
    /// Tests the contract that #909/#911 backends must satisfy.
    /// </summary>
    [Fact]
    public void ConformanceFixture_AttachReplayInputResizeExit_Contract()
    {
        // This is the deterministic schema/test equivalent of the #945
        // conformance fixture for later backend tasks.

        // 1. Session with full capabilities
        var session = new OperatorSession
        {
            SessionId = "pty:fixture-1",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = new OperatorSessionCapabilities
            {
                CanAttach = true,
                CanDetach = true,
                CanSendInput = true,
                CanResize = true,
                CanTerminate = true,
                CanReconnect = true,
                CanStreamTerminal = true,
                CanReadActivity = true,
            },
            CreatedAt = DateTime.UtcNow,
        };

        Assert.True(session.Capabilities.CanAttach);
        Assert.True(session.Capabilities.CanSendInput);
        Assert.True(session.Capabilities.CanResize);
        Assert.True(session.Capabilities.CanTerminate);

        // 2. Attach request/response contract
        var attachRequest = new TerminalAttachRequest
        {
            SessionId = "pty:fixture-1",
            Mode = "terminal_stream",
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxBytes = 65536, MaxChunks = 20 },
            ClientId = "test",
        };
        Assert.Equal("pty:fixture-1", attachRequest.SessionId);
        Assert.Equal("terminal_stream", attachRequest.Mode);

        var attachResponse = new TerminalAttachResponse
        {
            StreamId = "stream_fixture_1",
            SessionId = "pty:fixture-1",
            AttachedAt = "2026-04-29T00:00:00.000Z",
            StartCursor = "cur_000000000001",
            ReplayAvailableFrom = "cur_000000000001",
            ReplayGap = false,
            Capabilities = new TerminalAttachCapabilities { CanSendInput = true, CanResize = true, CanDetach = true, CanTerminate = true, CanStreamTerminal = true },
            Limits = new TerminalStreamLimits(),
        };
        Assert.Equal("pty:fixture-1", attachResponse.SessionId);
        Assert.False(attachResponse.ReplayGap);

        // 3. Send input contract
        var inputRequest = new TerminalSendInputRequest
        {
            SessionId = "pty:fixture-1",
            StreamId = "stream_fixture_1",
            InputId = "in_fixture_1",
            Encoding = "utf8",
            Data = "echo ok\n",
            ByteCount = 8,
        };
        Assert.Equal(8, inputRequest.ByteCount);

        // 4. Resize contract (R945-1: viewport limits)
        var resizeRequest = new TerminalResizeRequest
        {
            SessionId = "pty:fixture-1",
            StreamId = "stream_fixture_1",
            Cols = 100,
            Rows = 30,
        };
        Assert.InRange(resizeRequest.Cols, 1, 500);
        Assert.InRange(resizeRequest.Rows, 1, 500);

        // 5. Output chunk contract (R945-2: chunk max)
        var buffer = new OperatorSessionActivityBuffer(outputChunkMaxBytes: 65536);
        var largeData = new byte[131072]; // 128 KiB
        var chunks = buffer.Append(largeData);
        Assert.All(chunks, c => Assert.True(c.ByteCount <= 65536));
        // 131072 / 65536 = 2 chunks (exactly)
        Assert.Equal(2, chunks.Count);

        // 6. Backpressure limits (R945-3)
        Assert.True(buffer.MaxQueuedSubscriberBytes > 0);
        Assert.Equal(262144, buffer.MaxQueuedSubscriberBytes);

        // 7. Dotted-name convention (R945-4)
        Assert.StartsWith("den.terminal.", DesktopSidecarProtocol.TerminalOutputEvent, StringComparison.Ordinal);
        Assert.StartsWith("den.terminal.", DesktopSidecarProtocol.TerminalHeartbeatEvent, StringComparison.Ordinal);
        Assert.StartsWith("den.terminal.", DesktopSidecarProtocol.TerminalExitEvent, StringComparison.Ordinal);
        Assert.StartsWith("den.terminal.", DesktopSidecarProtocol.TerminalSessionStatusEvent, StringComparison.Ordinal);

        // 8. Negative cases: unsupported capability for control stubs
        Assert.StartsWith("unsupported", TerminalErrorResult.Unsupported("send_input", "no backend").Category, StringComparison.Ordinal);
    }
}
