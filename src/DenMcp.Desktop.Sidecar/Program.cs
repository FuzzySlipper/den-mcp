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

    await using var provider = DesktopSidecarBridge.CreateServiceProvider(options);
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
    provider.GetRequiredService<OperatorRuntimeBridgeEventSink>().SetPublisher(server);
    Console.WriteLine(DesktopSidecarStartup.FormatReadySentinel(
        DesktopSidecarStartup.CreateReadySentinel(options, server.Port)));
    Console.Out.Flush();

    var runtime = provider.GetRequiredService<OperatorRuntimeService>();
    await runtime.StartAsync(runInitialRefresh: true, startBackgroundLoop: true, shutdown.Token).ConfigureAwait(false);

    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    finally
    {
        await runtime.StopAsync().ConfigureAwait(false);
    }

    return 0;
}

return await RunAsync(args).ConfigureAwait(false);
