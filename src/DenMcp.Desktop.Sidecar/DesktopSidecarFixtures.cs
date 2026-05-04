using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar;

public static class DesktopSidecarFixtures
{
    private static readonly DateTimeOffset FixtureTimestamp = new(2026, 4, 29, 12, 34, 56, TimeSpan.Zero);

    /// <summary>
    /// Creates a comprehensive wire fixture covering all runtime commands, events, and
    /// schema bundle definitions. The fixture size is proportional to the protocol surface
    /// area and intentionally grows when new commands or events are added. See review
    /// finding R1000-5.
    /// </summary>
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

        var status = OperatorStatus.Starting(OperatorSettings.CreateDefault(() => "den-desktop-fixture"));

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
                OperatorStatusEvent = EventFrame(state, "evt_status_001", DesktopSidecarProtocol.OperatorStatusEvent, status),
                GitSnapshotEvent = EventFrame(state, "evt_git_001", DesktopSidecarProtocol.GitSnapshotEvent, Array.Empty<LocalGitSnapshot>()),
                SessionSnapshotEvent = EventFrame(state, "evt_session_001", DesktopSidecarProtocol.SessionSnapshotEvent, Array.Empty<LocalSessionSnapshot>()),
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

    private static BridgeEventFrame EventFrame<T>(DesktopSidecarRuntimeState state, string eventId, string eventName, T payload)
    {
        return new BridgeEventFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            EventId = eventId,
            Sequence = state.NextSequence(),
            Event = eventName,
            Payload = BridgeJson.ToElement(payload),
            Correlation = BridgeCorrelation.Empty,
            SentAt = FixtureTimestamp,
        };
    }
}
