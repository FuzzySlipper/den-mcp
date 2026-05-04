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
    public const string GetAppearanceSettingsCommand = "den_desktop.operator.get_appearance_settings";
    public const string SaveAppearanceSettingsCommand = "den_desktop.operator.save_appearance_settings";
    public const string RefreshNowCommand = "den_desktop.operator.refresh_now";
    public const string ListLocalGitSnapshotsCommand = "den_desktop.operator.list_local_git_snapshots";
    public const string ListLocalSessionSnapshotsCommand = "den_desktop.operator.list_local_session_snapshots";
    public const string GetLatestDiffSnapshotCommand = "den_desktop.operator.get_latest_diff_snapshot";
    public const string OperatorStatusEvent = "den://operator-status";
    public const string GitSnapshotEvent = "den://git-snapshot-updated";
    public const string SessionSnapshotEvent = "den://session-snapshot-updated";

    // Terminal protocol commands (task #1010, spec #945)
    public const string TerminalCreateSessionCommand = "den_desktop.terminal.create_session";
    public const string TerminalListSessionsCommand = "den_desktop.terminal.list_sessions";
    public const string TerminalReadActivityCommand = "den_desktop.terminal.read_activity";
    public const string TerminalAttachCommand = "den_desktop.terminal.attach";
    public const string TerminalDetachCommand = "den_desktop.terminal.detach";
    public const string TerminalSendInputCommand = "den_desktop.terminal.send_input";
    public const string TerminalResizeCommand = "den_desktop.terminal.resize";
    public const string TerminalTerminateCommand = "den_desktop.terminal.terminate";
    public const string TerminalReconnectCommand = "den_desktop.terminal.reconnect";
    public const string TerminalAckOutputCommand = "den_desktop.terminal.ack_output";

    // Terminal protocol events per R945-4: dotted-name convention
    public const string TerminalSessionListEvent = "den.terminal.session_list_updated";
    public const string TerminalSessionStatusEvent = "den.terminal.session_status_changed";
    public const string TerminalOutputEvent = "den.terminal.output";
    public const string TerminalReplayCompleteEvent = "den.terminal.replay_complete";
    public const string TerminalExitEvent = "den.terminal.exit";
    public const string TerminalErrorEvent = "den.terminal.error";
    public const string TerminalHeartbeatEvent = "den.terminal.heartbeat";
    public const string TerminalBackpressureEvent = "den.terminal.backpressure";

    // Console command protocol (task #914)
    public const string ConsoleListCommandsCommand = "den_desktop.console.list_commands";
    public const string ConsoleRunCommandCommand = "den_desktop.console.run_command";

    // App-agent context/tool bridge foundation (task #1023)
    public const string AppAgentBuildContextCommand = "den_desktop.app_agent.build_context";
    public const string AppAgentListToolsCommand = "den_desktop.app_agent.list_tools";
    public const string AppAgentInvokeToolCommand = "den_desktop.app_agent.invoke_tool";
    public const string AppAgentCancelRequestCommand = "den_desktop.app_agent.cancel_request";

    // Tasks/orchestrator dashboard projection (task #1028)
    public const string TasksGetDashboardSnapshotCommand = "den_desktop.tasks.get_dashboard_snapshot";

    // Messages tab projection (task #1092)
    public const string MessagesGetSnapshotCommand = "den_desktop.messages.get_snapshot";

    // Task update bridge command (task #1152)
    public const string TaskUpdateCommand = "den_desktop.tasks.update";

    // Documents tab (task #1147)
    public const string DocumentsListCommand = "den_desktop.documents.list";
    public const string DocumentGetCommand = "den_desktop.documents.get";
    public const string DocumentStoreCommand = "den_desktop.documents.store";

    // Collaboration response delivery (task #920)
    public const string CollaborationSendCompiledResponseCommand = "den_desktop.collaboration.send_compiled_response";

    public const string AppAgentRunStateEvent = "den.app_agent.run_state_changed";
    public const string AppAgentToolCallStateEvent = "den.app_agent.tool_call_state_changed";
    public const string CollaborationDeliveryEvent = "den.collaboration.delivery_state_changed";

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
