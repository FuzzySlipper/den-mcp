using Den.Bridge.Abstractions;
using Den.Bridge.Hosting;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;
using Den.Bridge.Schema;
using Den.Bridge.Transport.WebSockets;
using Microsoft.Extensions.DependencyInjection;

namespace DenMcp.Desktop.Sidecar;

public static class DesktopSidecarBridge
{
    public static ServiceProvider CreateServiceProvider(DesktopSidecarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        // The bridge runtime stores settings under options.ConfigPath (default ~/.den-mcp/desktop).
        // This diverges from OperatorSettingsStorage.DefaultSettingsPath() (~/.config/den-desktop),
        // but the bridge always provides an explicit path through DI so the runtime path is
        // the effective one. See review finding R1000-2.
        services.AddSingleton(_ => new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(Path.Combine(options.ConfigPath, OperatorSettingsStorage.SettingsFileName))));
        services.AddSingleton(_ => new DenHttpClient());
        services.AddSingleton<IGitCommandRunner, SystemGitCommandRunner>();
        services.AddSingleton<GitSnapshotBuilder>();
        services.AddSingleton<PiSessionSnapshotBuilder>();
        services.AddSingleton<OperatorSessionRegistry>();
        services.AddSingleton<OperatorSessionLeaseStore>();
        services.AddSingleton<ITmuxCommandRunner, SystemTmuxCommandRunner>();
        services.AddSingleton<TmuxOperatorSessionService>();
        services.AddSingleton<IDirectPtyBackend, PortaDirectPtyBackend>();
        services.AddSingleton<DirectPtyOperatorSessionService>();
        services.AddSingleton<TerminalOperatorSessionService>();
        services.AddSingleton<DesktopSidecarRuntimeState>();
        services.AddSingleton<OperatorRuntimeBridgeEventSink>();
        services.AddSingleton<IOperatorRuntimeEventSink>(sp => sp.GetRequiredService<OperatorRuntimeBridgeEventSink>());
        services.AddSingleton<OperatorRuntimeService>();
        services.AddSingleton<IConsoleCommandRunner, ConsoleCommandRunner>();
        services.AddSingleton<AppAgentToolRegistry>();
        services.AddSingleton<AppAgentAuditService>();
        services.AddSingleton<AppAgentContextBuilder>();
        services.AddSingleton<AppAgentService>();
        services.AddSingleton<CollaborationResponseDeliveryService>();
        services.AddSingleton<TasksDashboardProjectionService>();
        services.AddSingleton<MessagesProjectionService>();
        services.AddSingleton<DocumentsListHandler>();
        services.AddSingleton<DocumentGetHandler>();
        services.AddSingleton<DocumentStoreHandler>();
        services.AddSingleton<TaskUpdateHandler>();
        services.AddBridgeHost(
            ConfigureRegistry,
            host =>
            {
                host.AppId = options.AppId;
                host.AppVersion = options.AppVersion;
                host.SchemaVersion = DesktopSidecarProtocol.SchemaVersion;
                host.SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId;
                host.SupportedTransports = new[] { WebSocketBridgeTransportNames.LoopbackWebSocket };
                host.FeatureFlags = new[] { "operator_runtime", "typed_runtime_bridge", "tmux_operator_sessions", "direct_pty_operator_sessions", "app_agent_bridge_foundation", "tasks_dashboard_projection", "messages_tab_projection", "documents_tab" };
            });

