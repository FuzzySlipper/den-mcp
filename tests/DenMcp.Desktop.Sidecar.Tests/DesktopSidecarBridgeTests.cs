using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar.Tests;

public class DesktopSidecarBridgeTests
{
    [Fact]
    public void SchemaBundle_RegistersRuntimeCommandsAndEvents()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var bundle = DesktopSidecarBridge.CreateSchemaBundle(provider);

        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, bundle.BundleId);
        Assert.Equal(DesktopSidecarProtocol.SchemaVersion, bundle.SchemaVersion);
        Assert.Equal(
            new[]
            {
                DesktopSidecarProtocol.CapabilitiesCommand,
                DesktopSidecarProtocol.HealthCommand,
                DesktopSidecarProtocol.GetLatestDiffSnapshotCommand,
                DesktopSidecarProtocol.GetSettingsCommand,
                DesktopSidecarProtocol.GetOperatorStatusCommand,
                DesktopSidecarProtocol.ListLocalGitSnapshotsCommand,
                DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand,
                DesktopSidecarProtocol.RefreshNowCommand,
                DesktopSidecarProtocol.SaveSettingsCommand,
            },
            bundle.Commands.Select(command => command.Command).ToArray());
        Assert.Equal(
            new[]
            {
                DesktopSidecarProtocol.GitSnapshotEvent,
                DesktopSidecarProtocol.OperatorStatusEvent,
                DesktopSidecarProtocol.SessionSnapshotEvent,
            },
            bundle.Events.Select(@event => @event.Event).ToArray());
        Assert.Contains(DesktopSidecarProtocol.GetOperatorStatusCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.OperatorStatusEvent + ".payload", bundle.Definitions.Keys);
    }

    [Fact]
    public async Task CommandRouter_ReturnsHealthCapabilitiesAndRuntimeStatusThroughBridgeResponses()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var healthResponse = await router.DispatchAsync(Request("req_health", DesktopSidecarProtocol.HealthCommand));
        var capabilitiesResponse = await router.DispatchAsync(Request("req_capabilities", DesktopSidecarProtocol.CapabilitiesCommand));
        var statusResponse = await router.DispatchAsync(Request("req_status", DesktopSidecarProtocol.GetOperatorStatusCommand));

        Assert.Null(healthResponse.Error);
        Assert.NotNull(healthResponse.Result);
        Assert.Equal("den-desktop", healthResponse.Result!.Value.GetProperty("app_id").GetString());
        Assert.Equal(DesktopSidecarProtocol.SchemaVersion, healthResponse.Result.Value.GetProperty("schema_version").GetString());
        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, healthResponse.Result.Value.GetProperty("schema_bundle_id").GetString());

        Assert.Null(capabilitiesResponse.Error);
        Assert.NotNull(capabilitiesResponse.Result);
        Assert.Equal("loopback_websocket", capabilitiesResponse.Result!.Value.GetProperty("supported_transports")[0].GetString());
        Assert.Contains(
            DesktopSidecarProtocol.GetOperatorStatusCommand,
            capabilitiesResponse.Result.Value.GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("command").GetString()));
        Assert.Contains(
            DesktopSidecarProtocol.OperatorStatusEvent,
            capabilitiesResponse.Result.Value.GetProperty("events").EnumerateArray().Select(@event => @event.GetProperty("event").GetString()));

        Assert.Null(statusResponse.Error);
        Assert.Equal("starting", statusResponse.Result!.Value.GetProperty("phase").GetString());
        Assert.Equal("unknown", statusResponse.Result.Value.GetProperty("denConnection").GetProperty("state").GetString());
    }

    [Fact]
    public void ReadySentinel_SerializesSingleBootstrapLineWithCompatibilityMetadata()
    {
        var options = DesktopSidecarFixtures.CreateFixtureOptions() with { Port = 0, EndpointPath = "/bridge" };
        var line = DesktopSidecarStartup.FormatReadySentinel(DesktopSidecarStartup.CreateReadySentinel(options, 54321));

        Assert.StartsWith(DesktopSidecarProtocol.ReadySentinelPrefix, line, StringComparison.Ordinal);
        Assert.Contains("\"port\":54321", line, StringComparison.Ordinal);
        Assert.Contains("\"endpoint_path\":\"/bridge\"", line, StringComparison.Ordinal);
        Assert.Contains("\"protocol_version\":\"1.0\"", line, StringComparison.Ordinal);
        Assert.Contains($"\"schema_version\":\"{DesktopSidecarProtocol.SchemaVersion}\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-token", line, StringComparison.Ordinal);
    }

    [Fact]
    public void WireFixture_ContainsSchemaVersionedHealthCapabilitiesAndRuntimeEventFrames()
    {
        var fixture = DesktopSidecarFixtures.CreateWireFixture(DesktopSidecarFixtures.CreateFixtureOptions());
        var json = BridgeJson.Serialize(fixture);

        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, fixture.SchemaBundleId);
        Assert.Equal("response", fixture.Frames.HealthResponse.FrameType);
        Assert.Equal("response", fixture.Frames.CapabilitiesResponse.FrameType);
        Assert.Equal("event", fixture.Frames.OperatorStatusEvent.FrameType);
        Assert.Equal(DesktopSidecarProtocol.OperatorStatusEvent, fixture.Frames.OperatorStatusEvent.Event);
        Assert.Equal(DesktopSidecarProtocol.GitSnapshotEvent, fixture.Frames.GitSnapshotEvent.Event);
        Assert.Equal(DesktopSidecarProtocol.SessionSnapshotEvent, fixture.Frames.SessionSnapshotEvent.Event);
        Assert.Contains(DesktopSidecarProtocol.GetOperatorStatusCommand, json, StringComparison.Ordinal);
        Assert.Contains(DesktopSidecarProtocol.OperatorStatusEvent, json, StringComparison.Ordinal);
    }

    private static BridgeRequestFrame Request(string requestId, string command)
    {
        return new BridgeRequestFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            RequestId = requestId,
            Command = command,
            Payload = BridgeJson.EmptyObject(),
            SentAt = new DateTimeOffset(2026, 4, 29, 12, 34, 56, TimeSpan.Zero),
        };
    }
}
