using System.Net;

namespace Den.Bridge.Transport.WebSockets;

public static class WebSocketBridgeTransportNames
{
    public const string LoopbackWebSocket = "loopback_websocket";
}

public static class WebSocketBridgeAuth
{
    public const string AuthorizationScheme = "Bearer";
    public const string TokenHeaderName = "X-Den-Bridge-Token";
}

public sealed class WebSocketBridgeServerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;

    public int Port { get; init; }

    public string Path { get; init; } = "/bridge";

    public required string AuthToken { get; init; }

    public int MaxFrameBytes { get; init; } = 1024 * 1024;
}

public sealed class WebSocketBridgeClientOptions
{
    public required Uri Endpoint { get; init; }

    public required string AuthToken { get; init; }

    public int MaxFrameBytes { get; init; } = 1024 * 1024;

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);
}