        return services.BuildServiceProvider(validateScopes: true);
    }

    public static void ConfigureRegistry(BridgeRegistryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .RegisterCommand<DesktopSidecarEmptyRequest, DesktopSidecarHealthResponse, GetHealthHandler>(
                DesktopSidecarProtocol.HealthCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, DesktopSidecarCapabilitiesResponse, GetCapabilitiesHandler>(
                DesktopSidecarProtocol.CapabilitiesCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, OperatorStatus, GetOperatorStatusHandler>(
                DesktopSidecarProtocol.GetOperatorStatusCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, OperatorSettings, GetOperatorSettingsHandler>(
                DesktopSidecarProtocol.GetSettingsCommand)
            .RegisterCommand<SaveOperatorSettingsRequest, OperatorSettings, SaveOperatorSettingsHandler>(
                DesktopSidecarProtocol.SaveSettingsCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, OperatorAppearanceSettings, GetAppearanceSettingsHandler>(
                DesktopSidecarProtocol.GetAppearanceSettingsCommand)
            .RegisterCommand<SaveOperatorAppearanceSettingsRequest, OperatorAppearanceSettings, SaveAppearanceSettingsHandler>(
                DesktopSidecarProtocol.SaveAppearanceSettingsCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, DesktopSidecarEmptyResponse, RefreshNowHandler>(
                DesktopSidecarProtocol.RefreshNowCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, LocalSnapshotList, ListLocalSnapshotsHandler>(
                DesktopSidecarProtocol.ListLocalGitSnapshotsCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, LocalSessionSnapshotList, ListLocalSessionSnapshotsHandler>(
                DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand)
            .RegisterCommand<LatestDiffSnapshotRequest, DesktopDiffSnapshotLatestResult, GetLatestDiffSnapshotHandler>(
                DesktopSidecarProtocol.GetLatestDiffSnapshotCommand)
            // Terminal protocol commands (task #1010, spec #945)
            .RegisterCommand<TerminalCreateSessionRequest, TerminalCreateSessionResponse, TerminalCreateSessionHandler>(
                DesktopSidecarProtocol.TerminalCreateSessionCommand)
            .RegisterCommand<TerminalListSessionsRequest, TerminalListSessionsResponse, TerminalListSessionsHandler>(
                DesktopSidecarProtocol.TerminalListSessionsCommand)
            .RegisterCommand<TerminalReadActivityRequest, TerminalReadActivityResponse, TerminalReadActivityHandler>(
                DesktopSidecarProtocol.TerminalReadActivityCommand)
            .RegisterCommand<TerminalAttachRequest, TerminalAttachResponse, TerminalAttachHandler>(
                DesktopSidecarProtocol.TerminalAttachCommand)
            .RegisterCommand<TerminalDetachRequest, TerminalDetachResponse, TerminalDetachHandler>(
                DesktopSidecarProtocol.TerminalDetachCommand)
            .RegisterCommand<TerminalSendInputRequest, TerminalSendInputResponse, TerminalSendInputHandler>(
                DesktopSidecarProtocol.TerminalSendInputCommand)
            .RegisterCommand<TerminalResizeRequest, TerminalResizeResponse, TerminalResizeHandler>(
                DesktopSidecarProtocol.TerminalResizeCommand)
            .RegisterCommand<TerminalTerminateRequest, TerminalTerminateResponse, TerminalTerminateHandler>(
                DesktopSidecarProtocol.TerminalTerminateCommand)
            .RegisterCommand<TerminalReconnectRequest, TerminalAttachResponse, TerminalReconnectHandler>(
                DesktopSidecarProtocol.TerminalReconnectCommand)
            .RegisterCommand<TerminalAckOutputRequest, TerminalAckOutputResponse, TerminalAckOutputHandler>(
                DesktopSidecarProtocol.TerminalAckOutputCommand)
            // Console command protocol (task #914)
            .RegisterCommand<DesktopSidecarEmptyRequest, ConsoleCommandListResponse, ConsoleListCommandsHandler>(
                DesktopSidecarProtocol.ConsoleListCommandsCommand)
            .RegisterCommand<ConsoleCommandRunRequest, ConsoleCommandRunResponse, ConsoleRunCommandHandler>(
                DesktopSidecarProtocol.ConsoleRunCommandCommand,
                config => { config.SupportsProgress = true; })
            // App-agent context/tool bridge foundation (task #1023)
            .RegisterCommand<AppAgentBuildContextRequest, AppAgentBuildContextResponse, AppAgentBuildContextHandler>(
                DesktopSidecarProtocol.AppAgentBuildContextCommand,
                config => { config.SupportsCancellation = true; })
            .RegisterCommand<AppAgentListToolsRequest, AppAgentListToolsResponse, AppAgentListToolsHandler>(
                DesktopSidecarProtocol.AppAgentListToolsCommand)
            .RegisterCommand<AppAgentInvokeToolRequest, AppAgentInvokeToolResponse, AppAgentInvokeToolHandler>(
                DesktopSidecarProtocol.AppAgentInvokeToolCommand,
                config => { config.SupportsCancellation = true; config.SupportsProgress = true; })
            .RegisterCommand<AppAgentCancelRequest, AppAgentCancelResponse, AppAgentCancelRequestHandler>(
                DesktopSidecarProtocol.AppAgentCancelRequestCommand)
            .RegisterCommand<TasksDashboardSnapshotRequest, TasksDashboardSnapshot, TasksDashboardSnapshotHandler>(
                DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand,
                config => { config.SupportsCancellation = true; })
            // Messages tab projection (task #1092)
            .RegisterCommand<MessagesSnapshotRequest, MessagesSnapshot, MessagesSnapshotHandler>(
                DesktopSidecarProtocol.MessagesGetSnapshotCommand,
                config => { config.SupportsCancellation = true; })
            // Documents tab (task #1147)
            .RegisterCommand<DocumentsListRequest, DocumentsListResponse, DocumentsListHandler>(
                DesktopSidecarProtocol.DocumentsListCommand)
            .RegisterCommand<DocumentGetRequest, DocumentGetResponse, DocumentGetHandler>(
                DesktopSidecarProtocol.DocumentGetCommand)
            .RegisterCommand<DocumentStoreRequest, DocumentStoreResponse, DocumentStoreHandler>(
                DesktopSidecarProtocol.DocumentStoreCommand)
            // Task update (task #1152)
            .RegisterCommand<TaskUpdateRequest, TaskUpdateResponse, TaskUpdateHandler>(
                DesktopSidecarProtocol.TaskUpdateCommand)
            // Collaboration response delivery (task #920)
            .RegisterCommand<CollaborationSendCompiledResponseRequest, CollaborationSendCompiledResponseResponse, CollaborationSendCompiledResponseHandler>(
                DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand)
            .RegisterEvent<OperatorStatus>(DesktopSidecarProtocol.OperatorStatusEvent)
            .RegisterEvent<IReadOnlyList<LocalGitSnapshot>>(DesktopSidecarProtocol.GitSnapshotEvent)
            .RegisterEvent<IReadOnlyList<LocalSessionSnapshot>>(DesktopSidecarProtocol.SessionSnapshotEvent)
            // Terminal protocol events (task #1010/#909, dot-convention names per R945-4)
            .RegisterEvent<TerminalOutputEvent>(DesktopSidecarProtocol.TerminalOutputEvent)
            .RegisterEvent<TerminalReplayCompleteEvent>(DesktopSidecarProtocol.TerminalReplayCompleteEvent)
            .RegisterEvent<TerminalExitEvent>(DesktopSidecarProtocol.TerminalExitEvent)
            .RegisterEvent<TerminalProtocolErrorEvent>(DesktopSidecarProtocol.TerminalErrorEvent)
            .RegisterEvent<TerminalHeartbeatEvent>(DesktopSidecarProtocol.TerminalHeartbeatEvent)
            .RegisterEvent<TerminalBackpressureEvent>(DesktopSidecarProtocol.TerminalBackpressureEvent)
            .RegisterEvent<TerminalSessionEvent>(DesktopSidecarProtocol.TerminalSessionStatusEvent)
            .RegisterEvent<TerminalListSessionsResponse>(DesktopSidecarProtocol.TerminalSessionListEvent)
            .RegisterEvent<AppAgentRunStateEvent>(DesktopSidecarProtocol.AppAgentRunStateEvent)
            .RegisterEvent<AppAgentToolCallStateEvent>(DesktopSidecarProtocol.AppAgentToolCallStateEvent)
            .RegisterEvent<CollaborationDeliveryEvent>(DesktopSidecarProtocol.CollaborationDeliveryEvent);
    }

    public static BridgeSchemaBundle CreateSchemaBundle(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return BridgeSchemaBundleFactory.Create(
            DesktopSidecarProtocol.SchemaBundleId,
            DesktopSidecarProtocol.SchemaVersion,
            serviceProvider.GetRequiredService<IBridgeCommandRegistry>(),
            serviceProvider.GetRequiredService<IBridgeEventRegistry>(),
            PayloadSchemas());
    }

    public static IReadOnlyList<BridgeNamedSchema> PayloadSchemas()
    {
        return new[]
        {
            Schema("console_command_definition", ConsoleCommandDefinitionSchema),
            Schema("console_command_line", ConsoleCommandLineSchema),
            Schema(DesktopSidecarProtocol.HealthCommand + ".request", """
                {"type":"object","additionalProperties":false}
                """),
            Schema(DesktopSidecarProtocol.HealthCommand + ".response", """
                {"type":"object","additionalProperties":false,"required":["process_id","uptime_ms","ready_state","app_id","app_version","config_path","protocol_version","schema_version","schema_bundle_id","active_request_count","degraded_subsystems"],"properties":{"process_id":{"type":"integer"},"uptime_ms":{"type":"integer"},"ready_state":{"type":"string"},"app_id":{"type":"string"},"app_version":{"type":"string"},"config_path":{"type":"string"},"log_path":{"type":"string"},"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"schema_bundle_id":{"type":"string"},"active_request_count":{"type":"integer"},"degraded_subsystems":{"type":"array","items":{"type":"string"}},"last_error":{"$ref":"bridge.error"}}}
                """),
            Schema(DesktopSidecarProtocol.CapabilitiesCommand + ".request", """
                {"type":"object","additionalProperties":false}
                """),
            Schema(DesktopSidecarProtocol.CapabilitiesCommand + ".response", """
                {"type":"object","additionalProperties":false,"required":["app_id","app_version","protocol_version","schema_version","schema_bundle_id","supported_transports","commands","events","feature_flags"],"properties":{"app_id":{"type":"string"},"app_version":{"type":"string"},"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"schema_bundle_id":{"type":"string"},"supported_transports":{"type":"array","items":{"type":"string"}},"commands":{"type":"array","items":{"$ref":"bridge.command_capability"}},"events":{"type":"array","items":{"$ref":"bridge.event_capability"}},"feature_flags":{"type":"array","items":{"type":"string"}}}}
                """),
            Schema(DesktopSidecarProtocol.GetOperatorStatusCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.GetOperatorStatusCommand + ".response", OperatorStatusSchema),
            Schema(DesktopSidecarProtocol.GetSettingsCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.GetSettingsCommand + ".response", OperatorSettingsSchema),
            Schema(DesktopSidecarProtocol.SaveSettingsCommand + ".request", SaveOperatorSettingsSchema),
            Schema(DesktopSidecarProtocol.SaveSettingsCommand + ".response", OperatorSettingsSchema),
            Schema(DesktopSidecarProtocol.GetAppearanceSettingsCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.GetAppearanceSettingsCommand + ".response", OperatorAppearanceSettingsSchema),
            Schema(DesktopSidecarProtocol.SaveAppearanceSettingsCommand + ".request", SaveOperatorAppearanceSettingsSchema),
            Schema(DesktopSidecarProtocol.SaveAppearanceSettingsCommand + ".response", OperatorAppearanceSettingsSchema),
            Schema(DesktopSidecarProtocol.RefreshNowCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.RefreshNowCommand + ".response", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.ListLocalGitSnapshotsCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.ListLocalGitSnapshotsCommand + ".response", """
                {"type":"object","additionalProperties":true,"required":["scopes","snapshots"],"properties":{"scopes":{"type":"array","items":{"type":"object","additionalProperties":true}},"snapshots":{"type":"array","items":{"type":"object","additionalProperties":true}}}}
                """),
            Schema(DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand + ".response", """
                {"type":"object","additionalProperties":true,"required":["snapshots"],"properties":{"snapshots":{"type":"array","items":{"type":"object","additionalProperties":true}}}}
                """),
            Schema(DesktopSidecarProtocol.GetLatestDiffSnapshotCommand + ".request", LatestDiffSnapshotRequestSchema),
            Schema(DesktopSidecarProtocol.GetLatestDiffSnapshotCommand + ".response", """
                {"type":"object","additionalProperties":true}
                """),
            Schema(DesktopSidecarProtocol.OperatorStatusEvent + ".payload", OperatorStatusSchema),
            Schema(DesktopSidecarProtocol.GitSnapshotEvent + ".payload", """
                {"type":"array","items":{"type":"object","additionalProperties":true}}
                """),
            Schema(DesktopSidecarProtocol.SessionSnapshotEvent + ".payload", """
                {"type":"array","items":{"type":"object","additionalProperties":true}}
                """),
            // Terminal protocol command schemas (task #1010/#909)
            Schema(DesktopSidecarProtocol.TerminalCreateSessionCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["project_id"],"properties":{"project_id":{"type":"string"},"task_id":{"type":["integer","null"]},"workspace_id":{"type":["string","null"]},"title":{"type":["string","null"]},"cwd":{"type":["string","null"]},"backend":{"type":"string"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalCreateSessionCommand + ".response", TerminalCreateSessionResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalListSessionsCommand + ".request", """
                {"type":"object","additionalProperties":false,"properties":{"kind":{"type":["string","null"]},"backend":{"type":["string","null"]},"status":{"type":["string","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalListSessionsCommand + ".response", TerminalListSessionsResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalReadActivityCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"after_cursor":{"type":["string","null"]},"limit":{"type":"integer"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalReadActivityCommand + ".response", TerminalReadActivityResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalAttachCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"terminal_protocol_version":{"type":"string"},"session_id":{"type":"string"},"mode":{"type":"string"},"client_id":{"type":["string","null"]},"viewport":{"type":["object","null"],"additionalProperties":false,"properties":{"cols":{"type":"integer"},"rows":{"type":"integer"}}},"replay":{"type":["object","null"],"additionalProperties":false,"properties":{"after_cursor":{"type":["string","null"]},"max_bytes":{"type":"integer"},"max_chunks":{"type":"integer"}}}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalAttachCommand + ".response", TerminalAttachResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalDetachCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["stream_id","session_id"],"properties":{"stream_id":{"type":"string"},"session_id":{"type":"string"},"reason":{"type":["string","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalDetachCommand + ".response", TerminalDetachResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalSendInputCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id","data"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"input_id":{"type":["string","null"]},"encoding":{"type":"string"},"data":{"type":"string"},"byte_count":{"type":"integer"},"expected_lease_generation":{"type":["integer","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalSendInputCommand + ".response", TerminalSendInputResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalResizeCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id","cols","rows"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"cols":{"type":"integer"},"rows":{"type":"integer"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalResizeCommand + ".response", TerminalResizeResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalTerminateCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"mode":{"type":"string"},"reason":{"type":["string","null"]},"expected_lease_generation":{"type":["integer","null"]},"requested_by":{"type":["string","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalTerminateCommand + ".response", TerminalTerminateResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalReconnectCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"previous_stream_id":{"type":["string","null"]},"last_seen_cursor":{"type":["string","null"]},"viewport":{"type":["object","null"],"additionalProperties":false,"properties":{"cols":{"type":"integer"},"rows":{"type":"integer"}}}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalReconnectCommand + ".response", TerminalAttachResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalAckOutputCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"ack_cursor":{"type":["string","null"]},"received_bytes":{"type":"integer"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalAckOutputCommand + ".response", TerminalAckOutputResponseSchema),
            // Console command protocol schemas (task #914)
            Schema(DesktopSidecarProtocol.ConsoleListCommandsCommand + ".request", EmptyObjectSchema),
            Schema(DesktopSidecarProtocol.ConsoleListCommandsCommand + ".response", ConsoleCommandListResponseSchema),
            Schema(DesktopSidecarProtocol.ConsoleRunCommandCommand + ".request", ConsoleCommandRunRequestSchema),
            Schema(DesktopSidecarProtocol.ConsoleRunCommandCommand + ".response", ConsoleCommandRunResponseSchema),
            // App-agent context/tool bridge schemas (task #1023)
            Schema("app_agent_selection", AppAgentSelectionSchema),
            Schema("app_agent_tool_definition", AppAgentToolDefinitionSchema),
            Schema("app_agent_audit_correlation", AppAgentAuditCorrelationSchema),
            Schema(DesktopSidecarProtocol.AppAgentBuildContextCommand + ".request", AppAgentBuildContextRequestSchema),
            Schema(DesktopSidecarProtocol.AppAgentBuildContextCommand + ".response", AppAgentBuildContextResponseSchema),
            Schema(DesktopSidecarProtocol.AppAgentListToolsCommand + ".request", AppAgentListToolsRequestSchema),
            Schema(DesktopSidecarProtocol.AppAgentListToolsCommand + ".response", AppAgentListToolsResponseSchema),
            Schema(DesktopSidecarProtocol.AppAgentInvokeToolCommand + ".request", AppAgentInvokeToolRequestSchema),
            Schema(DesktopSidecarProtocol.AppAgentInvokeToolCommand + ".response", AppAgentInvokeToolResponseSchema),
            Schema(DesktopSidecarProtocol.AppAgentCancelRequestCommand + ".request", AppAgentCancelRequestSchema),
            Schema(DesktopSidecarProtocol.AppAgentCancelRequestCommand + ".response", AppAgentCancelResponseSchema),
            Schema(DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand + ".request", TasksDashboardSnapshotRequestSchema),
            Schema(DesktopSidecarProtocol.TasksGetDashboardSnapshotCommand + ".response", TasksDashboardSnapshotResponseSchema),
            Schema(DesktopSidecarProtocol.MessagesGetSnapshotCommand + ".request", MessagesSnapshotRequestSchema),
            Schema(DesktopSidecarProtocol.MessagesGetSnapshotCommand + ".response", MessagesSnapshotResponseSchema),
            // Documents tab (task #1147)
            Schema(DesktopSidecarProtocol.DocumentsListCommand + ".request", DocumentsListRequestSchema),
            Schema(DesktopSidecarProtocol.DocumentsListCommand + ".response", DocumentsListResponseSchema),
            Schema(DesktopSidecarProtocol.DocumentGetCommand + ".request", DocumentGetRequestSchema),
            Schema(DesktopSidecarProtocol.DocumentGetCommand + ".response", DocumentGetResponseSchema),
            Schema(DesktopSidecarProtocol.DocumentStoreCommand + ".request", DocumentStoreRequestSchema),
            Schema(DesktopSidecarProtocol.DocumentStoreCommand + ".response", DocumentStoreResponseSchema),
            // Task update schemas (task #1152)
            Schema(DesktopSidecarProtocol.TaskUpdateCommand + ".request", TaskUpdateRequestSchema),
            Schema(DesktopSidecarProtocol.TaskUpdateCommand + ".response", TaskUpdateResponseSchema),
            Schema(DesktopSidecarProtocol.AppAgentRunStateEvent + ".payload", AppAgentRunStateEventSchema),
            Schema(DesktopSidecarProtocol.AppAgentToolCallStateEvent + ".payload", AppAgentToolCallStateEventSchema),
            // Collaboration response delivery schemas (task #920)
            Schema(DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand + ".request", CollaborationSendCompiledResponseRequestSchema),
            Schema(DesktopSidecarProtocol.CollaborationSendCompiledResponseCommand + ".response", CollaborationSendCompiledResponseResponseSchema),
            Schema(DesktopSidecarProtocol.CollaborationDeliveryEvent + ".payload", CollaborationDeliveryEventPayloadSchema),
            // Terminal protocol event schemas
            Schema(DesktopSidecarProtocol.TerminalOutputEvent + ".payload", TerminalOutputEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalReplayCompleteEvent + ".payload", TerminalReplayCompleteEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalExitEvent + ".payload", TerminalExitEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalErrorEvent + ".payload", TerminalErrorEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalHeartbeatEvent + ".payload", TerminalHeartbeatEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalBackpressureEvent + ".payload", TerminalBackpressureEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalSessionStatusEvent + ".payload", TerminalSessionEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalSessionListEvent + ".payload", TerminalListSessionsResponseSchema),
        };
    }

    private const string EmptyObjectSchema = """
        {"type":"object","additionalProperties":false}
        """;

    private const string OperatorSettingsSchema = """
        {"type":"object","additionalProperties":false,"required":["denBaseUrl","sourceInstanceId","pollIntervalSeconds","maxChangedFiles","includeHiddenSpaces","includeArchivedSpaces"],"properties":{"denBaseUrl":{"type":"string"},"sourceInstanceId":{"type":"string"},"sourceDisplayName":{"type":["string","null"]},"pollIntervalSeconds":{"type":"integer"},"maxChangedFiles":{"type":"integer"},"includeHiddenSpaces":{"type":"boolean"},"includeArchivedSpaces":{"type":"boolean"}}}
        """;

    private const string SaveOperatorSettingsSchema = """
        {"type":"object","additionalProperties":false,"required":["denBaseUrl"],"properties":{"denBaseUrl":{"type":"string"},"sourceDisplayName":{"type":["string","null"]},"pollIntervalSeconds":{"type":"integer"},"maxChangedFiles":{"type":"integer"},"includeHiddenSpaces":{"type":"boolean"},"includeArchivedSpaces":{"type":"boolean"}}}
        """;

    private const string OperatorAppearanceSettingsSchema = """
        {"type":"object","additionalProperties":false,"required":["theme","accent","density","bodyFont","railMode","consoleMode","activeTab"],"properties":{"theme":{"type":"string"},"accent":{"type":"string"},"density":{"type":"string"},"bodyFont":{"type":"string"},"railMode":{"type":"string"},"consoleMode":{"type":"string"},"activeTab":{"type":"string"}}}
        """;

    private const string SaveOperatorAppearanceSettingsSchema = """
        {"type":"object","additionalProperties":false,"properties":{"theme":{"type":["string","null"]},"accent":{"type":["string","null"]},"density":{"type":["string","null"]},"bodyFont":{"type":["string","null"]},"railMode":{"type":["string","null"]},"consoleMode":{"type":["string","null"]},"activeTab":{"type":["string","null"]}}}
        """;

    private const string LatestDiffSnapshotRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["projectId","rootPath","sourceInstanceId"],"properties":{"projectId":{"type":"string"},"taskId":{"type":["integer","null"]},"workspaceId":{"type":["string","null"]},"rootPath":{"type":"string"},"path":{"type":["string","null"]},"sourceInstanceId":{"type":"string"}}}
        """;

    private const string OperatorStatusSchema = """
        {"type":"object","additionalProperties":false,"required":["phase","denConnection","sourceInstanceId","denBaseUrl","observerStatuses","diagnostics","projectCount","workspaceCount","localSnapshotCount","localSessionSnapshotCount"],"properties":{"phase":{"type":"string"},"denConnection":{"type":"object","additionalProperties":false,"required":["state"],"properties":{"state":{"type":"string"},"message":{"type":["string","null"]},"lastSuccessAt":{"type":["string","null"]},"lastFailureAt":{"type":["string","null"]},"nextRetryAt":{"type":["string","null"]}}},"sourceInstanceId":{"type":"string"},"denBaseUrl":{"type":"string"},"lastSyncAt":{"type":["string","null"]},"lastPublishAt":{"type":["string","null"]},"observerStatuses":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["kind","state","scopesScanned","warningCount"],"properties":{"kind":{"type":"string"},"state":{"type":"string"},"scopesScanned":{"type":"integer"},"warningCount":{"type":"integer"},"lastRunAt":{"type":["string","null"]},"nextRunAt":{"type":["string","null"]}}}},"diagnostics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["level","source","message","observedAt"],"properties":{"level":{"type":"string"},"source":{"type":"string"},"message":{"type":"string"},"observedAt":{"type":"string"}}}},"projectCount":{"type":"integer"},"workspaceCount":{"type":"integer"},"localSnapshotCount":{"type":"integer"},"localSessionSnapshotCount":{"type":"integer"},"spaceCount":{"type":"integer"},"spaces":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["id","name","kind","visibility"],"properties":{"id":{"type":"string"},"name":{"type":"string"},"kind":{"type":"string"},"visibility":{"type":"string"},"owner":{"type":["string","null"]},"rootPath":{"type":["string","null"]},"description":{"type":["string","null"]},"createdAt":{"type":["string","null"]},"updatedAt":{"type":["string","null"]}}}}}}
        """;

    // ── Terminal response/event schemas (task #1010, matching DTOs from TerminalBridgeDtos.cs) ──

    private const string TerminalCreateSessionResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["session"],"properties":{"session":{"type":"object","additionalProperties":true,"required":["session_id"],"properties":{"session_id":{"type":"string"},"backend":{"type":"string"},"status":{"type":"string"}}}}}
        """;

    private const string TerminalListSessionsResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["sessions","count"],"properties":{"sessions":{"type":"array","items":{"type":"object","additionalProperties":true,"required":["session_id"],"properties":{"session_id":{"type":"string"},"title":{"type":["string","null"]},"display_name":{"type":["string","null"]},"kind":{"type":"string"},"backend":{"type":"string"},"status":{"type":"string"},"can_read_activity":{"type":"boolean"},"can_send_input":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_attach":{"type":"boolean"},"can_open_external_attach":{"type":"boolean"},"can_deliver_compiled_response":{"type":"boolean"},"persistence_kind":{"type":["string","null"]},"ownership_kind":{"type":["string","null"]}}}},"count":{"type":"integer"}}}
        """;

    private const string TerminalReadActivityResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","items","truncated"],"properties":{"session_id":{"type":"string"},"items":{"type":"array","items":{"type":"object","additionalProperties":true,"properties":{"kind":{"type":["string","null"]},"role":{"type":["string","null"]},"tool":{"type":["string","null"]},"summary":{"type":["string","null"]},"timestamp":{"type":["string","null"]}}}},"next_cursor":{"type":["string","null"]},"truncated":{"type":"boolean"}}}
        """;

    private const string TerminalAttachResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["stream_id","session_id"],"properties":{"stream_id":{"type":"string"},"session_id":{"type":"string"},"attached_at":{"type":"string"},"start_cursor":{"type":"string"},"replay_available_from":{"type":"string"},"replay_gap":{"type":"boolean"},"capabilities":{"type":"object","additionalProperties":false,"required":["can_send_input","can_resize","can_detach","can_terminate","can_stream_terminal"],"properties":{"can_send_input":{"type":"boolean"},"can_resize":{"type":"boolean"},"can_detach":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_stream_terminal":{"type":"boolean"}}},"viewport_limits":{"type":["object","null"],"additionalProperties":false,"properties":{"min_cols":{"type":"integer"},"max_cols":{"type":"integer"},"min_rows":{"type":"integer"},"max_rows":{"type":"integer"}}},"limits":{"type":"object","additionalProperties":false,"properties":{"output_chunk_max_bytes":{"type":"integer"},"input_chunk_max_bytes":{"type":"integer"},"session_replay_max_bytes":{"type":"integer"},"subscriber_queue_max_bytes":{"type":"integer"},"ack_after_bytes":{"type":"integer"},"ack_after_millis":{"type":"integer"},"heartbeat_interval_ms":{"type":"integer"}}},"external_attach":{"type":["object","null"],"additionalProperties":false,"properties":{"available":{"type":"boolean"},"command":{"type":["string","null"]},"description":{"type":["string","null"]}}}}}
        """;

    private const string TerminalDetachResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["detached","backend_preserved"],"properties":{"detached":{"type":"boolean"},"backend_preserved":{"type":"boolean"}}}
        """;

    private const string TerminalSendInputResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["accepted","written_bytes"],"properties":{"accepted":{"type":"boolean"},"input_id":{"type":["string","null"]},"written_bytes":{"type":"integer"}}}
        """;

    private const string TerminalResizeResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["accepted","cols","rows"],"properties":{"accepted":{"type":"boolean"},"cols":{"type":"integer"},"rows":{"type":"integer"}}}
        """;

    private const string TerminalTerminateResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["accepted","mode"],"properties":{"accepted":{"type":"boolean"},"mode":{"type":"string"},"terminal_event_id":{"type":["string","null"]}}}
        """;

    private const string TerminalAckOutputResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["accepted"],"properties":{"accepted":{"type":"boolean"}}}
        """;

    private const string TerminalOutputEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["terminal_protocol_version","stream_id","session_id","terminal_sequence","stream_cursor","chunk_id","encoding","data","byte_count"],"properties":{"terminal_protocol_version":{"type":"string"},"stream_id":{"type":"string"},"session_id":{"type":"string"},"terminal_sequence":{"type":"integer"},"stream_cursor":{"type":"string"},"chunk_id":{"type":"string"},"origin":{"type":["string","null"]},"encoding":{"type":"string"},"data":{"type":"string"},"byte_count":{"type":"integer"},"cols":{"type":["integer","null"]},"rows":{"type":["integer","null"]},"emitted_at":{"type":"string"},"truncated":{"type":"boolean"},"redacted":{"type":"boolean"}}}
        """;

    private const string TerminalReplayCompleteEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["stream_id","session_id","replay_gap","dropped_bytes_before_start"],"properties":{"stream_id":{"type":"string"},"session_id":{"type":"string"},"from_cursor":{"type":["string","null"]},"to_cursor":{"type":["string","null"]},"replay_gap":{"type":"boolean"},"dropped_bytes_before_start":{"type":"integer"}}}
        """;

    private const string TerminalExitEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","reason","exited_at"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"exit_code":{"type":["integer","null"]},"exit_signal":{"type":["integer","null"]},"reason":{"type":"string"},"exited_at":{"type":"string"}}}
        """;

    private const string TerminalErrorEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","code","message","retryable","details"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"code":{"type":"string"},"message":{"type":"string"},"retryable":{"type":"boolean"},"details":{"type":"object","additionalProperties":{"type":"string"}}}}
        """;

    private const string TerminalHeartbeatEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","backend_status","queue_bytes","paused"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"stream_cursor":{"type":["string","null"]},"backend_status":{"type":"string"},"last_activity_at":{"type":["string","null"]},"queue_bytes":{"type":"integer"},"paused":{"type":"boolean"}}}
        """;

    private const string TerminalBackpressureEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","state","queue_bytes","dropped_bytes"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"state":{"type":"string"},"queue_bytes":{"type":"integer"},"dropped_bytes":{"type":"integer"},"next_action":{"type":["string","null"]}}}
        """;

    private const string TerminalSessionEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["terminal_protocol_version","session_id"],"properties":{"terminal_protocol_version":{"type":"string"},"session_id":{"type":"string"},"status":{"type":["string","null"]},"capabilities":{"type":["object","null"],"additionalProperties":false,"properties":{"can_send_input":{"type":"boolean"},"can_resize":{"type":"boolean"},"can_detach":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_stream_terminal":{"type":"boolean"}}},"warnings":{"type":"array","items":{"type":"string"}},"observed_at":{"type":["string","null"]}}}
        """;

    private const string ConsoleCommandListResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["commands"],"properties":{"commands":{"type":"array","items":{"$ref":"console_command_definition"}}}}
        """;

    private const string ConsoleCommandRunRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["command"],"properties":{"command":{"type":"string"},"projectId":{"type":["string","null"]},"taskId":{"type":["integer","null"]},"workspaceId":{"type":["string","null"]},"sessionId":{"type":["string","null"]}}}
        """;

    private const string ConsoleCommandRunResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["command","status","lines"],"properties":{"command":{"type":"string"},"status":{"type":"string"},"errorMessage":{"type":["string","null"]},"lines":{"type":"array","items":{"$ref":"console_command_line"}}}}
        """;

    private const string ConsoleCommandLineSchema = """
        {"type":"object","additionalProperties":false,"required":["level","timestamp","source","message"],"properties":{"level":{"type":"string"},"timestamp":{"type":"string"},"source":{"type":"string"},"message":{"type":"string"}}}
        """;

    private const string ConsoleCommandDefinitionSchema = """
        {"type":"object","additionalProperties":false,"required":["name","displayName","description"],"properties":{"name":{"type":"string"},"displayName":{"type":"string"},"description":{"type":"string"},"needsTarget":{"type":"boolean"}}}
        """;

    private const string AppAgentSelectionSchema = """
        {"type":"object","additionalProperties":false,"properties":{"project_id":{"type":["string","null"]},"task_id":{"type":["integer","null"]},"workspace_id":{"type":["string","null"]},"current_route":{"type":["string","null"]},"current_tab":{"type":["string","null"]},"session_id":{"type":["string","null"]},"selected_file_path":{"type":["string","null"]},"selected_diff_range":{"type":["string","null"]}}}
        """;

    private const string AppAgentToolDefinitionSchema = """
        {"type":"object","additionalProperties":false,"required":["name","display_name","category","description","enabled","requires_explicit_target","destructive","requires_confirmation","cancellable","audit_event_type","capabilities"],"properties":{"name":{"type":"string"},"display_name":{"type":"string"},"category":{"type":"string"},"description":{"type":"string"},"enabled":{"type":"boolean"},"disabled_reason":{"type":["string","null"]},"requires_explicit_target":{"type":"boolean"},"destructive":{"type":"boolean"},"requires_confirmation":{"type":"boolean"},"cancellable":{"type":"boolean"},"audit_event_type":{"type":"string"},"capabilities":{"type":"array","items":{"type":"string"}}}}
        """;

    private const string AppAgentAuditCorrelationSchema = """
        {"type":"object","additionalProperties":false,"required":["agent_run_id","trace_id"],"properties":{"agent_run_id":{"type":"string"},"operator_session_id":{"type":["string","null"]},"trace_id":{"type":"string"},"parent_request_id":{"type":["string","null"]},"task_id":{"type":["integer","null"]},"project_id":{"type":["string","null"]}}}
        """;

    private const string AppAgentBuildContextRequestSchema = """
        {"type":"object","additionalProperties":false,"properties":{"selection":{"$ref":"app_agent_selection"},"agent_run_id":{"type":["string","null"]},"parent_request_id":{"type":["string","null"]},"trace_id":{"type":["string","null"]},"terminal_excerpts":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"after_cursor":{"type":["string","null"]},"limit":{"type":"integer"}}}},"message_limit":{"type":"integer"}}}
        """;

    private const string AppAgentBuildContextResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["context"],"properties":{"context":{"type":"object","additionalProperties":true,"required":["context_version","selection","git_snapshot","session_summaries","command_summaries","terminal_excerpts","collaboration_state","authority","audit","warnings","built_at"],"properties":{"context_version":{"type":"integer"},"selection":{"$ref":"app_agent_selection"},"git_snapshot":{"type":"object","additionalProperties":true},"session_summaries":{"type":"array","items":{"type":"object","additionalProperties":true}},"command_summaries":{"type":"array","items":{"type":"object","additionalProperties":true}},"terminal_excerpts":{"type":"array","items":{"type":"object","additionalProperties":true}},"collaboration_state":{"type":"object","additionalProperties":true},"authority":{"type":"object","additionalProperties":true},"audit":{"$ref":"app_agent_audit_correlation"},"warnings":{"type":"array","items":{"type":"string"}},"built_at":{"type":"string"}}}}}
        """;

    private const string AppAgentListToolsRequestSchema = """
        {"type":"object","additionalProperties":false,"properties":{"selection":{"$ref":"app_agent_selection"}}}
        """;

    private const string AppAgentListToolsResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["tools"],"properties":{"tools":{"type":"array","items":{"$ref":"app_agent_tool_definition"}}}}
        """;

    private const string AppAgentInvokeToolRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["tool_name"],"properties":{"tool_name":{"type":"string"},"input":{"type":"object","additionalProperties":true},"selection":{"$ref":"app_agent_selection"},"agent_run_id":{"type":["string","null"]},"trace_id":{"type":["string","null"]}}}
        """;

    private const string AppAgentInvokeToolResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["tool_name","tool_call_id","status","result","audit"],"properties":{"tool_name":{"type":"string"},"tool_call_id":{"type":"string"},"status":{"type":"string"},"result":{},"audit":{"$ref":"app_agent_audit_correlation"}}}
        """;

    private const string AppAgentCancelRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["request_id"],"properties":{"request_id":{"type":"string"},"reason":{"type":["string","null"]}}}
        """;

    private const string AppAgentCancelResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["request_id","accepted","status"],"properties":{"request_id":{"type":"string"},"accepted":{"type":"boolean"},"status":{"type":"string"}}}
        """;

    private const string TasksDashboardSnapshotRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id"],"properties":{"project_id":{"type":"string"},"parent_task_id":{"type":["integer","null"]},"focused_task_id":{"type":["integer","null"]},"include_done":{"type":"boolean"}}}
        """;

    private const string TasksDashboardSnapshotResponseSchema = """
        {"type":"object","additionalProperties":true,"required":["snapshot_id","project_id","generated_at","header","tasks","waves","lanes","freshness"],"properties":{"snapshot_id":{"type":"string"},"project_id":{"type":"string"},"parent_task_id":{"type":["integer","null"]},"focused_task_id":{"type":["integer","null"]},"generated_at":{"type":"string"},"header":{"type":"object","additionalProperties":true,"required":["state","task_count","completion_percent"],"properties":{"state":{"type":"string"},"task_count":{"type":"integer"},"completion_percent":{"type":"integer"},"total_tokens":{"type":["integer","null"]},"total_cost":{"type":["number","null"]}}},"tasks":{"type":"array","items":{"type":"object","additionalProperties":true,"required":["id","project_id","title","status","computed_state","dependencies","packets","review","run_summary","agent_lifecycle","session_chips"],"properties":{"id":{"type":"integer"},"project_id":{"type":"string"},"title":{"type":"string"},"status":{"type":"string"},"computed_state":{"type":"string"},"dependencies":{"type":"array","items":{"type":"object","additionalProperties":true}},"packets":{"type":"array","items":{"type":"object","additionalProperties":true}},"review":{"type":"object","additionalProperties":true},"run_summary":{"type":"object","additionalProperties":true},"agent_lifecycle":{"type":"object","additionalProperties":true},"session_chips":{"type":"array","items":{"type":"object","additionalProperties":true}}}}},"waves":{"type":"array","items":{"type":"object","additionalProperties":true}},"lanes":{"type":"array","items":{"type":"object","additionalProperties":true}},"freshness":{"type":"object","additionalProperties":false,"required":["source","is_partial","warnings","errors"],"properties":{"source":{"type":"string"},"generated_at":{"type":["string","null"]},"is_partial":{"type":"boolean"},"warnings":{"type":"array","items":{"type":"string"}},"errors":{"type":"array","items":{"type":"string"}}}}}}
        """;

    private const string MessagesSnapshotRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id"],"properties":{"project_id":{"type":"string"},"task_id":{"type":["integer","null"]},"thread_id":{"type":["integer","null"]},"since":{"type":["string","null"]},"limit":{"type":"integer"},"unread_for":{"type":["string","null"]}}}
        """;

    private const string MessagesSnapshotResponseSchema = """
        {"type":"object","additionalProperties":true,"required":["snapshot_id","project_id","generated_at","messages","unread_count","total_count","freshness"],"properties":{"snapshot_id":{"type":"string"},"project_id":{"type":"string"},"task_id":{"type":["integer","null"]},"thread_id":{"type":["integer","null"]},"generated_at":{"type":"string"},"messages":{"type":"array","items":{"type":"object","additionalProperties":true,"required":["id","sender","content","content_summary"],"properties":{"id":{"type":"integer"},"sender":{"type":"string"},"content":{"type":"string"},"intent":{"type":["string","null"]},"metadata_type":{"type":["string","null"]},"task_id":{"type":["integer","null"]},"thread_id":{"type":["integer","null"]},"created_at":{"type":["string","null"]},"is_unread":{"type":"boolean"},"content_summary":{"type":"string"}}}},"thread_root":{"type":["object","null"]},"unread_count":{"type":"integer"},"total_count":{"type":"integer"},"freshness":{"type":"object","additionalProperties":false,"required":["source","is_partial","warnings","errors"],"properties":{"source":{"type":"string"},"generated_at":{"type":["string","null"]},"is_partial":{"type":"boolean"},"warnings":{"type":"array","items":{"type":"string"}},"errors":{"type":"array","items":{"type":"string"}}}}}}
        """;

    // ── Documents tab schemas (task #1147) ────────────────────────────────────

    private const string DocumentsListRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id"],"properties":{"project_id":{"type":"string"}}}
        """;

    private const string DocumentsListResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["documents"],"properties":{"documents":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["slug","title","doc_type","tags"],"properties":{"slug":{"type":"string"},"title":{"type":"string"},"doc_type":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}}}}}}
        """;

    private const string DocumentGetRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id","slug"],"properties":{"project_id":{"type":"string"},"slug":{"type":"string"}}}
        """;

    private const string DocumentGetResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["slug","title","content","doc_type","tags"],"properties":{"slug":{"type":"string"},"title":{"type":"string"},"content":{"type":"string"},"doc_type":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}}}}
        """;

    private const string DocumentStoreRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id","slug","title","content"],"properties":{"project_id":{"type":"string"},"slug":{"type":"string"},"title":{"type":"string"},"content":{"type":"string"},"doc_type":{"type":["string","null"]}}}
        """;

    private const string DocumentStoreResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["slug","title","created"],"properties":{"slug":{"type":"string"},"title":{"type":"string"},"created":{"type":"boolean"}}}
        """;

    private const string TaskUpdateRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["project_id","task_id","agent"],"properties":{"project_id":{"type":"string"},"task_id":{"type":"integer"},"agent":{"type":"string"},"title":{"type":["string","null"]},"description":{"type":["string","null"]},"status":{"type":["string","null"]},"priority":{"type":["integer","null"]},"assigned_to":{"type":["string","null"]}}}
        """;

    private const string TaskUpdateResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["task_id","project_id","title","status","priority"],"properties":{"task_id":{"type":"integer"},"project_id":{"type":"string"},"title":{"type":"string"},"status":{"type":"string"},"priority":{"type":"integer"},"assigned_to":{"type":["string","null"]}}}
        """;

    private const string AppAgentRunStateEventSchema = """
        {"type":"object","additionalProperties":false,"required":["agent_run_id","status","observed_at"],"properties":{"agent_run_id":{"type":"string"},"request_id":{"type":["string","null"]},"status":{"type":"string"},"tool_name":{"type":["string","null"]},"message":{"type":["string","null"]},"observed_at":{"type":"string"}}}
        """;

    private const string AppAgentToolCallStateEventSchema = """
        {"type":"object","additionalProperties":false,"required":["tool_call_id","agent_run_id","tool_name","status","cancellable"],"properties":{"tool_call_id":{"type":"string"},"agent_run_id":{"type":"string"},"tool_name":{"type":"string"},"status":{"type":"string"},"started_at":{"type":["string","null"]},"completed_at":{"type":["string","null"]},"cancellable":{"type":"boolean"},"target_summary":{"type":["string","null"]}}}
        """;

    private const string CollaborationSendCompiledResponseRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"integer"},"compiled_text":{"type":["string","null"]},"target_session_id":{"type":["string","null"]},"post_to_den":{"type":"boolean"},"requested_by":{"type":["string","null"]}}}
        """;

    private const string CollaborationSendCompiledResponseResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["compiled_text","den_post","delivery","session_id"],"properties":{"compiled_text":{"type":"string"},"den_post":{"type":"object","additionalProperties":false,"required":["posted"],"properties":{"posted":{"type":"boolean"},"draft_id":{"type":["integer","null"]},"project_id":{"type":["string","null"]},"error":{"type":["string","null"]}}},"delivery":{"type":"object","additionalProperties":false,"required":["status"],"properties":{"status":{"type":"string"},"target_session_id":{"type":["string","null"]},"target_session_status":{"type":["string","null"]},"can_deliver":{"type":"boolean"},"reason":{"type":["string","null"]},"error":{"type":["string","null"]}}},"session_id":{"type":"integer"},"target_session_id":{"type":["string","null"]}}}
        """;

    private const string CollaborationDeliveryEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","status","compiled_text_length","observed_at"],"properties":{"session_id":{"type":"string"},"status":{"type":"string"},"compiled_text_length":{"type":"integer"},"reason":{"type":["string","null"]},"observed_at":{"type":"string"}}}
        """;

    private static BridgeNamedSchema Schema(string name, string schema)
    {
        return new BridgeNamedSchema(name, BridgeSchemaBundleFactory.Schema(schema));
    }
}

