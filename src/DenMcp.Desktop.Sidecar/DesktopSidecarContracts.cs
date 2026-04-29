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
    public const string GetOperatorStatusCommand = "den_desktop.operator.get_status";
    public const string GetSettingsCommand = "den_desktop.operator.get_settings";
    public const string SaveSettingsCommand = "den_desktop.operator.save_settings";
    public const string RefreshNowCommand = "den_desktop.operator.refresh_now";
    public const string ListLocalGitSnapshotsCommand = "den_desktop.operator.list_local_git_snapshots";
    public const string ListLocalSessionSnapshotsCommand = "den_desktop.operator.list_local_session_snapshots";
    public const string GetLatestDiffSnapshotCommand = "den_desktop.operator.get_latest_diff_snapshot";
    public const string OperatorStatusEvent = "den://operator-status";
    public const string GitSnapshotEvent = "den://git-snapshot-updated";
    public const string SessionSnapshotEvent = "den://session-snapshot-updated";
    public const string ReadySentinelPrefix = "DEN_DESKTOP_BRIDGE_READY ";
}

public sealed record DesktopSidecarEmptyRequest;

public sealed record DesktopSidecarEmptyResponse;

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

    [JsonPropertyName("operator_status_event")]
    public required BridgeEventFrame OperatorStatusEvent { get; init; }

    [JsonPropertyName("git_snapshot_event")]
    public required BridgeEventFrame GitSnapshotEvent { get; init; }

    [JsonPropertyName("session_snapshot_event")]
    public required BridgeEventFrame SessionSnapshotEvent { get; init; }
}
