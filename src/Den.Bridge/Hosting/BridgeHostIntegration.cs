using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Den.Bridge.Hosting;

public sealed class BridgeHostMetadata
{
    public string AppId { get; set; } = "bridge-app";

    public string AppVersion { get; set; } = "0.0.0";

    public string SchemaVersion { get; set; } = BridgeProtocol.DefaultSchemaVersion;

    public string SchemaBundleId { get; set; } = BridgeProtocol.DefaultSchemaVersion;

    public IReadOnlyList<string> SupportedTransports { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> FeatureFlags { get; set; } = Array.Empty<string>();
}

public interface IBridgeCapabilitiesProvider
{
    BridgeCapabilitiesFrame CreateCapabilitiesFrame(
        BridgeCorrelation? correlation = null,
        DateTimeOffset? sentAt = null);
}

public sealed class BridgeCapabilitiesProvider : IBridgeCapabilitiesProvider
{
    private readonly IBridgeCommandRegistry _commandRegistry;
    private readonly IBridgeEventRegistry _eventRegistry;
    private readonly BridgeHostMetadata _hostMetadata;

    public BridgeCapabilitiesProvider(
        IBridgeCommandRegistry commandRegistry,
        IBridgeEventRegistry eventRegistry,
        BridgeHostMetadata hostMetadata)
    {
        _commandRegistry = commandRegistry;
        _eventRegistry = eventRegistry;
        _hostMetadata = hostMetadata;
    }

    public BridgeCapabilitiesFrame CreateCapabilitiesFrame(
        BridgeCorrelation? correlation = null,
        DateTimeOffset? sentAt = null)
    {
        return new BridgeCapabilitiesFrame
        {
            AppId = _hostMetadata.AppId,
            AppVersion = _hostMetadata.AppVersion,
            SchemaVersion = _hostMetadata.SchemaVersion,
            SupportedTransports = _hostMetadata.SupportedTransports,
            Commands = _commandRegistry.Commands.Select(descriptor => descriptor.ToCapability()).ToArray(),
            Events = _eventRegistry.Events.Select(descriptor => descriptor.ToCapability()).ToArray(),
            FeatureFlags = _hostMetadata.FeatureFlags,
            SchemaBundleId = _hostMetadata.SchemaBundleId,
            Correlation = correlation ?? BridgeCorrelation.Empty,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
        };
    }
}

public static class BridgeServiceCollectionExtensions
{
    public static IServiceCollection AddBridgeHost(
        this IServiceCollection services,
        Action<BridgeRegistryBuilder> configureRegistry,
        Action<BridgeHostMetadata>? configureHost = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureRegistry);

        var registryBuilder = new BridgeRegistryBuilder();
        configureRegistry(registryBuilder);

        var commandRegistry = registryBuilder.BuildCommandRegistry();
        var eventRegistry = registryBuilder.BuildEventRegistry();
        var hostMetadata = new BridgeHostMetadata();
        configureHost?.Invoke(hostMetadata);

        services.AddSingleton<IBridgeCommandRegistry>(commandRegistry);
        services.AddSingleton<IBridgeEventRegistry>(eventRegistry);
        services.AddSingleton(hostMetadata);
        services.TryAddSingleton<IBridgeProgressPublisher>(NoopBridgeProgressPublisher.Instance);
        services.TryAddSingleton<IBridgeCapabilityGate>(AllowAllBridgeCapabilityGate.Instance);
        services.TryAddSingleton<IBridgeCommandRouter, BridgeCommandInvoker>();
        services.TryAddSingleton<IBridgeCapabilitiesProvider, BridgeCapabilitiesProvider>();

        foreach (var descriptor in commandRegistry.Commands)
        {
            services.TryAddTransient(descriptor.HandlerType);
        }

        return services;
    }
}