public sealed class GetHealthHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, DesktopSidecarHealthResponse>
{
    private readonly DesktopSidecarRuntimeState _state;

    public GetHealthHandler(DesktopSidecarRuntimeState state)
    {
        _state = state;
    }

    public ValueTask<DesktopSidecarHealthResponse?> HandleAsync(
        DesktopSidecarEmptyRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<DesktopSidecarHealthResponse?>(_state.CreateHealth());
    }
}

public sealed class GetCapabilitiesHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, DesktopSidecarCapabilitiesResponse>
{
    private readonly IBridgeCapabilitiesProvider _capabilitiesProvider;

    public GetCapabilitiesHandler(IBridgeCapabilitiesProvider capabilitiesProvider)
    {
        _capabilitiesProvider = capabilitiesProvider;
    }

    public ValueTask<DesktopSidecarCapabilitiesResponse?> HandleAsync(
        DesktopSidecarEmptyRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = _capabilitiesProvider.CreateCapabilitiesFrame(context.Correlation);
        var response = new DesktopSidecarCapabilitiesResponse
        {
            AppId = frame.AppId,
            AppVersion = frame.AppVersion,
            ProtocolVersion = frame.ProtocolVersion,
            SchemaVersion = frame.SchemaVersion,
            SchemaBundleId = frame.SchemaBundleId,
            SupportedTransports = frame.SupportedTransports,
            Commands = frame.Commands,
            Events = frame.Events,
            FeatureFlags = frame.FeatureFlags,
        };

        return ValueTask.FromResult<DesktopSidecarCapabilitiesResponse?>(response);
    }
}

