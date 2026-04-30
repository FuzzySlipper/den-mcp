using System.Text.Json;
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
        // Both registries sort by name (Ordinal).
        var sortedCommands = new[]
        {
            DesktopSidecarProtocol.CapabilitiesCommand,
            DesktopSidecarProtocol.HealthCommand,
            DesktopSidecarProtocol.GetAppearanceSettingsCommand,
            DesktopSidecarProtocol.GetLatestDiffSnapshotCommand,
            DesktopSidecarProtocol.GetSettingsCommand,
            DesktopSidecarProtocol.GetOperatorStatusCommand,
            DesktopSidecarProtocol.ListLocalGitSnapshotsCommand,
            DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand,
            DesktopSidecarProtocol.RefreshNowCommand,
            DesktopSidecarProtocol.SaveAppearanceSettingsCommand,
            DesktopSidecarProtocol.SaveSettingsCommand,
            DesktopSidecarProtocol.TerminalAckOutputCommand,
            DesktopSidecarProtocol.TerminalAttachCommand,
            DesktopSidecarProtocol.TerminalCreateSessionCommand,
            DesktopSidecarProtocol.TerminalDetachCommand,
            DesktopSidecarProtocol.TerminalListSessionsCommand,
            DesktopSidecarProtocol.TerminalReadActivityCommand,
            DesktopSidecarProtocol.TerminalReconnectCommand,
            DesktopSidecarProtocol.TerminalResizeCommand,
            DesktopSidecarProtocol.TerminalSendInputCommand,
            DesktopSidecarProtocol.TerminalTerminateCommand,
            DesktopSidecarProtocol.ConsoleListCommandsCommand,
            DesktopSidecarProtocol.ConsoleRunCommandCommand,
        }.OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(sortedCommands, bundle.Commands.Select(command => command.Command).ToArray());

        var sortedEvents = new[]
        {
            DesktopSidecarProtocol.TerminalOutputEvent,
            DesktopSidecarProtocol.TerminalSessionListEvent,
            DesktopSidecarProtocol.TerminalSessionStatusEvent,
            DesktopSidecarProtocol.GitSnapshotEvent,
            DesktopSidecarProtocol.OperatorStatusEvent,
            DesktopSidecarProtocol.SessionSnapshotEvent,
        }.OrderBy(e => e, StringComparer.Ordinal).ToArray();
        Assert.Contains(DesktopSidecarProtocol.ConsoleListCommandsCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.ConsoleRunCommandCommand + ".response", bundle.Definitions.Keys);
        Assert.Equal(sortedEvents, bundle.Events.Select(@event => @event.Event).ToArray());
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

    [Fact]
    public void SchemaBundle_TerminalResponseSchemasHavePropertyDefinitions()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var bundle = DesktopSidecarBridge.CreateSchemaBundle(provider);

        // All terminal command response schemas must define properties (not empty objects)
        var terminalResponseSuffixes = new[]
        {
            DesktopSidecarProtocol.TerminalCreateSessionCommand + ".response",
            DesktopSidecarProtocol.TerminalListSessionsCommand + ".response",
            DesktopSidecarProtocol.TerminalReadActivityCommand + ".response",
            DesktopSidecarProtocol.TerminalAttachCommand + ".response",
            DesktopSidecarProtocol.TerminalDetachCommand + ".response",
            DesktopSidecarProtocol.TerminalSendInputCommand + ".response",
            DesktopSidecarProtocol.TerminalResizeCommand + ".response",
            DesktopSidecarProtocol.TerminalTerminateCommand + ".response",
            DesktopSidecarProtocol.TerminalReconnectCommand + ".response",
            DesktopSidecarProtocol.TerminalAckOutputCommand + ".response",
        };

        foreach (var key in terminalResponseSuffixes)
        {
            Assert.True(bundle.Definitions.ContainsKey(key), $"Schema definition '{key}' is missing");
            var schema = bundle.Definitions[key];
            Assert.True(schema.TryGetProperty("properties", out var properties),
                $"Schema '{key}' is missing 'properties' definition");
            Assert.NotEqual(JsonValueKind.Null, properties.ValueKind);
        }

        // Terminal event payload schemas must also have properties
        var terminalEventPayloads = new[]
        {
            DesktopSidecarProtocol.TerminalOutputEvent + ".payload",
            DesktopSidecarProtocol.TerminalSessionStatusEvent + ".payload",
            DesktopSidecarProtocol.TerminalSessionListEvent + ".payload",
        };

        foreach (var key in terminalEventPayloads)
        {
            Assert.True(bundle.Definitions.ContainsKey(key), $"Schema definition '{key}' is missing");
            var schema = bundle.Definitions[key];
            Assert.True(schema.TryGetProperty("properties", out var properties),
                $"Schema '{key}' is missing 'properties' definition");
            Assert.NotEqual(JsonValueKind.Null, properties.ValueKind);
        }
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
