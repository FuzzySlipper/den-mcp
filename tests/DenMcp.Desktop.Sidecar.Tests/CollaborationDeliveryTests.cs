using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar.Tests;

public class CollaborationDeliveryTests
{
    [Fact]
    public void DeliveryStatus_Constants_AreStable()
    {
        Assert.Equal("delivered", CollaborationDeliveryStatus.Delivered);
        Assert.Equal("no_live_session", CollaborationDeliveryStatus.NoLiveSession);
        Assert.Equal("session_stale", CollaborationDeliveryStatus.SessionStale);
        Assert.Equal("session_offline", CollaborationDeliveryStatus.SessionOffline);
        Assert.Equal("capability_denied", CollaborationDeliveryStatus.CapabilityDenied);
        Assert.Equal("skipped", CollaborationDeliveryStatus.Skipped);
        Assert.Equal("failed", CollaborationDeliveryStatus.Failed);
        Assert.Equal("draft_only_fallback", CollaborationDeliveryStatus.DraftOnlyFallback);
    }

    [Fact]
    public void ToolRegistry_IncludesSendCompiledResponse()
    {
        var registry = new AppAgentToolRegistry();
        var tool = registry.GetRequired("send_compiled_response");

        Assert.Equal("send_compiled_response", tool.Name);
        Assert.Equal("Send Compiled Response", tool.DisplayName);
        Assert.Equal("action", tool.Category);
        Assert.True(tool.Enabled);
        Assert.Contains("collaboration.deliver", tool.Capabilities);
    }

    [Fact]
    public async Task DeliveryService_NoLiveSession_ReturnsNoLiveSessionStatus()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var service = provider.GetRequiredService<CollaborationResponseDeliveryService>();

