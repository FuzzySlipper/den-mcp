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
            .RegisterEvent<OperatorStatus>(DesktopSidecarProtocol.OperatorStatusEvent)
            .RegisterEvent<IReadOnlyList<LocalGitSnapshot>>(DesktopSidecarProtocol.GitSnapshotEvent)
            .RegisterEvent<IReadOnlyList<LocalSessionSnapshot>>(DesktopSidecarProtocol.SessionSnapshotEvent);
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
