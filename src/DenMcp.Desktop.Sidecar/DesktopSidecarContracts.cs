using System.Text.Json.Serialization;
using Den.Bridge.Protocol;
using Den.Bridge.Schema;

namespace DenMcp.Desktop.Sidecar;

public static class DesktopSidecarProtocol
{
    public const string SchemaVersion = "den-desktop@2026-04-29";
    public const string SchemaBundleId = "den-desktop.sidecar@2026-04-29";
    public const string HealthCommand = "bridge.get_health";
    public const string CapabilitiesCommand = "bridge.get_capabilities";
    public const string PlaceholderRuntimeEvent = "den_desktop.runtime.placeholder";
    public const string ReadySentinelPrefix = "DEN_DESKTOP_BRIDGE_READY ";
}

public sealed record DesktopSidecarEmptyRequest;

public sealed record DesktopSidecarHealthResponse
{
    public required int ProcessId { get; init; }

    public required long UptimeMs { get; init; }

    public required string ReadyState { get; init; }

    public required string AppId { get; init; }

    public required string AppVersion { get; init; }

    public required string ConfigPath { get; init; }

    public string? LogPath { get; init; }

    public required string ProtocolVersion { get; init; }

    public required string SchemaVersion { get; init; }

    public required string SchemaBundleId { get; init; }

    public required int ActiveRequestCount { get; init; }

    public IReadOnlyList<string> DegradedSubsystems { get; init; } = Array.Empty<string>();

    public BridgeError? LastError { get; init; }
}

public sealed record DesktopSidecarCapabilitiesResponse
{
    public required string AppId { get; init; }

    public required string AppVersion { get; init; }

    public required string ProtocolVersion { get; init; }

    public required string SchemaVersion { get; init; }

    public required string SchemaBundleId { get; init; }

    public IReadOnlyList<string> SupportedTransports { get; init; } = Array.Empty<string>();

    public IReadOnlyList<BridgeCommandCapability> Commands { get; init; } = Array.Empty<BridgeCommandCapability>();

    public IReadOnlyList<BridgeEventCapability> Events { get; init; } = Array.Empty<BridgeEventCapability>();

    public IReadOnlyList<string> FeatureFlags { get; init; } = Array.Empty<string>();
}

public sealed record DesktopPlaceholderRuntimeEvent
{
    public required string Status { get; init; }

    public required string Message { get; init; }

    public required string ConfigPath { get; init; }

    public required string SchemaVersion { get; init; }
}

public sealed record DesktopSidecarReadySentinel
{
    public required int Port { get; init; }

    public required string EndpointPath { get; init; }

    public required string ProtocolVersion { get; init; }

    public required string SchemaVersion { get; init; }

    public required string SchemaBundleId { get; init; }

    public required string AppId { get; init; }

    public required string AppVersion { get; init; }
}

public sealed record DesktopSidecarWireFixture
{
    [JsonPropertyName("schema_bundle_id")]
    public required string SchemaBundleId { get; init; }

    [JsonPropertyName("schema_bundle")]
    public required BridgeSchemaBundle SchemaBundle { get; init; }

    [JsonPropertyName("frames")]
    public required DesktopSidecarWireFrames Frames { get; init; }
}

public sealed record DesktopSidecarWireFrames
{
    [JsonPropertyName("health_response")]
    public required BridgeResponseFrame HealthResponse { get; init; }

    [JsonPropertyName("capabilities_response")]
    public required BridgeResponseFrame CapabilitiesResponse { get; init; }

    [JsonPropertyName("placeholder_event")]
    public required BridgeEventFrame PlaceholderEvent { get; init; }
}