        // Since we cannot actually call Den in tests, we test with a compiled_text
        // that skips the Den load step, and no target session.
        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal("Test compiled response text", response.CompiledText);
        Assert.Equal(CollaborationDeliveryStatus.NoLiveSession, response.Delivery.Status);
        Assert.False(response.DenPost.Posted);
    }

    [Fact]
    public async Task DeliveryService_WithTargetSession_NotInRegistry_ReturnsNoLiveSession()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var service = provider.GetRequiredService<CollaborationResponseDeliveryService>();

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = "nonexistent-session",
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.NoLiveSession, response.Delivery.Status);
        Assert.Equal("nonexistent-session", response.Delivery.TargetSessionId);
        Assert.Contains("not found", response.Delivery.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeliveryService_StaleSession_ReturnsSessionStale()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = provider.GetRequiredService<OperatorSessionRegistry>();
        var service = provider.GetRequiredService<CollaborationResponseDeliveryService>();

        registry.Register(new OperatorSession
        {
            SessionId = "stale-session",
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Tmux,
            Status = OperatorSessionStatus.Stale,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("stale test"),
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = "stale-session",
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.SessionStale, response.Delivery.Status);
        Assert.Equal("stale-session", response.Delivery.TargetSessionId);
    }

    [Fact]
    public async Task DeliveryService_OfflineSession_ReturnsSessionOffline()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = provider.GetRequiredService<OperatorSessionRegistry>();
        var service = provider.GetRequiredService<CollaborationResponseDeliveryService>();

        registry.Register(new OperatorSession
        {
            SessionId = "offline-session",
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Tmux,
            Status = OperatorSessionStatus.SourceOffline,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("offline test"),
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = "offline-session",
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.SessionOffline, response.Delivery.Status);
    }

    [Fact]
    public async Task DeliveryService_LiveControllableAgentSession_SendsViaTerminalService()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-collab-agent");
        // Agent session: has agent_identity set, so CanDeliverCompiledResponse is explicitly granted.
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            AgentIdentity = "pi",
            Role = "coder",
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.FullControl() with { CanDeliverCompiledResponse = true },
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Please handle this annotated reply.",
            TargetSessionId = identity.SessionId,
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.Delivered, response.Delivery.Status);
        Assert.True(response.Delivery.CanDeliver);
        var sendKeys = Assert.Single(runner.Calls, call => call.Args.Count > 0 && call.Args[0] == "send-keys");
        Assert.Equal("den-collab-agent", sendKeys.Args[2]);
        var payload = sendKeys.Args[^1];
        Assert.Contains("[compiled-collaboration-response]", payload);
        Assert.Contains("Please handle this annotated reply.", payload);
    }

    [Fact]
    public async Task DeliveryService_SessionWithoutDeliverCapability_ReturnsCapabilityDenied()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-no-deliver");
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.FullControl(),
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = identity.SessionId,
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.CapabilityDenied, response.Delivery.Status);
        Assert.Contains("can_deliver_compiled_response", response.Delivery.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DeliveryService_SessionWithoutSendInputCapability_ReturnsCapabilityDenied()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = provider.GetRequiredService<OperatorSessionRegistry>();
        var service = provider.GetRequiredService<CollaborationResponseDeliveryService>();

        registry.Register(new OperatorSession
        {
            SessionId = "observe-only-session",
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            Backend = OperatorSessionBackend.Tmux,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.ObserveOnly("observe only test", canReadActivity: true),
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = "observe-only-session",
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.CapabilityDenied, response.Delivery.Status);
        Assert.Equal("observe-only-session", response.Delivery.TargetSessionId);
        Assert.False(response.Delivery.CanDeliver);
    }

    [Fact]
    public async Task DeliveryService_TooLargeResponse_ReturnsDraftOnlyFallback()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-large-agent");
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            AgentIdentity = "pi",
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.FullControl() with { CanDeliverCompiledResponse = true },
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        // Generate text larger than DraftOnlyThresholdBytes (128 KiB)
        var largeText = new string('A', 200 * 1024);
        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = largeText,
            TargetSessionId = identity.SessionId,
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.DraftOnlyFallback, response.Delivery.Status);
        Assert.False(response.Delivery.CanDeliver);
        Assert.Contains("safe delivery threshold", response.Delivery.Reason);
        Assert.DoesNotContain(runner.Calls, c => c.Args.Count > 0 && c.Args[0] == "send-keys");
    }

    [Fact]
    public async Task DeliveryService_PlainTerminalSession_ReturnsCapabilityDenied()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-plain-shell");
        // Plain terminal session: no agent identity, no task association.
        // CanDeliverCompiledResponse should be false by default.
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Terminal,
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.FullControl(), // CanDeliverCompiledResponse defaults false
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = "Test compiled response text",
            TargetSessionId = identity.SessionId,
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.CapabilityDenied, response.Delivery.Status);
        Assert.Contains("can_deliver_compiled_response", response.Delivery.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void DelimiterProtocol_Constants_AreStable()
    {
        Assert.Equal("[compiled-collaboration-response]", CollaborationDelimiterProtocol.OpenTag);
        Assert.Equal("[/compiled-collaboration-response]", CollaborationDelimiterProtocol.CloseTag);
        Assert.Equal("[compiled-collaboration-response part=\"{0}/{1}\"]", CollaborationDelimiterProtocol.ChunkOpenTagFormat);
        Assert.Equal(128 * 1024, CollaborationDelimiterProtocol.DraftOnlyThresholdBytes);
    }

    [Fact]
    public async Task DeliveryService_ChunkedDeliveryUtf8_NoReplacementCharacters()
    {
        // R1074-3: Regression test for UTF-8 boundary safety in chunked delivery.
        // Constructs a response with multi-byte UTF-8 characters (4-byte emoji)
        // positioned to land at chunk boundaries. Verifies:
        //   1. No U+FFFD replacement characters in any chunk payload
        //   2. Reassembled content matches the original compiled text
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-utf8-agent");
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
            AgentIdentity = "pi",
            Role = "coder",
            Backend = OperatorSessionBackend.Tmux,
            BackendRef = identity.BackendRef,
            Status = OperatorSessionStatus.Running,
            Capabilities = OperatorSessionCapabilities.FullControl() with { CanDeliverCompiledResponse = true },
            CreatedAt = DateTime.UtcNow,
            SourceInstanceId = "test",
        });

        // Build text with 4-byte emoji (UTF-8) characters that will land at chunk
        // boundaries when split into ~16 KiB chunks. Each emoji is 4 UTF-8 bytes;
        // by placing many of them, we ensure at least one falls on a boundary.
        // Total size: ~24 KiB (> 16 KiB chunk limit, < 128 KiB draft-only threshold).
        const string emoji = "\U0001F389"; // 🎉 = 4 UTF-8 bytes
        const string line = "Line of text with emoji ";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 1000; i++)
        {
            sb.Append(line);
            sb.Append(emoji);
            sb.Append(' ');
            sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        var compiledText = sb.ToString();
        var compiledBytes = System.Text.Encoding.UTF8.GetByteCount(compiledText);
        // Sanity check: must be large enough to trigger chunking
        Assert.True(compiledBytes > 16_384, $"Test text must exceed 16 KiB to trigger chunking; got {compiledBytes} bytes");
        Assert.True(compiledBytes < 128 * 1024, $"Test text must be below 128 KiB draft-only threshold; got {compiledBytes} bytes");

        var response = await service.DeliverAsync(new CollaborationSendCompiledResponseRequest
        {
            SessionId = 1,
            CompiledText = compiledText,
            TargetSessionId = identity.SessionId,
            PostToDen = false,
        }, CancellationToken.None);

        Assert.Equal(CollaborationDeliveryStatus.Delivered, response.Delivery.Status);

        // Collect all tmux send-keys payloads.
        var payloads = runner.Calls
            .Where(c => c.Args.Count > 0 && c.Args[0] == "send-keys")
            .Select(c => c.Args[^1] as string)
            .Where(p => p is not null)
            .ToList();
        Assert.True(payloads.Count > 1, $"Expected chunked delivery (>1 send-keys); got {payloads.Count}");

        // Verify no U+FFFD replacement character in any payload.
        foreach (var payload in payloads)
        {
            Assert.DoesNotContain('\uFFFD', payload!);
        }

        // Reassemble: strip delimiters and concatenate chunk text directly.
        // Chunks are split at UTF-8 byte boundaries, not line boundaries,
        // so the split can occur mid-line. No separator between chunks.
        var reassembled = new System.Text.StringBuilder();
        foreach (var payload in payloads)
        {
            // Each payload: [compiled-collaboration-response part="N/M"]\n<chunk>\n[/compiled-collaboration-response]\n
            var lines = payload!.Split('\n');
            // lines[0] = open tag, lines[^1] = empty (trailing newline), lines[^2] = close tag
            // content is lines[1..^2]
            for (var j = 1; j < lines.Length - 2; j++)
            {
                if (j > 1) reassembled.Append('\n');
                reassembled.Append(lines[j]);
            }
        }

        // The reassembled text should match the original compiled text.
        Assert.Equal(compiledText, reassembled.ToString());
    }

    private static CollaborationResponseDeliveryService CreateDeliveryService(
        ServiceProvider provider,
        OperatorSessionRegistry registry,
        FakeTmuxCommandRunner runner)
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
        var terminals = new TerminalOperatorSessionService(registry, tmux, direct);
        return new CollaborationResponseDeliveryService(
            registry,
            den,
            provider.GetRequiredService<OperatorRuntimeService>(),
            terminals,
            events,
            () => new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Dto_CollaborationSendCompiledResponseResponse_DefaultDeliveryIsSkipped()
    {
        var response = new CollaborationSendCompiledResponseResponse
        {
            CompiledText = "test",
            SessionId = 1,
        };

        Assert.Equal(CollaborationDeliveryStatus.Skipped, response.Delivery.Status);
    }

    [Fact]
    public void Dto_CollaborationDenPostRecord_DefaultValues()
    {
        var post = new CollaborationDenPostRecord();

        Assert.False(post.Posted);
        Assert.Null(post.DraftId);
        Assert.Null(post.ProjectId);
        Assert.Null(post.Error);
    }
}
