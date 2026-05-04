using System.Reflection;
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
    public void Registry_RegisterUsesRegistryAuthoritativeUpdatedAt()
    {
        var clock = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var observedAt = new DateTime(2026, 4, 29, 11, 59, 0, DateTimeKind.Utc);
        var callerUpdatedAt = new DateTime(2026, 4, 29, 10, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => clock);

        var session = registry.Register(new OperatorSession
        {
            SessionId = "pty:timestamp-policy",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.DirectPty,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.FullControl(),
            CreatedAt = callerUpdatedAt,
            LastObservedAt = observedAt,
            LastActivityAt = observedAt,
            UpdatedAt = callerUpdatedAt,
        });

        Assert.Equal(clock, session.UpdatedAt);
        Assert.Equal(observedAt, session.LastObservedAt);
        Assert.Equal(observedAt, session.LastActivityAt);
    }

    [Fact]
    public void Registry_RegisterFromPiSnapshotPreservesObservedAtButUpdatesRegistryTimestamp()
    {
        var clock = new DateTime(2026, 4, 29, 12, 5, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => clock);
        var snapshot = new LocalSessionSnapshot
        {
            ProjectId = "den-mcp",
            ArtifactRoot = "/tmp/runs/run-timestamp",
            Request = new DesktopSessionSnapshotRequest
            {
                SessionId = "pi-artifact:timestamp",
                Title = "timestamp",
                Kind = "artifact_observer",
                Backend = "pi_artifact",
                CurrentPhase = "running",
                StartedAt = "2026-04-29T11:00:00.000Z",
                ObservedAt = "2026-04-29T12:00:00.000Z",
                LastActivityAt = "2026-04-29T11:59:00.000Z",
                SourceInstanceId = "desktop-test",
                RecentActivity = JsonSerializer.SerializeToElement(new { items = Array.Empty<object>() }),
                Capabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities_v2" }),
                ControlCapabilities = JsonSerializer.SerializeToElement(new { schema = "den_desktop_session_capabilities" }),
                Warnings = [],
            },
        };

        var session = registry.RegisterFromPiSnapshot(snapshot);

        Assert.Equal(clock, session.UpdatedAt);
        Assert.Equal(new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc), session.LastObservedAt);
        Assert.Equal(new DateTime(2026, 4, 29, 11, 59, 0, DateTimeKind.Utc), session.LastActivityAt);
        Assert.Equal(new DateTime(2026, 4, 29, 11, 0, 0, DateTimeKind.Utc), session.CreatedAt);
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
    public async Task TerminalReadActivityHandler_UsesStableActivityCursorAcrossRegistryRefresh()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var registry = new OperatorSessionRegistry(() => now);
        var session = registry.Register(new OperatorSession
        {
            SessionId = "pi-artifact:cursor",
            Kind = OperatorSessionKind.ArtifactObserver,
            Backend = OperatorSessionBackend.PiArtifact,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.ObserveOnly("test", canReadActivity: true),
            CreatedAt = now,
            RecentActivity =
            [
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "A", Timestamp = "2026-04-29T11:59:00Z" },
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "B", Timestamp = "2026-04-29T11:59:01Z" },
            ],
        });

        var handler = new TerminalReadActivityHandler(registry);
        var first = await handler.HandleAsync(
            new TerminalReadActivityRequest { SessionId = session.SessionId, Limit = 1 },
            TestContext(),
            CancellationToken.None);

        Assert.NotNull(first!.NextCursor);
        Assert.StartsWith(OperatorSessionActivityReader.CursorPrefix, first.NextCursor, StringComparison.Ordinal);

        registry.Register(session with
        {
            RecentActivity =
            [
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "prepended", Timestamp = "2026-04-29T11:58:59Z" },
                session.RecentActivity[0],
                session.RecentActivity[1],
            ],
        });

        var next = await handler.HandleAsync(
            new TerminalReadActivityRequest { SessionId = session.SessionId, AfterCursor = first.NextCursor, Limit = 10 },
            TestContext(),
            CancellationToken.None);

        Assert.Single(next!.Items);
        Assert.Equal("B", next.Items[0].Summary);
        Assert.False(next.Truncated);
    }

    [Fact]
    public void AppAgentReadActivity_UsesSameStableActivityCursorSemantics()
    {
        var now = new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc);
        var session = new OperatorSession
        {
            SessionId = "agent:cursor",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Process,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "test",
            Capabilities = OperatorSessionCapabilities.ObserveOnly("test", canReadActivity: true),
            CreatedAt = now,
            RecentActivity =
            [
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "A", Timestamp = "2026-04-29T11:59:00Z" },
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "B", Timestamp = "2026-04-29T11:59:01Z" },
            ],
        };

        var first = AppAgentContextBuilder.ReadActivity(session, null, 1);
        var refreshed = session with
        {
            RecentActivity =
            [
                new OperatorSessionActivityItem { Kind = "tool", Tool = "bash", Summary = "prepended", Timestamp = "2026-04-29T11:58:59Z" },
                session.RecentActivity[0],
                session.RecentActivity[1],
            ],
        };

        var next = AppAgentContextBuilder.ReadActivity(refreshed, first.NextCursor, 10);

        Assert.Single(next.Items);
        Assert.Equal("B", next.Items[0].Summary);
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

    [Fact]
    public async Task TerminalCreateSessionHandler_CreatesTmuxSessionAndRegistersSummary()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var handler = new TerminalCreateSessionHandler(CreateTerminalService(runner, registry));

        var result = await handler.HandleAsync(
            new TerminalCreateSessionRequest
            {
                ProjectId = "den-mcp",
                TaskId = 909,
                WorkspaceId = "ws-1",
                Title = "Task 909",
                Cwd = "/tmp/work",
            },
            TestContext(),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.StartsWith("tmux-session:", result!.Session.SessionId, StringComparison.Ordinal);
        Assert.Equal(OperatorSessionBackend.Tmux, result.Session.Backend);
        Assert.Equal("tmux", result.Session.PersistenceKind);
        Assert.Equal("backend_persistent", result.Session.OwnershipKind);
        Assert.True(result.Session.CanOpenExternalAttach);
        Assert.Contains(runner.Calls, call => call.Args[0] == "new-session" && call.Args.Contains("-s"));
        Assert.Contains(runner.Calls, call => call.Args[0] == "set-option" && call.Args.Contains("@den.project_id"));
    }

    [Fact]
    public async Task TmuxAttachExternalInfo_ReturnsOpaqueAttachCommandWithoutRawStream()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var service = CreateTmuxService(runner, registry);
        var session = await service.CreateAsync(new TerminalCreateSessionRequest { ProjectId = "den-mcp", Title = "External" }, CancellationToken.None);

        var response = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, Mode = "external_attach_info" }, CancellationToken.None);

        Assert.Equal(session.SessionId, response.SessionId);
        Assert.Equal(string.Empty, response.StreamId);
        Assert.NotNull(response.ExternalAttach);
        Assert.True(response.ExternalAttach!.Available);
        Assert.Contains("tmux attach-session", response.ExternalAttach.Command, StringComparison.Ordinal);
        Assert.Contains("Display/copy-only", response.ExternalAttach.Description, StringComparison.Ordinal);
        Assert.Contains("must not auto-execute", response.ExternalAttach.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.Calls, call => call.Args[0] == "capture-pane");
        Assert.Equal(0, GetTrackedTmuxStreamCount(service));
    }

    [Fact]
    public async Task TmuxAttachExternalInfo_RepeatedRequestsDoNotCreateTrackedStreams()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var service = CreateTmuxService(runner, registry);
        var session = await service.CreateAsync(new TerminalCreateSessionRequest { ProjectId = "den-mcp", Title = "Repeated External" }, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var response = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, Mode = "external_attach_info" }, CancellationToken.None);

            Assert.Equal(session.SessionId, response.SessionId);
            Assert.Equal(string.Empty, response.StreamId);
            Assert.NotNull(response.ExternalAttach);
            Assert.Contains("must not auto-execute", response.ExternalAttach!.Description, StringComparison.Ordinal);
        }

        Assert.Equal(0, GetTrackedTmuxStreamCount(service));
        Assert.DoesNotContain(runner.Calls, call => call.Args[0] == "capture-pane");

        var stream = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, Mode = "terminal_stream" }, CancellationToken.None);
        Assert.Equal(1, GetTrackedTmuxStreamCount(service));

        await service.DetachAsync(new TerminalDetachRequest { SessionId = session.SessionId, StreamId = stream.StreamId }, CancellationToken.None);
        Assert.Equal(0, GetTrackedTmuxStreamCount(service));
    }

    /// <summary>
    /// Contract test: session.external_attach_info_requested event payload must be
    /// { mode, raw_stream } with no stream_id field. Verifies #1036 no-stream
    /// lifecycle behavior for external_attach_info mode.
    /// </summary>
    [Fact]
    public async Task TmuxAttachExternalInfo_EventPayloadHasModeAndRawStreamWithoutStreamId()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var recordingHandler = new RecordingDelegatingHandler();
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        var service = new TmuxOperatorSessionService(
            runner,
            registry,
            events,
            settings,
            new DenHttpClient(new HttpClient(recordingHandler)),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        var session = await service.CreateAsync(new TerminalCreateSessionRequest { ProjectId = "den-mcp", Title = "Payload Contract" }, CancellationToken.None);
        recordingHandler.Clear();

        var response = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, Mode = "external_attach_info" }, CancellationToken.None);

        Assert.Equal(string.Empty, response.StreamId);
        Assert.Equal(0, GetTrackedTmuxStreamCount(service));

        // Find the session.external_attach_info_requested event in recorded Den API calls.
        var eventRequest = recordingHandler.SentRequests
            .FirstOrDefault(r => r.RelativeUri.Contains("session-events", StringComparison.Ordinal)
                && r.Body.TryGetProperty("event_type", out var et)
                && et.GetString() == "session.external_attach_info_requested");

        Assert.True(eventRequest.Body.ValueKind != JsonValueKind.Undefined,
            "Expected a session.external_attach_info_requested event to be published to Den.");

        // The request body payload field is a JSON string (serialized payload), not a nested object.
        Assert.True(eventRequest.Body.TryGetProperty("payload", out var payloadStringElement),
            "Event request must include a payload field.");
        Assert.Equal(JsonValueKind.String, payloadStringElement.ValueKind);

        var payload = JsonSerializer.Deserialize<JsonElement>(payloadStringElement.GetString()!);

        Assert.True(payload.TryGetProperty("mode", out var modeProp),
            "Payload must include 'mode' field.");
        Assert.Equal("external_attach_info", modeProp.GetString());

        Assert.True(payload.TryGetProperty("raw_stream", out var rawStreamProp),
            "Payload must include 'raw_stream' field.");
        Assert.False(rawStreamProp.GetBoolean());

        // Explicitly verify stream_id is absent — this is the key contract.
        Assert.False(payload.TryGetProperty("stream_id", out _),
            "Payload must NOT include 'stream_id' for external_attach_info mode (no tracked stream is created).");
    }

    [Fact]
    public async Task TmuxAttachTerminalStream_AppliesViewportBeforeCaptureReplay()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var service = CreateTmuxService(runner, registry);
        var session = await service.CreateAsync(new TerminalCreateSessionRequest { ProjectId = "den-mcp", Title = "Viewport" }, CancellationToken.None);

        var response = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Mode = "terminal_stream",
            Viewport = new TerminalViewport { Cols = 132, Rows = 43 },
        }, CancellationToken.None);

        Assert.Equal(session.SessionId, response.SessionId);
        var resizeIndex = runner.Calls.FindIndex(call => call.Args[0] == "resize-window");
        var captureIndex = runner.Calls.FindIndex(call => call.Args[0] == "capture-pane");
        Assert.InRange(resizeIndex, 0, int.MaxValue);
        Assert.InRange(captureIndex, 0, int.MaxValue);
        Assert.True(resizeIndex < captureIndex);
        Assert.Contains(runner.Calls[resizeIndex].Args, arg => arg == "-x");
        Assert.Contains("132", runner.Calls[resizeIndex].Args);
        Assert.Contains("43", runner.Calls[resizeIndex].Args);
        Assert.Contains("-43", runner.Calls[captureIndex].Args);
        Assert.Contains("tmux_capture_replay", registry.Get(session.SessionId)!.Capabilities.Constraints, StringComparison.Ordinal);
        Assert.Contains("display_copy_only", registry.Get(session.SessionId)!.Capabilities.Constraints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPtyService_CreateAttachInputResizeExit_UsesBackendNeutralProtocol()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        var service = new DirectPtyOperatorSessionService(
            backend,
            registry,
            events,
            settings,
            new DenHttpClient(),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = "den-mcp",
            Title = "Direct",
            Cwd = "/tmp/work",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Viewport = new TerminalViewport { Cols = 100, Rows = 30 },
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxChunks = 20 },
        }, CancellationToken.None);

        Assert.StartsWith("pty:", session.SessionId, StringComparison.Ordinal);
        Assert.Equal(OperatorSessionBackend.DirectPty, session.Backend);
        Assert.True(session.Capabilities.CanStreamTerminal);
        Assert.Equal(session.SessionId, attach.SessionId);
        Assert.Equal(100, backend.Processes[0].Resizes[0].Cols);

        await service.SendInputAsync(new TerminalSendInputRequest
        {
            SessionId = session.SessionId,
            StreamId = attach.StreamId,
            InputId = "in_test",
            Data = "echo ok\n",
            ByteCount = 8,
        }, CancellationToken.None);
        Assert.Equal("echo ok\n", Encoding.UTF8.GetString(backend.Processes[0].Writes[0]));

        await service.ResizeAsync(new TerminalResizeRequest { SessionId = session.SessionId, StreamId = attach.StreamId, Cols = 120, Rows = 40 }, CancellationToken.None);
        Assert.Contains(backend.Processes[0].Resizes, resize => resize is { Cols: 120, Rows: 40 });

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("hello\n"));
        await Task.Delay(50);
        Assert.Contains(events.PublishedFrames, frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent);

        await service.TerminateAsync(new TerminalTerminateRequest { SessionId = session.SessionId, StreamId = attach.StreamId, Mode = "kill" }, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(OperatorSessionStatus.Exited, registry.Get(session.SessionId)!.Status);
        Assert.Contains(events.PublishedFrames, frame => frame.Event == DesktopSidecarProtocol.TerminalExitEvent);
    }

    [Fact]
    public async Task DirectPtyService_SplitsOversizedBackendOutputForLiveAndReplay()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits
            {
                OutputChunkMaxBytes = 4,
                AckAfterBytes = 1024,
                AckAfterMillis = 5000,
                HeartbeatIntervalMs = 5000,
            });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Oversized direct output",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId }, CancellationToken.None);

        var liveStart = events.PublishedFrames.Count;
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("abcdefghij"));
        var liveEvents = await WaitForTerminalOutputEventsAsync(events, attach.StreamId, expectedCount: 3, startIndex: liveStart);

        Assert.All(liveEvents, output => Assert.True(output.ByteCount <= 4));
        Assert.Equal([1L, 2L, 3L], liveEvents.Select(output => output.TerminalSequence).ToArray());
        Assert.Equal(["cur_000000000001", "cur_000000000002", "cur_000000000003"], liveEvents.Select(output => output.StreamCursor).ToArray());
        Assert.Equal(["chunk_000000000001", "chunk_000000000002", "chunk_000000000003"], liveEvents.Select(output => output.ChunkId).ToArray());
        Assert.Equal(["abcd", "efgh", "ij"], liveEvents.Select(output => Encoding.UTF8.GetString(Convert.FromBase64String(output.Data))).ToArray());
        Assert.Equal([true, true, false], liveEvents.Select(output => output.Truncated).ToArray());
        Assert.All(liveEvents, output => Assert.Equal("live", output.Origin));

        await service.DetachAsync(new TerminalDetachRequest { SessionId = session.SessionId, StreamId = attach.StreamId }, CancellationToken.None);

        var replayStart = events.PublishedFrames.Count;
        var reconnect = await service.ReconnectAsync(new TerminalReconnectRequest
        {
            SessionId = session.SessionId,
            LastSeenCursor = "cur_000000000001",
        }, CancellationToken.None);
        var replayEvents = await WaitForTerminalOutputEventsAsync(events, reconnect.StreamId, expectedCount: 2, startIndex: replayStart);

        Assert.False(reconnect.ReplayGap);
        Assert.Equal("cur_000000000001", reconnect.ReplayAvailableFrom);
        Assert.Equal("cur_000000000003", reconnect.StartCursor);
        Assert.All(replayEvents, output => Assert.True(output.ByteCount <= 4));
        Assert.Equal([2L, 3L], replayEvents.Select(output => output.TerminalSequence).ToArray());
        Assert.Equal(["cur_000000000002", "cur_000000000003"], replayEvents.Select(output => output.StreamCursor).ToArray());
        Assert.Equal(["efgh", "ij"], replayEvents.Select(output => Encoding.UTF8.GetString(Convert.FromBase64String(output.Data))).ToArray());
        Assert.Equal([true, false], replayEvents.Select(output => output.Truncated).ToArray());
        Assert.All(replayEvents, output => Assert.Equal("replay", output.Origin));
    }

    [Fact]
    public async Task DirectPtyService_AttachPublishesHeartbeatUntilDetached()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        await using var service = new DirectPtyOperatorSessionService(
            backend,
            registry,
            events,
            settings,
            new DenHttpClient(),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
            new TerminalStreamLimits { HeartbeatIntervalMs = 10 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = "den-mcp",
            Title = "Heartbeat",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
        }, CancellationToken.None);

        var heartbeatFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal));
        var heartbeat = JsonSerializer.Deserialize<TerminalHeartbeatEvent>(heartbeatFrame.Payload.GetRawText());

        Assert.NotNull(heartbeat);
        Assert.Equal(session.SessionId, heartbeat!.SessionId);
        Assert.Equal(attach.StreamId, heartbeat.StreamId);
        Assert.Equal(OperatorSessionStatus.Running, heartbeat.BackendStatus);
        Assert.Equal("cur_000000000000", heartbeat.StreamCursor);
        Assert.Equal(0, heartbeat.QueueBytes);
        Assert.False(heartbeat.Paused);

        await service.DetachAsync(new TerminalDetachRequest { SessionId = session.SessionId, StreamId = attach.StreamId, Reason = "test" }, CancellationToken.None);
        var heartbeatCountAfterDetach = events.PublishedFrames.Count(frame => frame.Event == DesktopSidecarProtocol.TerminalHeartbeatEvent);
        await Task.Delay(50);
        Assert.Equal(heartbeatCountAfterDetach, events.PublishedFrames.Count(frame => frame.Event == DesktopSidecarProtocol.TerminalHeartbeatEvent));
    }

    [Fact]
    public async Task DirectPtyService_EmitsBackpressureUntilAckResetsUnackedBytes()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        await using var service = new DirectPtyOperatorSessionService(
            backend,
            registry,
            events,
            settings,
            new DenHttpClient(),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
            new TerminalStreamLimits { AckAfterBytes = 8, SubscriberQueueMaxBytes = 16, HeartbeatIntervalMs = 10 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = "den-mcp",
            Title = "Backpressure",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
        }, CancellationToken.None);

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("0123456789"));

        var backpressureFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal));
        var backpressure = JsonSerializer.Deserialize<TerminalBackpressureEvent>(backpressureFrame.Payload.GetRawText());

        Assert.NotNull(backpressure);
        Assert.Equal("throttled", backpressure!.State);
        Assert.Equal("ack_required", backpressure.NextAction);
        Assert.True(backpressure.QueueBytes >= 10);

        var pausedHeartbeatFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("paused").GetBoolean());
        var pausedHeartbeat = JsonSerializer.Deserialize<TerminalHeartbeatEvent>(pausedHeartbeatFrame.Payload.GetRawText());
        Assert.True(pausedHeartbeat!.Paused);
        Assert.True(pausedHeartbeat.QueueBytes >= 10);

        var frameCountBeforeAck = events.PublishedFrames.Count;
        var ack = await service.AckOutputAsync(new TerminalAckOutputRequest
        {
            SessionId = session.SessionId,
            StreamId = attach.StreamId,
            AckCursor = pausedHeartbeat.StreamCursor,
            ReceivedBytes = pausedHeartbeat.QueueBytes,
        }, CancellationToken.None);

        Assert.True(ack.Accepted);

        var resumedHeartbeatFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && !frame.Payload.GetProperty("paused").GetBoolean()
                && frame.Payload.GetProperty("queue_bytes").GetInt32() == 0,
            startIndex: frameCountBeforeAck);
        var resumedHeartbeat = JsonSerializer.Deserialize<TerminalHeartbeatEvent>(resumedHeartbeatFrame.Payload.GetRawText());
        Assert.False(resumedHeartbeat!.Paused);
        Assert.Equal(0, resumedHeartbeat.QueueBytes);
    }

    [Fact]
    public async Task DirectPtyService_EmitsTimeBackpressureForSlowUnackedOutputBelowByteThreshold()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { AckAfterBytes = 1024, AckAfterMillis = 30, SubscriberQueueMaxBytes = 2048, HeartbeatIntervalMs = 10 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Timed backpressure",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId }, CancellationToken.None);

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("a"));
        await Task.Delay(10);
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("b"));

        var backpressureFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("queue_bytes").GetInt32() == 2);
        var backpressure = JsonSerializer.Deserialize<TerminalBackpressureEvent>(backpressureFrame.Payload.GetRawText());

        Assert.NotNull(backpressure);
        Assert.Equal("throttled", backpressure!.State);
        Assert.Equal("ack_required", backpressure.NextAction);
        Assert.Equal(2, backpressure.QueueBytes);

        var pausedHeartbeatFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("paused").GetBoolean()
                && frame.Payload.GetProperty("queue_bytes").GetInt32() == 2);
        var pausedHeartbeat = JsonSerializer.Deserialize<TerminalHeartbeatEvent>(pausedHeartbeatFrame.Payload.GetRawText());
        Assert.True(pausedHeartbeat!.Paused);
        Assert.Equal(2, pausedHeartbeat.QueueBytes);
    }

    [Fact]
    public async Task DirectPtyService_AckCancelsTimeBackpressureTimerAndAllowsRecovery()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { AckAfterBytes = 1024, AckAfterMillis = 50, SubscriberQueueMaxBytes = 2048, HeartbeatIntervalMs = 10 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Timed ack recovery",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId }, CancellationToken.None);

        var firstWindowStart = events.PublishedFrames.Count;
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("abc"));
        await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalOutputEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal),
            startIndex: firstWindowStart);

        var ack = await service.AckOutputAsync(new TerminalAckOutputRequest
        {
            SessionId = session.SessionId,
            StreamId = attach.StreamId,
            AckCursor = "cur_000000000001",
            ReceivedBytes = 3,
        }, CancellationToken.None);
        Assert.True(ack.Accepted);

        await Task.Delay(100);
        Assert.DoesNotContain(events.PublishedFrames.Skip(firstWindowStart), frame =>
            frame.Event == DesktopSidecarProtocol.TerminalBackpressureEvent
            && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal));

        var recoveryStart = events.PublishedFrames.Count;
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("de"));
        var recoveryBackpressureFrame = await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("queue_bytes").GetInt32() == 2,
            startIndex: recoveryStart);
        var recoveryBackpressure = JsonSerializer.Deserialize<TerminalBackpressureEvent>(recoveryBackpressureFrame.Payload.GetRawText());
        Assert.Equal(2, recoveryBackpressure!.QueueBytes);

        var frameCountBeforeRecoveryAck = events.PublishedFrames.Count;
        await service.AckOutputAsync(new TerminalAckOutputRequest
        {
            SessionId = session.SessionId,
            StreamId = attach.StreamId,
            AckCursor = "cur_000000000002",
            ReceivedBytes = 2,
        }, CancellationToken.None);

        await WaitForPublishedFrameAsync(
            events,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), attach.StreamId, StringComparison.Ordinal)
                && !frame.Payload.GetProperty("paused").GetBoolean()
                && frame.Payload.GetProperty("queue_bytes").GetInt32() == 0,
            startIndex: frameCountBeforeRecoveryAck);
    }

    [Fact]
    public async Task DirectPtyService_DetachPreservesBackendAndReattachCanReplayAndControl()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(backend, registry, events);

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Detach",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var firstAttach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
        }, CancellationToken.None);

        var detached = await service.DetachAsync(new TerminalDetachRequest
        {
            SessionId = session.SessionId,
            StreamId = firstAttach.StreamId,
            Reason = "test_detach",
        }, CancellationToken.None);

        Assert.True(detached.Detached);
        Assert.True(detached.BackendPreserved);
        Assert.False(backend.Processes[0].HasExited);

        var outputCountAfterDetach = events.PublishedFrames.Count(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent);
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("detached output"));
        await WaitForActivityCountAsync(registry, session.SessionId, 1);
        Assert.Equal(outputCountAfterDetach, events.PublishedFrames.Count(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent));

        var staleStreamError = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.SendInputAsync(new TerminalSendInputRequest
            {
                SessionId = session.SessionId,
                StreamId = firstAttach.StreamId,
                Data = "should fail",
                ByteCount = 11,
            }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", staleStreamError.Code);

        var secondAttach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxChunks = 10 },
        }, CancellationToken.None);
        var replayFrame = events.PublishedFrames.Last(frame =>
            frame.Event == DesktopSidecarProtocol.TerminalOutputEvent
            && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), secondAttach.StreamId, StringComparison.Ordinal));
        var replay = JsonSerializer.Deserialize<TerminalOutputEvent>(replayFrame.Payload.GetRawText());
        Assert.NotNull(replay);
        Assert.Equal("replay", replay!.Origin);
        Assert.Equal("detached output", Encoding.UTF8.GetString(Convert.FromBase64String(replay.Data)));

        var input = await service.SendInputAsync(new TerminalSendInputRequest
        {
            SessionId = session.SessionId,
            StreamId = secondAttach.StreamId,
            InputId = "in_after_reattach",
            Data = "echo after\n",
            ByteCount = 11,
        }, CancellationToken.None);
        Assert.True(input.Accepted);
        Assert.Equal("echo after\n", Encoding.UTF8.GetString(backend.Processes[0].Writes.Single()));
    }

    [Fact]
    public async Task DirectPtyService_ActivityOnlyAttachSuppressesTerminalReplayChunks()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        var recordingHandler = new RecordingDelegatingHandler();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        await using var service = new DirectPtyOperatorSessionService(
            backend,
            registry,
            events,
            settings,
            new DenHttpClient(new HttpClient(recordingHandler)),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = "test-project",
            Title = "Activity-only replay suppression",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);

        // First attach as terminal_stream to establish a stream and produce buffered output.
        var firstAttach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Mode = "terminal_stream",
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
        }, CancellationToken.None);

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("retained output A"));
        await WaitForActivityCountAsync(registry, session.SessionId, 1);
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("retained output B"));
        await WaitForActivityCountAsync(registry, session.SessionId, 2);

        // Verify the terminal_stream stream did receive live output.
        var liveOutputs = await WaitForTerminalOutputEventsAsync(events, firstAttach.StreamId, expectedCount: 2);
        Assert.Equal(["retained output A", "retained output B"],
            liveOutputs.Select(o => Encoding.UTF8.GetString(Convert.FromBase64String(o.Data))).ToArray());
        Assert.All(liveOutputs, o => Assert.Equal("live", o.Origin));

        await service.DetachAsync(new TerminalDetachRequest
        {
            SessionId = session.SessionId,
            StreamId = firstAttach.StreamId,
        }, CancellationToken.None);

        // Record frame index before activity_only attach so we can scope assertions.
        var frameStart = events.PublishedFrames.Count;
        recordingHandler.Clear();

        // Attach with activity_only mode — replay chunks must be suppressed.
        var activityAttach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Mode = "activity_only",
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxChunks = 100 },
        }, CancellationToken.None);

        // The attach response still reports cursor/replay metadata from the buffer.
        Assert.Equal(session.SessionId, activityAttach.SessionId);
        Assert.NotEmpty(activityAttach.StreamId);
        Assert.NotEqual(firstAttach.StreamId, activityAttach.StreamId);
        Assert.Equal("cur_000000000002", activityAttach.StartCursor);
        Assert.Equal("cur_000000000001", activityAttach.ReplayAvailableFrom);
        Assert.False(activityAttach.ReplayGap);

        // No TerminalOutputEvent should be published for the activity_only stream.
        // AttachAsync is awaited so all synchronous publish paths are complete;
        // no timing delay is needed.
        var activityOutputFrames = events.PublishedFrames.Skip(frameStart)
            .Where(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent
                && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), activityAttach.StreamId, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(activityOutputFrames);

        // TerminalReplayCompleteEvent is still published for activity_only.
        var replayCompleteFrame = events.PublishedFrames.Skip(frameStart)
            .FirstOrDefault(frame => frame.Event == DesktopSidecarProtocol.TerminalReplayCompleteEvent
                && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), activityAttach.StreamId, StringComparison.Ordinal));
        Assert.NotNull(replayCompleteFrame);
        var replayComplete = JsonSerializer.Deserialize<TerminalReplayCompleteEvent>(replayCompleteFrame.Payload.GetRawText());
        Assert.NotNull(replayComplete);
        Assert.Equal(activityAttach.StreamId, replayComplete!.StreamId);
        Assert.Equal(session.SessionId, replayComplete.SessionId);
        Assert.Null(replayComplete.FromCursor);
        Assert.Equal("cur_000000000002", replayComplete.ToCursor);
        Assert.False(replayComplete.ReplayGap);

        // session.attached metadata is published with raw_stream = false via Den HTTP.
        var attachedRequests = recordingHandler.SentRequests
            .Where(r => r.RelativeUri.Contains("/desktop/session-events", StringComparison.Ordinal)
                && r.Body.TryGetProperty("event_type", out var et)
                && et.GetString() == "session.attached")
            .ToList();
        Assert.NotEmpty(attachedRequests);
        var attachedRequest = attachedRequests[0];
        Assert.True(attachedRequest.Body.TryGetProperty("payload", out var payloadProp));
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadProp.GetString()!);
        Assert.NotNull(payload);
        Assert.Equal(activityAttach.StreamId, payload!["stream_id"].GetString());
        Assert.Equal("activity_only", payload["mode"].GetString());
        Assert.False(payload["raw_stream"].GetBoolean());

        // Activity-only stream can still be detached cleanly.
        var detachResult = await service.DetachAsync(new TerminalDetachRequest
        {
            SessionId = session.SessionId,
            StreamId = activityAttach.StreamId,
            Reason = "test_activity_only_done",
        }, CancellationToken.None);
        Assert.True(detachResult.Detached);
        Assert.True(detachResult.BackendPreserved);
    }

    [Fact]
    public async Task DirectPtyService_ReconnectWithCursorReplaysOnlyNewOutput()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(backend, registry, events);

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Reconnect",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("first"));
        await WaitForActivityCountAsync(registry, session.SessionId, 1);
        var firstAttach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Replay = new TerminalReplaySpec { AfterCursor = null, MaxChunks = 10 },
        }, CancellationToken.None);
        Assert.Equal("cur_000000000001", firstAttach.StartCursor);
        Assert.False(firstAttach.ReplayGap);

        await service.DetachAsync(new TerminalDetachRequest { SessionId = session.SessionId, StreamId = firstAttach.StreamId }, CancellationToken.None);
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("second"));
        await WaitForActivityCountAsync(registry, session.SessionId, 2);

        var frameStart = events.PublishedFrames.Count;
        var reconnect = await service.ReconnectAsync(new TerminalReconnectRequest
        {
            SessionId = session.SessionId,
            PreviousStreamId = firstAttach.StreamId,
            LastSeenCursor = firstAttach.StartCursor,
        }, CancellationToken.None);

        Assert.Equal("cur_000000000002", reconnect.StartCursor);
        Assert.False(reconnect.ReplayGap);
        var replayFrames = events.PublishedFrames.Skip(frameStart)
            .Where(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent
                && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), reconnect.StreamId, StringComparison.Ordinal))
            .Select(frame => JsonSerializer.Deserialize<TerminalOutputEvent>(frame.Payload.GetRawText())!)
            .ToList();
        Assert.Single(replayFrames);
        Assert.Equal("replay", replayFrames[0].Origin);
        Assert.Equal("second", Encoding.UTF8.GetString(Convert.FromBase64String(replayFrames[0].Data)));
    }

    [Fact]
    public async Task DirectPtyService_ReconnectSignalsReplayGapWhenCursorPredatesRetainedBuffer()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { SessionReplayMaxBytes = 2, HeartbeatIntervalMs = 5000 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Reconnect gap",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);

        foreach (var value in new[] { "a", "b", "c", "d" })
        {
            backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes(value));
        }

        await WaitForActivityCountAsync(registry, session.SessionId, 4);
        var reconnect = await service.ReconnectAsync(new TerminalReconnectRequest
        {
            SessionId = session.SessionId,
            LastSeenCursor = "cur_000000000001",
        }, CancellationToken.None);

        Assert.True(reconnect.ReplayGap);
        Assert.Equal("cur_000000000003", reconnect.ReplayAvailableFrom);
        Assert.Equal("cur_000000000004", reconnect.StartCursor);
        var replayPayloads = events.PublishedFrames
            .Where(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent
                && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), reconnect.StreamId, StringComparison.Ordinal))
            .Select(frame => JsonSerializer.Deserialize<TerminalOutputEvent>(frame.Payload.GetRawText())!)
            .Select(output => Encoding.UTF8.GetString(Convert.FromBase64String(output.Data)))
            .ToList();
        Assert.Equal(["c", "d"], replayPayloads);
    }

    [Fact]
    public async Task DirectPtyService_FansOutputToMultipleStreamsAndAckIsPerStream()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { AckAfterBytes = 5, SubscriberQueueMaxBytes = 16, HeartbeatIntervalMs = 10 });

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Multiple streams",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var streamA = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, ClientId = "a" }, CancellationToken.None);
        var streamB = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, ClientId = "b" }, CancellationToken.None);

        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("abcdef"));

        var outputA = await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalOutputEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamA.StreamId, StringComparison.Ordinal));
        var outputB = await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalOutputEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamB.StreamId, StringComparison.Ordinal));
        Assert.Equal("abcdef", Encoding.UTF8.GetString(Convert.FromBase64String(outputA.Payload.GetProperty("data").GetString()!)));
        Assert.Equal("abcdef", Encoding.UTF8.GetString(Convert.FromBase64String(outputB.Payload.GetProperty("data").GetString()!)));

        await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamA.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("queue_bytes").GetInt32() >= 6);
        await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamB.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("queue_bytes").GetInt32() >= 6);

        await service.AckOutputAsync(new TerminalAckOutputRequest
        {
            SessionId = session.SessionId,
            StreamId = streamA.StreamId,
            AckCursor = "cur_000000000001",
            ReceivedBytes = 6,
        }, CancellationToken.None);

        var frameStart = events.PublishedFrames.Count;
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes("g"));

        // Wait for stream B's backpressure (7 unacked bytes >= AckAfterBytes=5).
        await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalBackpressureEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamB.StreamId, StringComparison.Ordinal)
                && frame.Payload.GetProperty("queue_bytes").GetInt32() >= 7,
            startIndex: frameStart);

        // Positive proof that HandleOutputAsync processed stream A for the "g" emission.
        // Stream A was acked, so unackedBytes is only 1 byte after this output — well below
        // the AckAfterBytes threshold. Waiting for this output event (and the stream B
        // backpressure above) guarantees all synchronous processing is complete, making the
        // negative assertion below deterministic without a timing window.
        await WaitForPublishedFrameAsync(events, DesktopSidecarProtocol.TerminalOutputEvent,
            frame => string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamA.StreamId, StringComparison.Ordinal)
                && frame.Payload.TryGetProperty("data", out var data)
                && Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!)) == "g",
            startIndex: frameStart);

        Assert.DoesNotContain(events.PublishedFrames.Skip(frameStart), frame =>
            frame.Event == DesktopSidecarProtocol.TerminalBackpressureEvent
            && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamA.StreamId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectPtyService_BuildSnapshotListForDenOmitsRawTerminalBytes()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(backend, registry, events);

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = "den-mcp",
            TaskId = 1032,
            WorkspaceId = "ws-test",
            Title = "Snapshot",
            Cwd = "/tmp/work",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var rawTerminal = "\u001b[31mSECRET_RAW_TERMINAL_BYTES\u001b[0m";
        backend.Processes[0].EmitOutput(Encoding.UTF8.GetBytes(rawTerminal));
        await WaitForActivityCountAsync(registry, session.SessionId, 1);

        var snapshots = service.BuildSnapshotListForDen();

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("den-mcp", snapshot.ProjectId);
        Assert.Equal(session.SessionId, snapshot.Request.SessionId);
        Assert.Equal(1032, snapshot.Request.TaskId);
        Assert.Equal(OperatorSessionBackend.DirectPty, snapshot.Request.Backend);
        Assert.Equal(OperatorSessionKind.Terminal, snapshot.Request.Kind);
        var snapshotRequestJson = BridgeJson.Serialize(snapshot.Request);
        var recentActivityJson = snapshot.Request.RecentActivity.GetRawText();
        Assert.Contains("terminal output (", recentActivityJson, StringComparison.Ordinal);
        Assert.Contains(" bytes)", recentActivityJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_RAW_TERMINAL_BYTES", snapshotRequestJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(rawTerminal)), snapshotRequestJson, StringComparison.Ordinal);
        var capabilitiesJson = snapshot.Request.Capabilities!.Value.GetRawText();
        Assert.Contains("raw_stream_scope", capabilitiesJson, StringComparison.Ordinal);
        Assert.Contains("local_bridge_only", capabilitiesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPtyService_RejectsInvalidSessionInputAndViewportBounds()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        await using var service = CreateDirectPtyService(backend, registry, events);

        var session = await service.CreateAsync(new TerminalCreateSessionRequest
        {
            ProjectId = string.Empty,
            Title = "Invalid inputs",
            Backend = OperatorSessionBackend.DirectPty,
        }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest
        {
            SessionId = session.SessionId,
            Viewport = new TerminalViewport { Cols = 80, Rows = 24 },
        }, CancellationToken.None);

        var emptySession = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.AttachAsync(new TerminalAttachRequest { SessionId = string.Empty }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", emptySession.Code);

        var emptySendInputSession = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.SendInputAsync(new TerminalSendInputRequest
            {
                SessionId = "   ",
                StreamId = attach.StreamId,
                Data = "ignored",
                ByteCount = 7,
            }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", emptySendInputSession.Code);

        var oversizedInput = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.SendInputAsync(new TerminalSendInputRequest
            {
                SessionId = session.SessionId,
                StreamId = attach.StreamId,
                Data = new string('x', 16_385),
                ByteCount = 16_385,
            }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", oversizedInput.Code);

        var invalidAttachViewport = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.AttachAsync(new TerminalAttachRequest
            {
                SessionId = session.SessionId,
                Viewport = new TerminalViewport { Cols = 0, Rows = 24 },
            }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", invalidAttachViewport.Code);

        var invalidResizeBounds = await Assert.ThrowsAsync<BridgeHandlerException>(() =>
            service.ResizeAsync(new TerminalResizeRequest
            {
                SessionId = session.SessionId,
                StreamId = attach.StreamId,
                Cols = 120,
                Rows = 501,
            }, CancellationToken.None));
        Assert.Equal("terminal.request.invalid", invalidResizeBounds.Code);
    }

    [Fact]
    public async Task TmuxAckOutput_IsCapabilityValidatedSnapshotContract()
    {
        var runner = new FakeTmuxCommandRunner();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var service = CreateTmuxService(runner, registry);
        var session = await service.CreateAsync(new TerminalCreateSessionRequest { ProjectId = "den-mcp", Title = "Tmux Ack" }, CancellationToken.None);
        var attach = await service.AttachAsync(new TerminalAttachRequest { SessionId = session.SessionId, Mode = "terminal_stream" }, CancellationToken.None);

        var ack = await service.AckOutputAsync(new TerminalAckOutputRequest
        {
            SessionId = session.SessionId,
            StreamId = attach.StreamId,
            AckCursor = attach.StartCursor,
            ReceivedBytes = 0,
        }, CancellationToken.None);

        Assert.True(ack.Accepted);
        Assert.Contains("backpressure_contract", registry.Get(session.SessionId)!.Capabilities.Constraints, StringComparison.Ordinal);
        Assert.Contains("deferred_to_909_911", registry.Get(session.SessionId)!.Capabilities.Constraints, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TmuxRediscover_RegistersExistingManagedSessionAndMarksMissingStale()
    {
        var runner = new FakeTmuxCommandRunner
        {
            ListSessionsOutput = "den-source-den-mcp-task909-abc\t1770000000\t0\t1770000100\tden-desktop-fixture\tden-mcp\t909\tws\tRediscovered\t/tmp/work\n",
        };
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        registry.Register(new OperatorSession
        {
            SessionId = TmuxSessionNaming.FromSessionName("den-missing").SessionId,
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = TmuxSessionNaming.FromSessionName("den-missing").BackendRef,
            Status = OperatorSessionStatus.Running,
            SourceInstanceId = "den-desktop-fixture",
            Capabilities = OperatorSessionCapabilities.FullControl(),
            CreatedAt = new DateTime(2026, 4, 29, 11, 0, 0, DateTimeKind.Utc),
        });
        var service = CreateTmuxService(runner, registry);

        var discovered = await service.RediscoverAsync(CancellationToken.None);

        Assert.Single(discovered);
        Assert.Equal("den-mcp", discovered[0].ProjectId);
        Assert.Equal(909, discovered[0].TaskId);
        var stale = registry.Get(TmuxSessionNaming.FromSessionName("den-missing").SessionId);
        Assert.Equal(OperatorSessionStatus.Stale, stale!.Status);
        Assert.False(stale.Capabilities.CanSendInput);
    }

    /// <summary>
    /// R1034-2 negative-path coverage: EnsurePublishableOutputChunks rejects oversized chunks.
    /// The guard is a defense-in-depth check in DirectPtyOperatorSessionService that throws
    /// InvalidOperationException when a chunk exceeds the configured OutputChunkMaxBytes limit.
    /// Normal operation splits chunks in OperatorSessionActivityBuffer before publication,
    /// so this test exercises the guard path directly via reflection to cover the rejection case.
    /// </summary>
    [Fact]
    public void DirectPtyService_EnsurePublishableOutputChunks_RejectsOversizedChunk()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { OutputChunkMaxBytes = 32 });

        var guardMethod = typeof(DirectPtyOperatorSessionService).GetMethod(
            "EnsurePublishableOutputChunks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(guardMethod);

        // A chunk that fits within the limit should pass the guard.
        var validChunks = new List<TerminalOutputChunk>
        {
            new()
            {
                Sequence = 1,
                Data = new byte[16],
                ByteCount = 16,
            },
        };
        var exception = Record.Exception(() => guardMethod!.Invoke(service, [validChunks]));
        Assert.Null(exception);

        // A chunk exceeding the 32-byte limit must be rejected.
        var oversizedChunks = new List<TerminalOutputChunk>
        {
            new()
            {
                Sequence = 2,
                Data = new byte[64],
                ByteCount = 64,
            },
        };
        var invocationException = Assert.Throws<TargetInvocationException>(() =>
            guardMethod!.Invoke(service, [oversizedChunks]));
        var actual = Assert.IsType<InvalidOperationException>(invocationException.InnerException);
        Assert.Contains("exceeds the configured", actual.Message, StringComparison.Ordinal);
        Assert.Contains("OutputChunkMaxBytes", actual.Message, StringComparison.Ordinal);
        Assert.Contains("chunk_000000000002", actual.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1034-2 negative-path coverage: the guard rejects a chunk whose Data array is oversized
    /// even when ByteCount happens to be at or below the limit (Data.Length check path).
    /// </summary>
    [Fact]
    public void DirectPtyService_EnsurePublishableOutputChunks_RejectsOversizedDataArray()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { OutputChunkMaxBytes = 32 });

        var guardMethod = typeof(DirectPtyOperatorSessionService).GetMethod(
            "EnsurePublishableOutputChunks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(guardMethod);

        // ByteCount is within the limit but Data.Length exceeds it.
        var deceptiveChunks = new List<TerminalOutputChunk>
        {
            new()
            {
                Sequence = 1,
                Data = new byte[64],
                ByteCount = 16, // ByteCount <= limit but Data.Length > limit
            },
        };
        var invocationException = Assert.Throws<TargetInvocationException>(() =>
            guardMethod!.Invoke(service, [deceptiveChunks]));
        var actual = Assert.IsType<InvalidOperationException>(invocationException.InnerException);
        Assert.Contains("exceeds the configured", actual.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1034-2 negative-path coverage: the guard passes when all chunks fit within the limit,
    /// and rejects as soon as the first oversized chunk is encountered.
    /// </summary>
    [Fact]
    public void DirectPtyService_EnsurePublishableOutputChunks_RejectsFirstOversizedInList()
    {
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { OutputChunkMaxBytes = 32 });

        var guardMethod = typeof(DirectPtyOperatorSessionService).GetMethod(
            "EnsurePublishableOutputChunks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(guardMethod);

        // Mixed list: first chunk valid, second oversized, third valid.
        var mixedChunks = new List<TerminalOutputChunk>
        {
            new()
            {
                Sequence = 1,
                Data = new byte[16],
                ByteCount = 16,
            },
            new()
            {
                Sequence = 2,
                Data = new byte[64],
                ByteCount = 64,
            },
            new()
            {
                Sequence = 3,
                Data = new byte[8],
                ByteCount = 8,
            },
        };
        var invocationException = Assert.Throws<TargetInvocationException>(() =>
            guardMethod!.Invoke(service, [mixedChunks]));
        var actual = Assert.IsType<InvalidOperationException>(invocationException.InnerException);
        // The guard should identify the first oversized chunk (sequence 2).
        Assert.Contains("chunk_000000000002", actual.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R1034-2 positive-path confirmation: when the buffer splits oversized input correctly,
    /// the guard allows all resulting chunks through to publication without error.
    /// This complements the negative-path tests above by verifying the guard does not
    /// reject properly split output from the normal OperatorSessionActivityBuffer path.
    /// </summary>
    [Fact]
    public void DirectPtyService_EnsurePublishableOutputChunks_AllowsBufferSplitChunks()
    {
        var chunkLimit = 32;
        var backend = new FakeDirectPtyBackend();
        var registry = new OperatorSessionRegistry(() => new DateTime(2026, 4, 29, 12, 0, 0, DateTimeKind.Utc));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(DesktopSidecarFixtures.CreateFixtureOptions()));
        using var service = CreateDirectPtyService(
            backend,
            registry,
            events,
            new TerminalStreamLimits { OutputChunkMaxBytes = chunkLimit });

        var guardMethod = typeof(DirectPtyOperatorSessionService).GetMethod(
            "EnsurePublishableOutputChunks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(guardMethod);

        // Simulate what the buffer actually produces: a 100-byte payload split into
        // chunks of at most 32 bytes each → 32 + 32 + 32 + 4 = 100 bytes in 4 chunks.
        var buffer = new OperatorSessionActivityBuffer(
            maxBytes: 100_000,
            outputChunkMaxBytes: chunkLimit);
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        var splitChunks = buffer.Append(data);

        // All chunks must fit the limit.
        Assert.Equal(4, splitChunks.Count);
        Assert.All(splitChunks, c => Assert.True(c.ByteCount <= chunkLimit));

        // The guard must accept all of them.
        var exception = Record.Exception(() => guardMethod!.Invoke(service, [splitChunks]));
        Assert.Null(exception);
    }

    private static DirectPtyOperatorSessionService CreateDirectPtyService(
        FakeDirectPtyBackend backend,
        OperatorSessionRegistry registry,
        OperatorRuntimeBridgeEventSink events,
        TerminalStreamLimits? limits = null)
    {
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        return new DirectPtyOperatorSessionService(
            backend,
            registry,
            events,
            settings,
            new DenHttpClient(),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero),
            limits);
    }

    private static TmuxOperatorSessionService CreateTmuxService(FakeTmuxCommandRunner runner, OperatorSessionRegistry registry)
    {
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        return new TmuxOperatorSessionService(
            runner,
            registry,
            events,
            settings,
            new DenHttpClient(),
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
    }

    private static TerminalOperatorSessionService CreateTerminalService(FakeTmuxCommandRunner runner, OperatorSessionRegistry registry)
    {
        var options = DesktopSidecarFixtures.CreateFixtureOptions();
        var settings = new OperatorSettingsService(OperatorSettingsStorage.ForPath(Path.Combine(Path.GetTempPath(), "den-tests", Guid.NewGuid().ToString("N"), "settings.json")));
        var events = new OperatorRuntimeBridgeEventSink(new DesktopSidecarRuntimeState(options));
        var den = new DenHttpClient();
        var tmux = new TmuxOperatorSessionService(
            runner,
            registry,
            events,
            settings,
            den,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        var direct = new DirectPtyOperatorSessionService(
            new FakeDirectPtyBackend(),
            registry,
            events,
            settings,
            den,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
        return new TerminalOperatorSessionService(registry, tmux, direct);
    }

    private static int GetTrackedTmuxStreamCount(TmuxOperatorSessionService service)
    {
        return service.TrackedStreamCount;
    }

    private static async Task WaitForActivityCountAsync(OperatorSessionRegistry registry, string sessionId, int expectedCount, int timeoutMs = 1000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((registry.Get(sessionId)?.RecentActivity.Count ?? 0) >= expectedCount)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} activity item(s) on {sessionId}.");
    }

    private static async Task<IReadOnlyList<TerminalOutputEvent>> WaitForTerminalOutputEventsAsync(
        OperatorRuntimeBridgeEventSink events,
        string streamId,
        int expectedCount,
        int timeoutMs = 1000,
        int startIndex = 0)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var outputs = events.PublishedFrames.Skip(startIndex)
                .Where(frame => frame.Event == DesktopSidecarProtocol.TerminalOutputEvent
                    && string.Equals(frame.Payload.GetProperty("stream_id").GetString(), streamId, StringComparison.Ordinal))
                .Select(frame => JsonSerializer.Deserialize<TerminalOutputEvent>(frame.Payload.GetRawText())!)
                .ToList();
            if (outputs.Count >= expectedCount)
            {
                return outputs.Take(expectedCount).ToList();
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} terminal output event(s) on {streamId}.");
    }

    private static async Task<BridgeEventFrame> WaitForPublishedFrameAsync(
        OperatorRuntimeBridgeEventSink events,
        string eventName,
        Func<BridgeEventFrame, bool>? predicate = null,
        int timeoutMs = 1000,
        int startIndex = 0)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var frame = events.PublishedFrames.Skip(startIndex).FirstOrDefault(frame =>
                frame.Event == eventName && (predicate is null || predicate(frame)));
            if (frame is not null)
            {
                return frame;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Timed out waiting for {eventName}.");
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
        Assert.Contains("\"viewport\"", json, StringComparison.Ordinal);
        Assert.Contains("\"replay\"", json, StringComparison.Ordinal);
        Assert.Contains("\"max_bytes\":65536", json, StringComparison.Ordinal);
        Assert.Contains("120", json, StringComparison.Ordinal);

        var deserialized = BridgeJson.Deserialize<TerminalAttachRequest>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(120, deserialized!.Viewport!.Cols);
        Assert.Equal(65536, deserialized.Replay!.MaxBytes);
        Assert.Equal(20, deserialized.Replay.MaxChunks);
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
                    Cwd = "/work/den-mcp",
                    SourceInstanceId = "desktop-1",
                    CanReadActivity = true,
                    CanSendInput = true,
                    CanResize = true,
                    CanAttach = true,
                    CanDetach = true,
                    CanReconnect = true,
                    CanStreamTerminal = true,
                    Warnings = ["watching"],
                },
            ],
            Count = 1,
        };

        var json = BridgeJson.Serialize(response);
        Assert.Contains("pty:test-1", json, StringComparison.Ordinal);
        Assert.Contains("direct_pty", json, StringComparison.Ordinal);
        Assert.Contains("\"can_stream_terminal\":true", json, StringComparison.Ordinal);
        Assert.Contains("/work/den-mcp", json, StringComparison.Ordinal);
        Assert.Contains("watching", json, StringComparison.Ordinal);
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
                AckAfterMillis = 500,
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
        Assert.Contains("\"ack_after_millis\":500", json, StringComparison.Ordinal);
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
        Assert.Equal(500, deserialized.Limits.AckAfterMillis);
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

public sealed class FakeTmuxCommandRunner : ITmuxCommandRunner
{
    public string ListSessionsOutput { get; init; } = string.Empty;
    public List<FakeTmuxCall> Calls { get; } = [];

    public Task<TmuxCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        Calls.Add(new FakeTmuxCall(args.ToArray()));
        var command = args.Count > 0 ? args[0] : string.Empty;
        return Task.FromResult(command switch
        {
            "list-sessions" => new TmuxCommandResult { ExitCode = 0, Stdout = ListSessionsOutput },
            "capture-pane" => new TmuxCommandResult { ExitCode = 0, Stdout = "captured output\n" },
            _ => new TmuxCommandResult { ExitCode = 0 },
        });
    }
}

public sealed record FakeTmuxCall(IReadOnlyList<string> Args);

public sealed class FakeDirectPtyBackend : IDirectPtyBackend
{
    public List<DirectPtyStartInfo> Starts { get; } = [];
    public List<FakeDirectPtyProcess> Processes { get; } = [];

    public Task<IDirectPtyProcess> SpawnAsync(DirectPtyStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        Starts.Add(startInfo);
        var process = new FakeDirectPtyProcess(startInfo.SessionId);
        Processes.Add(process);
        return Task.FromResult<IDirectPtyProcess>(process);
    }
}

public sealed class FakeDirectPtyProcess : IDirectPtyProcess
{
    public FakeDirectPtyProcess(string sessionId)
    {
        SessionId = sessionId;
    }

    public string SessionId { get; }
    public int? ProcessId => 1234;
    public bool HasExited { get; private set; }
    public int? ExitCode { get; private set; }
    public List<byte[]> Writes { get; } = [];
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public event EventHandler<byte[]>? OutputReceived;
    public event EventHandler<DirectPtyExitedEventArgs>? Exited;

    public Task WriteAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        Writes.Add(bytes);
        return Task.CompletedTask;
    }

    public void Resize(int cols, int rows)
    {
        Resizes.Add((cols, rows));
    }

    public Task TerminateAsync(string mode, CancellationToken cancellationToken = default)
    {
        HasExited = true;
        ExitCode = 0;
        Exited?.Invoke(this, new DirectPtyExitedEventArgs(0, "process_exited"));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void EmitOutput(byte[] bytes) => OutputReceived?.Invoke(this, bytes);
}

/// <summary>
/// A <see cref="DelegatingHandler"/> that records outgoing HTTP requests and returns 200 OK.
/// Used to capture Den session event publish calls in tests.
/// </summary>
internal sealed class RecordingDelegatingHandler : DelegatingHandler
{
    private readonly List<(string RelativeUri, JsonElement Body)> _sentRequests = [];

    public IReadOnlyList<(string RelativeUri, JsonElement Body)> SentRequests
    {
        get
        {
            lock (_sentRequests)
            {
                return _sentRequests.ToArray();
            }
        }
    }

    public void Clear()
    {
        lock (_sentRequests)
        {
            _sentRequests.Clear();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri?.PathAndQuery ?? string.Empty;
        JsonElement body = default;
        if (request.Content is { } content)
        {
            var bodyStr = await content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(bodyStr))
            {
                body = JsonSerializer.Deserialize<JsonElement>(bodyStr);
            }
        }

        lock (_sentRequests)
        {
            _sentRequests.Add((uri, body));
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