public sealed class GetOperatorStatusHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, OperatorStatus>
{
    private readonly OperatorRuntimeService _runtime;

    public GetOperatorStatusHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<OperatorStatus?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetOperatorSettingsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, OperatorSettings>
{
    private readonly OperatorRuntimeService _runtime;

    public GetOperatorSettingsHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<OperatorSettings?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SaveOperatorSettingsHandler : IBridgeCommandHandler<SaveOperatorSettingsRequest, OperatorSettings>
{
    private readonly OperatorRuntimeService _runtime;

    public SaveOperatorSettingsHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<OperatorSettings?> HandleAsync(SaveOperatorSettingsRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.SaveSettingsAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class RefreshNowHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, DesktopSidecarEmptyResponse>
{
    private readonly OperatorRuntimeService _runtime;

    public RefreshNowHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<DesktopSidecarEmptyResponse?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return new DesktopSidecarEmptyResponse();
    }
}

public sealed class ListLocalSnapshotsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, LocalSnapshotList>
{
    private readonly OperatorRuntimeService _runtime;

    public ListLocalSnapshotsHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<LocalSnapshotList?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.ListLocalSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class ListLocalSessionSnapshotsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, LocalSessionSnapshotList>
{
    private readonly OperatorRuntimeService _runtime;

    public ListLocalSessionSnapshotsHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<LocalSessionSnapshotList?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.ListLocalSessionSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetLatestDiffSnapshotHandler : IBridgeCommandHandler<LatestDiffSnapshotRequest, DesktopDiffSnapshotLatestResult>
{
    private readonly OperatorRuntimeService _runtime;

    public GetLatestDiffSnapshotHandler(OperatorRuntimeService runtime) => _runtime = runtime;

    public async ValueTask<DesktopDiffSnapshotLatestResult?> HandleAsync(LatestDiffSnapshotRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        return await _runtime.GetLatestDiffSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GetAppearanceSettingsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, OperatorAppearanceSettings>
{
    private readonly OperatorSettingsService _settingsService;
    private readonly OperatorRuntimeService _runtime;

    public GetAppearanceSettingsHandler(OperatorSettingsService settingsService, OperatorRuntimeService runtime)
    {
        _settingsService = settingsService;
        _runtime = runtime;
    }

    public async ValueTask<OperatorAppearanceSettings?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _settingsService.LoadAppearanceFull();
        if (result.RecoveredFromMalformed)
        {
            await _runtime.AddDiagnosticAsync(
                "warn",
                "appearance-settings",
                "Corrupt appearance-settings.json recovered to defaults; the original file is preserved for inspection.",
                cancellationToken).ConfigureAwait(false);
        }

        return result.Settings;
    }
}

public sealed class SaveAppearanceSettingsHandler : IBridgeCommandHandler<SaveOperatorAppearanceSettingsRequest, OperatorAppearanceSettings>
{
    private readonly OperatorSettingsService _settingsService;

    public SaveAppearanceSettingsHandler(OperatorSettingsService settingsService) => _settingsService = settingsService;

    public ValueTask<OperatorAppearanceSettings?> HandleAsync(SaveOperatorAppearanceSettingsRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<OperatorAppearanceSettings?>(_settingsService.SaveAppearance(request));
    }
}

public sealed class ConsoleListCommandsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, ConsoleCommandListResponse>
{
    private readonly IConsoleCommandRunner _runner;

    public ConsoleListCommandsHandler(IConsoleCommandRunner runner) => _runner = runner;

    public ValueTask<ConsoleCommandListResponse?> HandleAsync(DesktopSidecarEmptyRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<ConsoleCommandListResponse?>(new ConsoleCommandListResponse
        {
            Commands = _runner.ListCommands(),
        });
    }
}

public sealed class ConsoleRunCommandHandler : IBridgeCommandHandler<ConsoleCommandRunRequest, ConsoleCommandRunResponse>
{
    private readonly IConsoleCommandRunner _runner;

    public ConsoleRunCommandHandler(IConsoleCommandRunner runner) => _runner = runner;

    public async ValueTask<ConsoleCommandRunResponse?> HandleAsync(ConsoleCommandRunRequest request, BridgeRequestContext context, CancellationToken cancellationToken)
    {
        // Forward each structured line as a progress event so the caller can stream output.
        async ValueTask OnProgress(ConsoleCommandLine line, CancellationToken ct)
        {
            await context.ReportProgressAsync(
                "line",
                message: $"[{line.Level}] [{line.Source}] {line.Message}",
                payload: BridgeJson.ToElement(line),
                cancellationToken: ct).ConfigureAwait(false);
        }

        return await _runner.RunCommandAsync(request, OnProgress, cancellationToken).ConfigureAwait(false);
    }
}
