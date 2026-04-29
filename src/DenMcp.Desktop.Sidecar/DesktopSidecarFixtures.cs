using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar;

public static class DesktopSidecarFixtures
{
    private static readonly DateTimeOffset FixtureTimestamp = new(2026, 4, 29, 12, 34, 56, TimeSpan.Zero);

    public static DesktopSidecarWireFixture CreateWireFixture(DesktopSidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var provider = DesktopSidecarBridge.CreateServiceProvider(options);
        var state = provider.GetRequiredService<DesktopSidecarRuntimeState>();
        var schemaBundle = DesktopSidecarBridge.CreateSchemaBundle(provider);
        var health = state.CreateHealth(FixtureTimestamp);
        var capabilitiesHandler = ActivatorUtilities.CreateInstance<GetCapabilitiesHandler>(provider);
        var capabilities = capabilitiesHandler.HandleAsync(
            new DesktopSidecarEmptyRequest(),
            new BridgeRequestContext("req_capabilities", BridgeCorrelation.Empty, (_, _) => ValueTask.CompletedTask),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();

        return new DesktopSidecarWireFixture
        {
            SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId,
            SchemaBundle = schemaBundle,
            Frames = new DesktopSidecarWireFrames
            {
                HealthResponse = BridgeResponseFrame.Success(
                    "req_health",
                    BridgeJson.ToElement(health),
                    BridgeCorrelation.Empty,
                    FixtureTimestamp,
                    DesktopSidecarProtocol.SchemaVersion),
                CapabilitiesResponse = BridgeResponseFrame.Success(
                    "req_capabilities",
                    BridgeJson.ToElement(capabilities),
                    BridgeCorrelation.Empty,
                    FixtureTimestamp,
                    DesktopSidecarProtocol.SchemaVersion),
                PlaceholderEvent = new BridgeEventFrame
                {
                    SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
                    EventId = "evt_placeholder_001",
                    Sequence = state.NextSequence(),
                    Event = DesktopSidecarProtocol.PlaceholderRuntimeEvent,
                    Payload = BridgeJson.ToElement(state.CreatePlaceholderEventPayload()),
                    Correlation = BridgeCorrelation.Empty,
                    SentAt = FixtureTimestamp,
                },
            },
        };
    }

    public static DesktopSidecarOptions CreateFixtureOptions()
    {
        return new DesktopSidecarOptions
        {
            AppId = DesktopSidecarOptions.DefaultAppId,
            AppVersion = "0.1.0-test",
            ConfigPath = "/tmp/den-desktop/config",
            LogPath = "/tmp/den-desktop/logs",
            AuthToken = "fixture-token",
            Port = 0,
            EndpointPath = DesktopSidecarOptions.DefaultEndpointPath,
        };
    }
}
