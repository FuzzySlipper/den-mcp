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
            DesktopSidecarProtocol.AppAgentBuildContextCommand,
            DesktopSidecarProtocol.AppAgentCancelRequestCommand,
            DesktopSidecarProtocol.AppAgentInvokeToolCommand,
            DesktopSidecarProtocol.AppAgentListToolsCommand,
            DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand,
            DesktopSidecarProtocol.GetAppearanceSettingsCommand,
            DesktopSidecarProtocol.GetLatestDiffSnapshotCommand,
            DesktopSidecarProtocol.GetSettingsCommand,
            DesktopSidecarProtocol.GetOperatorStatusCommand,
            DesktopSidecarProtocol.ListLocalGitSnapshotsCommand,
            DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand,
            DesktopSidecarProtocol.DocumentGetCommand,
            DesktopSidecarProtocol.DocumentStoreCommand,
            DesktopSidecarProtocol.DocumentsListCommand,
            DesktopSidecarProtocol.MessagesGetSnapshotCommand,
            DesktopSidecarProtocol.RefreshNowCommand,
            DesktopSidecarProtocol.SaveAppearanceSettingsCommand,
            DesktopSidecarProtocol.SaveSettingsCommand,
            DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand,
            DesktopSidecarProtocol.TaskUpdateCommand,
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
            DesktopSidecarProtocol.TerminalBackpressureEvent,
            DesktopSidecarProtocol.TerminalErrorEvent,
            DesktopSidecarProtocol.TerminalExitEvent,
            DesktopSidecarProtocol.TerminalHeartbeatEvent,
            DesktopSidecarProtocol.TerminalOutputEvent,
            DesktopSidecarProtocol.TerminalReplayCompleteEvent,
            DesktopSidecarProtocol.TerminalSessionListEvent,
            DesktopSidecarProtocol.TerminalSessionStatusEvent,
            DesktopSidecarProtocol.CollaborationDeliveryEvent,
            DesktopSidecarProtocol.AppAgentRunStateEvent,
            DesktopSidecarProtocol.AppAgentToolCallStateEvent,
            DesktopSidecarProtocol.GitSnapshotEvent,
            DesktopSidecarProtocol.OperatorStatusEvent,
            DesktopSidecarProtocol.SessionSnapshotEvent,
        }.OrderBy(e => e, StringComparer.Ordinal).ToArray();
        Assert.Contains(DesktopSidecarProtocol.ConsoleListCommandsCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.ConsoleRunCommandCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.AppAgentBuildContextCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.AppAgentInvokeToolCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.CollaborationDeliveryEvent + ".payload", bundle.Definitions.Keys);
        Assert.Equal(sortedEvents, bundle.Events.Select(@event => @event.Event).ToArray());
        Assert.Contains(DesktopSidecarProtocol.GetOperatorStatusCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.OperatorStatusEvent + ".payload", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentsListCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentsListCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentGetCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentGetCommand + ".response", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentStoreCommand + ".request", bundle.Definitions.Keys);
        Assert.Contains(DesktopSidecarProtocol.DocumentStoreCommand + ".response", bundle.Definitions.Keys);
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
    public async Task CommandRouter_RoundTripsAppearanceSettingsThroughBridgeHandlers()
    {
        var configPath = Path.Combine(
            Path.GetTempPath(),
            "den-mcp-sidecar-bridge-tests",
            Guid.NewGuid().ToString("N"));
        var options = DesktopSidecarFixtures.CreateFixtureOptions() with { ConfigPath = configPath };

        try
        {
            using var provider = DesktopSidecarBridge.CreateServiceProvider(options);
            var router = provider.GetRequiredService<IBridgeCommandRouter>();
            var settingsService = provider.GetRequiredService<OperatorSettingsService>();

            var initialResponse = await router.DispatchAsync(Request("req_get_appearance_initial", DesktopSidecarProtocol.GetAppearanceSettingsCommand));
            var saveResponse = await router.DispatchAsync(Request(
                "req_save_appearance",
                DesktopSidecarProtocol.SaveAppearanceSettingsCommand,
                JsonSerializer.Deserialize<JsonElement>("""
                    {"theme":"graphite-dark","accent":"violet","density":"compact","bodyFont":"mono","railMode":"collapsed","consoleMode":"half","activeTab":"git"}
                    """)));
            var loadedResponse = await router.DispatchAsync(Request("req_get_appearance_loaded", DesktopSidecarProtocol.GetAppearanceSettingsCommand));

            Assert.Null(initialResponse.Error);
            Assert.Equal(OperatorAppearanceSettings.DefaultTheme, initialResponse.Result!.Value.GetProperty("theme").GetString());

            Assert.Null(saveResponse.Error);
            Assert.Equal("graphite-dark", saveResponse.Result!.Value.GetProperty("theme").GetString());
            Assert.Equal("violet", saveResponse.Result.Value.GetProperty("accent").GetString());
            Assert.Equal("compact", saveResponse.Result.Value.GetProperty("density").GetString());
            Assert.Equal("mono", saveResponse.Result.Value.GetProperty("bodyFont").GetString());
            Assert.Equal("collapsed", saveResponse.Result.Value.GetProperty("railMode").GetString());
            Assert.Equal("half", saveResponse.Result.Value.GetProperty("consoleMode").GetString());
            Assert.Equal("git", saveResponse.Result.Value.GetProperty("activeTab").GetString());

            Assert.Null(loadedResponse.Error);
            Assert.Equal(saveResponse.Result.Value.GetRawText(), loadedResponse.Result!.Value.GetRawText());
            Assert.True(File.Exists(settingsService.AppearanceSettingsPath));
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CommandRouter_CorruptAppearanceSettingsEmitsDiagnosticAndReturnsDefaults()
    {
        var configPath = Path.Combine(
            Path.GetTempPath(),
            "den-mcp-sidecar-bridge-tests",
            Guid.NewGuid().ToString("N"));
        var options = DesktopSidecarFixtures.CreateFixtureOptions() with { ConfigPath = configPath };

        try
        {
            using var provider = DesktopSidecarBridge.CreateServiceProvider(options);
            var router = provider.GetRequiredService<IBridgeCommandRouter>();
            var settingsService = provider.GetRequiredService<OperatorSettingsService>();
            var runtime = provider.GetRequiredService<OperatorRuntimeService>();

            // Write a corrupt appearance settings file
            var appearancePath = settingsService.AppearanceSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(appearancePath)!);
            File.WriteAllText(appearancePath, "{not valid json");

            var response = await router.DispatchAsync(Request("req_corrupt_appearance", DesktopSidecarProtocol.GetAppearanceSettingsCommand));

            Assert.Null(response.Error);
            Assert.Equal(OperatorAppearanceSettings.DefaultTheme, response.Result!.Value.GetProperty("theme").GetString());

            // Verify a warn diagnostic was emitted for the corrupt settings recovery
            var status = await runtime.GetStatusAsync();
            Assert.Contains(status.Diagnostics, d =>
                d.Level == "warn" &&
                d.Source == "appearance-settings" &&
                d.Message.Contains("recovered", StringComparison.OrdinalIgnoreCase));

            // Verify the corrupt file is preserved
            Assert.Equal("{not valid json", File.ReadAllText(appearancePath));
        }
        finally
        {
            if (Directory.Exists(configPath))
            {
                Directory.Delete(configPath, recursive: true);
            }
        }
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
    public void SchemaBundle_TerminalAttachRequestIncludesViewportAndReplayContract()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var bundle = DesktopSidecarBridge.CreateSchemaBundle(provider);
        var schema = bundle.Definitions[DesktopSidecarProtocol.TerminalAttachCommand + ".request"];
        var properties = schema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("terminal_protocol_version", out _));
        Assert.True(properties.TryGetProperty("viewport", out var viewport));
        Assert.True(properties.TryGetProperty("replay", out var replay));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(viewport.GetProperty("additionalProperties").GetBoolean());
        Assert.True(viewport.GetProperty("properties").TryGetProperty("cols", out _));
        Assert.True(viewport.GetProperty("properties").TryGetProperty("rows", out _));
        Assert.False(replay.GetProperty("additionalProperties").GetBoolean());
        Assert.True(replay.GetProperty("properties").TryGetProperty("after_cursor", out _));
        Assert.True(replay.GetProperty("properties").TryGetProperty("max_bytes", out _));
        Assert.True(replay.GetProperty("properties").TryGetProperty("max_chunks", out _));
    }

    [Fact]
    public void SchemaBundle_TerminalReconnectRequestIncludesViewportContract()
    {
        using var provider = DesktopSidecarBridge.CreateServiceProvider(DesktopSidecarFixtures.CreateFixtureOptions());
        var bundle = DesktopSidecarBridge.CreateSchemaBundle(provider);
        var schema = bundle.Definitions[DesktopSidecarProtocol.TerminalReconnectCommand + ".request"];
        var properties = schema.GetProperty("properties");

        Assert.True(properties.TryGetProperty("viewport", out var viewport));
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.False(viewport.GetProperty("additionalProperties").GetBoolean());
        Assert.True(viewport.GetProperty("properties").TryGetProperty("cols", out _));
        Assert.True(viewport.GetProperty("properties").TryGetProperty("rows", out _));
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
        return Request(requestId, command, BridgeJson.EmptyObject());
    }

    private static BridgeRequestFrame Request(string requestId, string command, JsonElement payload)
    {
        return new BridgeRequestFrame
        {
            SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
            RequestId = requestId,
            Command = command,
            Payload = payload,
            SentAt = new DateTimeOffset(2026, 4, 29, 12, 34, 56, TimeSpan.Zero),
        };
    }
}
