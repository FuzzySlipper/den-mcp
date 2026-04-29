using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace Den.Bridge.Transport.WebSockets;

public sealed class WebSocketBridgeConnectionClosedException : IOException
{
    public WebSocketBridgeConnectionClosedException(string message)
        : base(message)
    {
    }

    public WebSocketBridgeConnectionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class WebSocketBridgeClient : IBridgeClient, IAsyncDisposable
{
    private readonly WebSocketBridgeClientOptions _options;
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponseFrame>> _pendingResponses = new(StringComparer.Ordinal);
    private readonly Channel<BridgeEventFrame> _events = Channel.CreateUnbounded<BridgeEventFrame>(CreateChannelOptions());
    private readonly Channel<BridgeProgressFrame> _progress = Channel.CreateUnbounded<BridgeProgressFrame>(CreateChannelOptions());
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Task _receiveTask;
    private bool _disposed;

    private WebSocketBridgeClient(
        WebSocketBridgeClientOptions options,
        ClientWebSocket socket)
    {
        _options = options;
        _socket = socket;
        _receiveTask = Task.Run(ReceiveLoopAsync);
    }

    public Uri Endpoint => _options.Endpoint;

    public static async Task<WebSocketBridgeClient> ConnectAsync(
        WebSocketBridgeClientOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = options.KeepAliveInterval;
        socket.Options.SetRequestHeader(
            "Authorization",
            $"{WebSocketBridgeAuth.AuthorizationScheme} {options.AuthToken}");

        try
        {
            await socket.ConnectAsync(options.Endpoint, cancellationToken).ConfigureAwait(false);
            return new WebSocketBridgeClient(options, socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask<BridgeResponseFrame> SendAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        var pending = new TaskCompletionSource<BridgeResponseFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingResponses.TryAdd(request.RequestId, pending))
        {
            throw new InvalidOperationException($"Bridge request '{request.RequestId}' is already pending on this connection.");
        }

        try
        {
            await SendFrameAsync(request, cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pendingResponses.TryRemove(request.RequestId, out _);
            throw;
        }
    }

    public ValueTask SendCancelAsync(
        BridgeCancelFrame cancel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cancel);
        return SendFrameAsync(cancel, cancellationToken);
    }

    public async IAsyncEnumerable<BridgeEventFrame> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var frame))
            {
                yield return frame;
            }
        }
    }

    public async IAsyncEnumerable<BridgeProgressFrame> ReadProgressAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _progress.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_progress.Reader.TryRead(out var frame))
            {
                yield return frame;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _disposeCts.CancelAsync().ConfigureAwait(false);

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_disposed", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }

        CompletePending(new WebSocketBridgeConnectionClosedException("Bridge WebSocket client was disposed."));
        _events.Writer.TryComplete();
        _progress.Writer.TryComplete();

        try
        {
            await _receiveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _socket.Dispose();
            _sendGate.Dispose();
            _disposeCts.Dispose();
        }
    }

    private static void ValidateOptions(WebSocketBridgeClientOptions options)
    {
        if (options.Endpoint.Scheme != "ws" && options.Endpoint.Scheme != "wss")
        {
            throw new ArgumentException("Bridge WebSocket endpoint must use ws or wss.", nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.AuthToken);

        if (options.MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum frame size must be positive.");
        }
    }

    private ValueTask SendFrameAsync(IBridgeFrame frame, CancellationToken cancellationToken)
    {
        return WebSocketBridgeFrameIO.SendFrameAsync(_socket, _sendGate, frame, cancellationToken);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var frame = await WebSocketBridgeFrameIO.ReceiveFrameAsync(
                    _socket,
                    _options.MaxFrameBytes,
                    _disposeCts.Token).ConfigureAwait(false);

                if (frame is null)
                {
                    break;
                }

                await HandleIncomingFrameAsync(frame, _disposeCts.Token).ConfigureAwait(false);
            }

            CompletePending(new WebSocketBridgeConnectionClosedException("Bridge WebSocket connection closed."));
            _events.Writer.TryComplete();
            _progress.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CompletePending(new WebSocketBridgeConnectionClosedException("Bridge WebSocket receive loop failed.", ex));
            _events.Writer.TryComplete(ex);
            _progress.Writer.TryComplete(ex);
        }
    }

    private async ValueTask HandleIncomingFrameAsync(IBridgeFrame frame, CancellationToken cancellationToken)
    {
        switch (frame)
        {
            case BridgeResponseFrame response:
                if (_pendingResponses.TryRemove(response.RequestId, out var pending))
                {
                    pending.TrySetResult(response);
                }

                break;
            case BridgeEventFrame @event:
                await _events.Writer.WriteAsync(@event, cancellationToken).ConfigureAwait(false);
                break;
            case BridgeProgressFrame progress:
                await _progress.Writer.WriteAsync(progress, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pair in _pendingResponses.ToArray())
        {
            if (_pendingResponses.TryRemove(pair.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private static UnboundedChannelOptions CreateChannelOptions()
    {
        return new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
        };
    }
}
