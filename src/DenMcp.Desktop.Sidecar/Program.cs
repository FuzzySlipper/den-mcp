using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Transport.WebSockets;
using DenMcp.Desktop.Sidecar;
using Microsoft.Extensions.DependencyInjection;

static async Task<int> RunAsync(string[] args)
{
    DesktopSidecarOptions options;
    try
    {
        options = DesktopSidecarOptions.Parse(args);
    }
    catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
    {
        Console.Error.WriteLine($"DEN_DESKTOP_SIDECAR_CONFIG_ERROR {ex.Message}");
        return 2;
    }

    if (options.PrintSchema)
    {
        using var schemaProvider = DesktopSidecarBridge.CreateServiceProvider(options);
        Console.WriteLine(BridgeJson.Serialize(DesktopSidecarBridge.CreateSchemaBundle(schemaProvider)));
        return 0;
    }

    if (options.PrintWireFixture)
    {
        Console.WriteLine(BridgeJson.Serialize(DesktopSidecarFixtures.CreateWireFixture(options)));
        return 0;
    }

    Directory.CreateDirectory(options.ConfigPath);
    if (options.LogPath is not null)
    {
        Directory.CreateDirectory(options.LogPath);
    }

    using var provider = DesktopSidecarBridge.CreateServiceProvider(options);
    var router = provider.GetRequiredService<IBridgeCommandRouter>();
    await using var server = new WebSocketBridgeServer(
        new WebSocketBridgeServerOptions
        {
            Port = options.Port,
            Path = options.EndpointPath,
            AuthToken = options.AuthToken,
        },
        router);

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        shutdown.Cancel();
    };

    await server.StartAsync(shutdown.Token).ConfigureAwait(false);
    Console.WriteLine(DesktopSidecarStartup.FormatReadySentinel(
        DesktopSidecarStartup.CreateReadySentinel(options, server.Port)));
    Console.Out.Flush();

    var state = provider.GetRequiredService<DesktopSidecarRuntimeState>();
    await PublishPlaceholderEventAsync(server, state, shutdown.Token).ConfigureAwait(false);

    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }

    return 0;
}

static ValueTask PublishPlaceholderEventAsync(
    IBridgeEventPublisher publisher,
    DesktopSidecarRuntimeState state,
    CancellationToken cancellationToken)
{
    var frame = new BridgeEventFrame
    {
        SchemaVersion = DesktopSidecarProtocol.SchemaVersion,
        EventId = $"evt_placeholder_{Guid.NewGuid():N}",
        Sequence = state.NextSequence(),
        Event = DesktopSidecarProtocol.PlaceholderRuntimeEvent,
        Payload = BridgeJson.ToElement(state.CreatePlaceholderEventPayload()),
        SentAt = DateTimeOffset.UtcNow,
    };

    return publisher.PublishAsync(frame, cancellationToken);
}

return await RunAsync(args).ConfigureAwait(false);
