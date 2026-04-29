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
        services.AddSingleton(_ => new OperatorSettingsService());
        services.AddSingleton<IGitCommandRunner, SystemGitCommandRunner>();
        services.AddSingleton<GitSnapshotBuilder>();
        services.AddSingleton<DesktopSidecarRuntimeState>();
        services.AddBridgeHost(
            ConfigureRegistry,
            host =>
            {
                host.AppId = options.AppId;
                host.AppVersion = options.AppVersion;
                host.SchemaVersion = DesktopSidecarProtocol.SchemaVersion;
                host.SchemaBundleId = DesktopSidecarProtocol.SchemaBundleId;
                host.SupportedTransports = new[] { WebSocketBridgeTransportNames.LoopbackWebSocket };
                host.FeatureFlags = new[] { "sidecar_skeleton", "placeholder_runtime_events" };
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
            .RegisterEvent<DesktopPlaceholderRuntimeEvent>(DesktopSidecarProtocol.PlaceholderRuntimeEvent);
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
            new BridgeNamedSchema(
                DesktopSidecarProtocol.HealthCommand + ".request",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false}
                    """)),
            new BridgeNamedSchema(
                DesktopSidecarProtocol.HealthCommand + ".response",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["process_id","uptime_ms","ready_state","app_id","app_version","config_path","protocol_version","schema_version","schema_bundle_id","active_request_count","degraded_subsystems"],"properties":{"process_id":{"type":"integer"},"uptime_ms":{"type":"integer"},"ready_state":{"type":"string"},"app_id":{"type":"string"},"app_version":{"type":"string"},"config_path":{"type":"string"},"log_path":{"type":"string"},"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"schema_bundle_id":{"type":"string"},"active_request_count":{"type":"integer"},"degraded_subsystems":{"type":"array","items":{"type":"string"}},"last_error":{"$ref":"bridge.error"}}}
                    """)),
            new BridgeNamedSchema(
                DesktopSidecarProtocol.CapabilitiesCommand + ".request",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false}
                    """)),
            new BridgeNamedSchema(
                DesktopSidecarProtocol.CapabilitiesCommand + ".response",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["app_id","app_version","protocol_version","schema_version","schema_bundle_id","supported_transports","commands","events","feature_flags"],"properties":{"app_id":{"type":"string"},"app_version":{"type":"string"},"protocol_version":{"const":"1.0"},"schema_version":{"type":"string"},"schema_bundle_id":{"type":"string"},"supported_transports":{"type":"array","items":{"type":"string"}},"commands":{"type":"array","items":{"$ref":"bridge.command_capability"}},"events":{"type":"array","items":{"$ref":"bridge.event_capability"}},"feature_flags":{"type":"array","items":{"type":"string"}}}}
                    """)),
            new BridgeNamedSchema(
                DesktopSidecarProtocol.PlaceholderRuntimeEvent + ".payload",
                BridgeSchemaBundleFactory.Schema("""
                    {"type":"object","additionalProperties":false,"required":["status","message","config_path","schema_version"],"properties":{"status":{"type":"string"},"message":{"type":"string"},"config_path":{"type":"string"},"schema_version":{"type":"string"}}}
                    """)),
        };
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
