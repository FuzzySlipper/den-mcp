using System.Net.WebSockets;
using System.Text;
using Den.Bridge.Protocol;

namespace Den.Bridge.Transport.WebSockets;

internal static class WebSocketBridgeFrameIO
{
    private const int BufferSize = 16 * 1024;

    public static async ValueTask SendFrameAsync(
        WebSocket socket,
        SemaphoreSlim sendGate,
        IBridgeFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(sendGate);
        ArgumentNullException.ThrowIfNull(frame);

        var json = BridgeFrameJson.Serialize(frame);
        var bytes = Encoding.UTF8.GetBytes(json);

        await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                WebSocketMessageFlags.EndOfMessage,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sendGate.Release();
        }
    }

    public static async ValueTask<IBridgeFrame?> ReceiveFrameAsync(
        WebSocket socket,
        int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (maxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes), "Maximum frame size must be positive.");
        }

        using var stream = new MemoryStream();
        var buffer = new byte[BufferSize];

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WebSocketException(WebSocketError.InvalidMessageType, "Bridge WebSocket frames must be text JSON messages.");
            }

            if (stream.Length + result.Count > maxFrameBytes)
            {
                throw new WebSocketException(WebSocketError.Faulted, "Bridge WebSocket frame exceeded the configured maximum size.");
            }

            stream.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var json = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
            return BridgeFrameJson.Deserialize(json);
        }
    }
}
