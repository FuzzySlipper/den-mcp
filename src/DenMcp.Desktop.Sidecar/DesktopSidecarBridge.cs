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
        services.AddSingleton(_ => new OperatorSettingsService(
            OperatorSettingsStorage.ForPath(Path.Combine(options.ConfigPath, OperatorSettingsStorage.SettingsFileName))));
        services.AddSingleton(_ => new DenHttpClient());
        services.AddSingleton<IGitCommandRunner, SystemGitCommandRunner>();
        services.AddSingleton<GitSnapshotBuilder>();
        services.AddSingleton<PiSessionSnapshotBuilder>();
        services.AddSingleton<OperatorSessionRegistry>();
        services.AddSingleton<OperatorSessionLeaseStore>();
        services.AddSingleton<DesktopSidecarRuntimeState>();
        services.AddSingleton<OperatorRuntimeBridgeEventSink>();
        services.AddSingleton<IOperatorRuntimeEventSink>(sp => sp.GetRequiredService<OperatorRuntimeBridgeEventSink>());
        services.AddSingleton<OperatorRuntimeService>();
        services.AddBridgeHost(
            ConfigureRegistry,
            host =>
            {
                host.AppId = options.AppId;
                host.AppVersion = options.AppVersion;
                host.SchemaVersion = DesktopSidecarProtocol.SchemaVersion;
                host.SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId;
                host.SupportedTransports = new[] { WebSocketBridgeTransportNames.LoopbackWebSocket };
                host.FeatureFlags = new[] { "operator_runtime", "typed_runtime_bridge" };
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
            .RegisterCommand<DesktopSidecarEmptyRequest, DesktopSidecarEmptyResponse, RefreshNowHandler>(
                DesktopSidecarProtocol.RefreshNowCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, LocalSnapshotList, ListLocalGitSnapshotsHandler>(
                DesktopSidecarProtocol.ListLocalGitSnapshotsCommand)
            .RegisterCommand<DesktopSidecarEmptyRequest, LocalSessionSnapshotList, ListLocalSessionSnapshotsHandler>(
                DesktopSidecarProtocol.ListLocalSessionSnapshotsCommand)
            .RegisterCommand<LatestDiffSnapshotRequest, DesktopDiffSnapshotLatestResult, GetLatestDiffSnapshotHandler>(
                DesktopSidecarProtocol.GetLatestDiffSnapshotCommand)
            // Terminal protocol commands (task #1010, spec #945)
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
            .RegisterEvent<OperatorStatus>(DesktopSidecarProtocol.OperatorStatusEvent)
            .RegisterEvent<IReadOnlyList<LocalGitSnapshot>>(DesktopSidecarProtocol.GitSnapshotEvent)
            .RegisterEvent<IReadOnlyList<LocalSessionSnapshot>>(DesktopSidecarProtocol.SessionSnapshotEvent)
            // Terminal protocol events (task #1010, dot-convention names per R945-4)
            .RegisterEvent<TerminalSessionEvent>(DesktopSidecarProtocol.TerminalSessionStatusEvent)
            .RegisterEvent<TerminalListSessionsResponse>(DesktopSidecarProtocol.TerminalSessionListEvent);
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
            // Terminal protocol command schemas (task #1010)
            Schema(DesktopSidecarProtocol.TerminalListSessionsCommand + ".request", """
                {"type":"object","additionalProperties":false,"properties":{"kind":{"type":["string","null"]},"backend":{"type":["string","null"]},"status":{"type":["string","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalListSessionsCommand + ".response", TerminalListSessionsResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalReadActivityCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"after_cursor":{"type":["string","null"]},"limit":{"type":"integer"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalReadActivityCommand + ".response", TerminalReadActivityResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalAttachCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"mode":{"type":"string"},"client_id":{"type":["string","null"]}}}
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
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"previous_stream_id":{"type":["string","null"]},"last_seen_cursor":{"type":["string","null"]},"viewport":{"type":["object","null"]}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalReconnectCommand + ".response", TerminalAttachResponseSchema),
            Schema(DesktopSidecarProtocol.TerminalAckOutputCommand + ".request", """
                {"type":"object","additionalProperties":false,"required":["session_id"],"properties":{"session_id":{"type":"string"},"stream_id":{"type":["string","null"]},"ack_cursor":{"type":["string","null"]},"received_bytes":{"type":"integer"}}}
                """),
            Schema(DesktopSidecarProtocol.TerminalAckOutputCommand + ".response", TerminalAckOutputResponseSchema),
            // Terminal protocol event schemas
            Schema(DesktopSidecarProtocol.TerminalSessionStatusEvent + ".payload", TerminalSessionEventPayloadSchema),
            Schema(DesktopSidecarProtocol.TerminalSessionListEvent + ".payload", TerminalListSessionsResponseSchema),
        };
    }

    private const string EmptyObjectSchema = """
        {"type":"object","additionalProperties":false}
        """;

    private const string OperatorSettingsSchema = """
        {"type":"object","additionalProperties":false,"required":["denBaseUrl","sourceInstanceId","pollIntervalSeconds","maxChangedFiles"],"properties":{"denBaseUrl":{"type":"string"},"sourceInstanceId":{"type":"string"},"sourceDisplayName":{"type":["string","null"]},"pollIntervalSeconds":{"type":"integer"},"maxChangedFiles":{"type":"integer"}}}
        """;

    private const string SaveOperatorSettingsSchema = """
        {"type":"object","additionalProperties":false,"required":["denBaseUrl"],"properties":{"denBaseUrl":{"type":"string"},"sourceDisplayName":{"type":["string","null"]},"pollIntervalSeconds":{"type":"integer"},"maxChangedFiles":{"type":"integer"}}}
        """;

    private const string LatestDiffSnapshotRequestSchema = """
        {"type":"object","additionalProperties":false,"required":["projectId","rootPath","sourceInstanceId"],"properties":{"projectId":{"type":"string"},"taskId":{"type":["integer","null"]},"workspaceId":{"type":["string","null"]},"rootPath":{"type":"string"},"path":{"type":["string","null"]},"sourceInstanceId":{"type":"string"}}}
        """;

    private const string OperatorStatusSchema = """
        {"type":"object","additionalProperties":false,"required":["phase","denConnection","sourceInstanceId","denBaseUrl","observerStatuses","diagnostics","projectCount","workspaceCount","localSnapshotCount","localSessionSnapshotCount"],"properties":{"phase":{"type":"string"},"denConnection":{"type":"object","additionalProperties":false,"required":["state"],"properties":{"state":{"type":"string"},"message":{"type":["string","null"]},"lastSuccessAt":{"type":["string","null"]},"lastFailureAt":{"type":["string","null"]},"nextRetryAt":{"type":["string","null"]}}},"sourceInstanceId":{"type":"string"},"denBaseUrl":{"type":"string"},"lastSyncAt":{"type":["string","null"]},"lastPublishAt":{"type":["string","null"]},"observerStatuses":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["kind","state","scopesScanned","warningCount"],"properties":{"kind":{"type":"string"},"state":{"type":"string"},"scopesScanned":{"type":"integer"},"warningCount":{"type":"integer"},"lastRunAt":{"type":["string","null"]},"nextRunAt":{"type":["string","null"]}}}},"diagnostics":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["level","source","message","observedAt"],"properties":{"level":{"type":"string"},"source":{"type":"string"},"message":{"type":"string"},"observedAt":{"type":"string"}}}},"projectCount":{"type":"integer"},"workspaceCount":{"type":"integer"},"localSnapshotCount":{"type":"integer"},"localSessionSnapshotCount":{"type":"integer"}}}
        """;

    // ── Terminal response/event schemas (task #1010, matching DTOs from TerminalBridgeDtos.cs) ──

    private const string TerminalListSessionsResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["sessions","count"],"properties":{"sessions":{"type":"array","items":{"type":"object","additionalProperties":true,"required":["session_id"],"properties":{"session_id":{"type":"string"},"title":{"type":["string","null"]},"display_name":{"type":["string","null"]},"kind":{"type":"string"},"backend":{"type":"string"},"status":{"type":"string"},"can_read_activity":{"type":"boolean"},"can_send_input":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_attach":{"type":"boolean"}}}},"count":{"type":"integer"}}}
        """;

    private const string TerminalReadActivityResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["session_id","items","truncated"],"properties":{"session_id":{"type":"string"},"items":{"type":"array","items":{"type":"object","additionalProperties":true,"properties":{"kind":{"type":["string","null"]},"role":{"type":["string","null"]},"tool":{"type":["string","null"]},"summary":{"type":["string","null"]},"timestamp":{"type":["string","null"]}}}},"next_cursor":{"type":["string","null"]},"truncated":{"type":"boolean"}}}
        """;

    private const string TerminalAttachResponseSchema = """
        {"type":"object","additionalProperties":false,"required":["stream_id","session_id"],"properties":{"stream_id":{"type":"string"},"session_id":{"type":"string"},"attached_at":{"type":"string"},"start_cursor":{"type":"string"},"replay_available_from":{"type":"string"},"replay_gap":{"type":"boolean"},"capabilities":{"type":"object","additionalProperties":false,"required":["can_send_input","can_resize","can_detach","can_terminate","can_stream_terminal"],"properties":{"can_send_input":{"type":"boolean"},"can_resize":{"type":"boolean"},"can_detach":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_stream_terminal":{"type":"boolean"}}},"viewport_limits":{"type":["object","null"],"additionalProperties":false,"properties":{"min_cols":{"type":"integer"},"max_cols":{"type":"integer"},"min_rows":{"type":"integer"},"max_rows":{"type":"integer"}}},"limits":{"type":"object","additionalProperties":false,"properties":{"output_chunk_max_bytes":{"type":"integer"},"input_chunk_max_bytes":{"type":"integer"},"session_replay_max_bytes":{"type":"integer"},"subscriber_queue_max_bytes":{"type":"integer"},"ack_after_bytes":{"type":"integer"},"heartbeat_interval_ms":{"type":"integer"}}}}}
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

    private const string TerminalSessionEventPayloadSchema = """
        {"type":"object","additionalProperties":false,"required":["terminal_protocol_version","session_id"],"properties":{"terminal_protocol_version":{"type":"string"},"session_id":{"type":"string"},"status":{"type":["string","null"]},"capabilities":{"type":["object","null"],"additionalProperties":false,"properties":{"can_send_input":{"type":"boolean"},"can_resize":{"type":"boolean"},"can_detach":{"type":"boolean"},"can_terminate":{"type":"boolean"},"can_stream_terminal":{"type":"boolean"}}},"warnings":{"type":"array","items":{"type":"string"}},"observed_at":{"type":["string","null"]}}}
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

public sealed class ListLocalGitSnapshotsHandler : IBridgeCommandHandler<DesktopSidecarEmptyRequest, LocalSnapshotList>
{
    private readonly OperatorRuntimeService _runtime;

    public ListLocalGitSnapshotsHandler(OperatorRuntimeService runtime) => _runtime = runtime;

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
