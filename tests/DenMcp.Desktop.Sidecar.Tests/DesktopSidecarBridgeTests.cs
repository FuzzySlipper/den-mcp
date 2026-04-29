using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar.Tests;

public class DesktopSidecarBridgeTests
{
    [Fact]
    public void SchemaBundle_RegistersOnlySkeletonCommandsAndPlaceholderEvent()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var bundle = DesktopSidecarBridge.CreateSchemaBundle(provider);

        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, bundle.BundleId);
        Assert.Equal(DesktopSidecarProtocol.SchemaVersion, bundle.SchemaVersion);
        Assert.Equal(
            new[] { DesktopSidecarProtocol.CapabilitiesCommand, DesktopSidecarProtocol.HealthCommand },
            bundle.Commands.Select(command => command.Command).ToArray());
        Assert.Equal(new[] { DesktopSidecarProtocol.PlaceholderRuntimeEvent }, bundle.Events.Select(@event => @event.Event).ToArray());
        Assert.Contains(DesktopSidecarProtocol.HealthCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.CapabilitiesCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.PlaceholderRuntimeEvent + ".payload", bundle.Definitions.Keys);
    }

    [Fact]
    public async Task CommandRouter_ReturnsHealthAndCapabilitiesThroughBridgeResponses()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var router = provider.GetRequiredService<IBridgeCommandRouter>();

        var healthResponse = await router.DispatchAsync(Request("req_health", DesktopSidecarProtocol.HealthCommand));
        var capabilitiesResponse = await router.DispatchAsync(Request("req_capabilities", DesktopSidecarProtocol.CapabilitiesCommand));

        Assert.Null(healthResponse.Error);
        Assert.NotNull(healthResponse.Result);
        Assert.Equal("den-desktop", healthResponse.Result!.Value.GetProperty("app_id").GetString());
        Assert.Equal(DesktopSidecarProtocol.SchemaVersion, healthResponse.Result.Value.GetProperty("schema_version").GetString());
        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, healthResponse.Result.Value.GetProperty("schema_bundle_id").GetString());

        Assert.Null(capabilitiesResponse.Error);
        Assert.NotNull(capabilitiesResponse.Result);
        Assert.Equal("loopback_websocket", capabilitiesResponse.Result!.Value.GetProperty("supported_transports")[0].GetString());
        Assert.Equal(DesktopSidecarProtocol.HealthCommand, capabilitiesResponse.Result.Value.GetProperty("commands")[1].GetProperty("command").GetString());
        Assert.Equal(DesktopSidecarProtocol.PlaceholderRuntimeEvent, capabilitiesResponse.Result.Value.GetProperty("events")[0].GetProperty("event").GetString());
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
    public void WireFixture_ContainsSchemaVersionedHealthCapabilitiesAndPlaceholderEventFrames()
    {
        var fixture = DesktopSidecarFixtures.CreateWireFixture(DesktopSidecarFixtures.CreateFixtureOptions());
        var json = BridgeJson.Serialize(fixture);

        Assert.Equal(DesktopSidecarProtocol.SchemaBundleId, fixture.SchemaBundleId);
        Assert.Equal("response", fixture.Frames.HealthResponse.FrameType);
        Assert.Equal("response", fixture.Frames.CapabilitiesResponse.FrameType);
        Assert.Equal("event", fixture.Frames.PlaceholderEvent.FrameType);
        Assert.Equal(DesktopSidecarProtocol.PlaceholderRuntimeEvent, fixture.Frames.PlaceholderEvent.Event);
        Assert.Contains("bridge.get_health", json, StringComparison.Ordinal);
        Assert.Contains("den_desktop.runtime.placeholder", json, StringComparison.Ordinal);
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
