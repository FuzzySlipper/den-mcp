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
    public async Task DeliveryService_LiveControllableSession_SendsViaTerminalService()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var registry = new OperatorSessionRegistry();
        var runner = new FakeTmuxCommandRunner();
        var service = CreateDeliveryService(provider, registry, runner);
        var identity = TmuxSessionNaming.FromSessionName("den-collab-agent");
        registry.Register(new OperatorSession
        {
            SessionId = identity.SessionId,
            ProjectId = "test",
            Kind = OperatorSessionKind.Agent,
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
