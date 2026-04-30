using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Den.Bridge.Transport.WebSockets;

public sealed class WebSocketBridgeServer : IBridgeEventPublisher, IBridgeProgressPublisher, IAsyncDisposable
{
    private const int EphemeralPortBindAttempts = 10;

    private readonly WebSocketBridgeServerOptions _options;
    private readonly IBridgeCommandRouter _router;
    private readonly ILogger<WebSocketBridgeServer> _logger;
    private HttpListener _listener = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly ConcurrentDictionary<Guid, WebSocketBridgeConnection> _connections = new();
    private Task? _acceptTask;
    private bool _started;
    private bool _disposed;

    public WebSocketBridgeServer(
        WebSocketBridgeServerOptions options,
        IBridgeCommandRouter router,
        ILogger<WebSocketBridgeServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(router);
        ValidateOptions(options);

        _options = options;
        _router = router;
        _logger = logger ?? NullLogger<WebSocketBridgeServer>.Instance;
    }

    public Uri? Endpoint { get; private set; }

    public int Port { get; private set; }

    public ValueTask PublishAsync(BridgeEventFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return BroadcastAsync(frame, cancellationToken);
    }

    ValueTask IBridgeProgressPublisher.PublishAsync(BridgeProgressFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return BroadcastAsync(frame, cancellationToken);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            return Task.CompletedTask;
        }

        StartListenerWithRetry(cancellationToken);

        Endpoint = CreateEndpointUri(_options.ListenAddress, Port, _options.Path);
        _started = true;
        _acceptTask = Task.Run(AcceptLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopCts.CancelAsync().ConfigureAwait(false);

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        foreach (var connection in _connections.Values.ToArray())
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _listener.Close();
        _stopCts.Dispose();
    }

    private static void ValidateOptions(WebSocketBridgeServerOptions options)
    {
        if (!IPAddress.IsLoopback(options.ListenAddress))
        {
            throw new ArgumentException("Bridge WebSocket server must bind to a loopback address.", nameof(options));
        }

        if (options.Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 0 and 65535.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.AuthToken);

        if (options.MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum frame size must be positive.");
        }
    }

    private void StartListenerWithRetry(CancellationToken cancellationToken)
    {
        if (_options.Port != 0)
        {
            StartListenerOnPort(_options.Port);
            Port = _options.Port;
            return;
        }

        Exception? lastBindException = null;
        for (var attempt = 1; attempt <= EphemeralPortBindAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidatePort = AllocateEphemeralLoopbackPort(_options.ListenAddress);
            try
            {
                StartListenerOnPort(candidatePort);
                Port = candidatePort;
                return;
            }
            catch (HttpListenerException ex)
            {
                lastBindException = ex;
                _logger.LogWarning(
                    ex,
                    "Bridge WebSocket server failed to bind reserved ephemeral port {Port} on attempt {Attempt}/{MaxAttempts}.",
                    candidatePort,
                    attempt,
                    EphemeralPortBindAttempts);
                ResetListenerAfterFailedStart();
            }
        }

        throw new InvalidOperationException(
            $"Bridge WebSocket server could not bind an ephemeral loopback port after {EphemeralPortBindAttempts} attempts.",
            lastBindException);
    }

    private void StartListenerOnPort(int port)
    {
        var prefix = $"http://{FormatListenAddress(_options.ListenAddress)}:{port}/";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add(prefix);
        try
        {
            _listener.Start();
        }
        catch
        {
            TryClearPrefixes(_listener);
            throw;
        }
    }

    private void ResetListenerAfterFailedStart()
    {
        try
        {
            _listener.Close();
        }
        finally
        {
            _listener = new HttpListener();
        }
    }

    private static void TryClearPrefixes(HttpListener listener)
    {
        try
        {
            listener.Prefixes.Clear();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static int AllocateEphemeralLoopbackPort(IPAddress listenAddress)
    {
        var listener = new TcpListener(listenAddress, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string FormatListenAddress(IPAddress address)
    {
        return address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();
    }

    private static Uri CreateEndpointUri(IPAddress listenAddress, int port, string path)
    {
        return new Uri($"ws://{FormatListenAddress(listenAddress)}:{port}{NormalizePath(path)}");
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.StartsWith("/", StringComparison.Ordinal) ? path : $"/{path}";
        return normalized.TrimEnd('/');
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
            {
                break;
            }
            catch (HttpListenerException) when (_stopCts.IsCancellationRequested || !_listener.IsListening)
            {
                break;
            }
            catch (ObjectDisposedException) when (_stopCts.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsyncWithLogging(context), CancellationToken.None);
        }
    }

    private async Task HandleContextAsyncWithLogging(HttpListenerContext context)
    {
        try
        {
            await HandleContextAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled bridge WebSocket connection handler failure.");
            TryAbortResponse(context);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        if (!IsLoopbackRequest(context))
        {
            Reject(context, HttpStatusCode.Forbidden, "Bridge WebSocket accepts loopback clients only.");
            return;
        }

        if (!IsBridgePath(context.Request.Url))
        {
            Reject(context, HttpStatusCode.NotFound, "Bridge WebSocket path was not found.");
            return;
        }

        if (!context.Request.IsWebSocketRequest)
        {
            Reject(context, HttpStatusCode.BadRequest, "Bridge endpoint requires a WebSocket upgrade.");
            return;
        }

        if (!IsAuthorized(context.Request))
        {
            context.Response.Headers["WWW-Authenticate"] = WebSocketBridgeAuth.AuthorizationScheme;
            Reject(context, HttpStatusCode.Unauthorized, "Bridge WebSocket authentication failed.");
            return;
        }

        try
        {
            var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            var connection = new WebSocketBridgeConnection(this, webSocketContext.WebSocket);
            _connections.TryAdd(connection.Id, connection);
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "Bridge WebSocket connection failed during upgrade or receive loop.");
        }
        catch (HttpListenerException ex)
        {
            _logger.LogDebug(ex, "Bridge WebSocket listener failed while handling a connection.");
        }
    }

    private static void TryAbortResponse(HttpListenerContext context)
    {
        try
        {
            context.Response.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool IsBridgePath(Uri? url)
    {
        return string.Equals(url?.AbsolutePath, NormalizePath(_options.Path), StringComparison.Ordinal);
    }

    private static bool IsLoopbackRequest(HttpListenerContext context)
    {
        var address = context.Request.RemoteEndPoint?.Address;
        return address is not null && IPAddress.IsLoopback(address);
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var authorization = request.Headers["Authorization"];
        if (authorization is not null
            && authorization.StartsWith($"{WebSocketBridgeAuth.AuthorizationScheme} ", StringComparison.Ordinal))
        {
            var token = authorization[WebSocketBridgeAuth.AuthorizationScheme.Length..].Trim();
            if (TokenEquals(_options.AuthToken, token))
            {
                return true;
            }
        }

        return TokenEquals(_options.AuthToken, request.Headers[WebSocketBridgeAuth.TokenHeaderName]);
    }

    private static bool TokenEquals(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static void Reject(HttpListenerContext context, HttpStatusCode statusCode, string message)
    {
        var responseBytes = Encoding.UTF8.GetBytes(message);
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = responseBytes.Length;
        context.Response.OutputStream.Write(responseBytes, 0, responseBytes.Length);
        context.Response.Close();
    }

    private async ValueTask BroadcastAsync(IBridgeFrame frame, CancellationToken cancellationToken)
    {
        var connections = _connections.Values.ToArray();
        if (connections.Length == 0)
        {
            return;
        }

        var sendTasks = new Task[connections.Length];
        for (var index = 0; index < connections.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sendTasks[index] = connections[index].TrySendFrameAsync(frame, cancellationToken).AsTask();
        }

        // Sends are intentionally started for all current connections before awaiting; WhenAll still
        // waits for every send, and callers can bound that wait with cancellation if needed.
        await Task.WhenAll(sendTasks).ConfigureAwait(false);
    }

    private void RemoveConnection(Guid id)
    {
        // Send failures and receive-loop finalization can race here. TryRemove keeps removal
        // idempotent; RunAsync remains the single owner of socket/gate disposal.
        _connections.TryRemove(id, out _);
    }

    private sealed class WebSocketBridgeConnection
    {
        private readonly WebSocketBridgeServer _server;
        private readonly WebSocket _socket;
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);

        public WebSocketBridgeConnection(WebSocketBridgeServer server, WebSocket socket)
        {
            _server = server;
            _socket = socket;
        }

        public Guid Id { get; } = Guid.NewGuid();

        public async Task RunAsync()
        {
            try
            {
                while (!_server._stopCts.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    var frame = await WebSocketBridgeFrameIO.ReceiveFrameAsync(
                        _socket,
                        _server._options.MaxFrameBytes,
                        _server._stopCts.Token).ConfigureAwait(false);

                    if (frame is null)
                    {
                        break;
                    }

                    await HandleIncomingFrameAsync(frame).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_server._stopCts.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                CancelActiveRequests();
                _server.RemoveConnection(Id);
                _sendGate.Dispose();
                _socket.Dispose();
            }
        }

        public async ValueTask CloseAsync()
        {
            CancelActiveRequests();

            try
            {
                if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "server_disposed", closeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException)
            {
            }
        }

        public async ValueTask TrySendFrameAsync(IBridgeFrame frame, CancellationToken cancellationToken)
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await WebSocketBridgeFrameIO.SendFrameAsync(_socket, _sendGate, frame, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _server._stopCts.IsCancellationRequested)
            {
            }
            catch (WebSocketException)
            {
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !_server._stopCts.IsCancellationRequested)
            {
                _server._logger.LogWarning(ex, "Bridge WebSocket send failed unexpectedly; removing connection {ConnectionId}.", Id);
                _server.RemoveConnection(Id);
            }
        }

        private ValueTask HandleIncomingFrameAsync(IBridgeFrame frame)
        {
            switch (frame)
            {
                case BridgeRequestFrame request:
                    _ = Task.Run(() => DispatchRequestWithLoggingAsync(request), CancellationToken.None);
                    break;
                case BridgeCancelFrame cancel:
                    CancelRequest(cancel.RequestId);
                    break;
            }

            return ValueTask.CompletedTask;
        }

        private async Task DispatchRequestWithLoggingAsync(BridgeRequestFrame request)
        {
            try
            {
                await DispatchRequestAsync(request).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_server._stopCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _server._logger.LogError(ex, "Unhandled bridge WebSocket request dispatch failure for request {RequestId}.", request.RequestId);
            }
        }

        private async Task DispatchRequestAsync(BridgeRequestFrame request)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(_server._stopCts.Token);
            if (!_activeRequests.TryAdd(request.RequestId, linkedCancellation))
            {
                await TrySendFrameAsync(
                    CreateErrorResponse(
                        request,
                        BridgeErrorCodes.RequestDuplicate,
                        $"Bridge request '{request.RequestId}' is already active.",
                        BridgeErrorCategories.Conflict),
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            try
            {
                BridgeResponseFrame response;
                try
                {
                    response = await _server._router.DispatchAsync(request, linkedCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
                {
                    response = CreateErrorResponse(
                        request,
                        BridgeErrorCodes.RequestCancelled,
                        $"Bridge request '{request.RequestId}' was cancelled.",
                        BridgeErrorCategories.Cancelled);
                }
                catch (Exception)
                {
                    response = CreateErrorResponse(
                        request,
                        BridgeErrorCodes.HandlerFailed,
                        $"Bridge request '{request.RequestId}' failed during transport dispatch.",
                        BridgeErrorCategories.Internal);
                }

                await TrySendFrameAsync(response, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (_activeRequests.TryRemove(request.RequestId, out var completed))
                {
                    completed.Dispose();
                }
            }
        }

        private void CancelRequest(string requestId)
        {
            if (_activeRequests.TryGetValue(requestId, out var source))
            {
                source.Cancel();
            }
        }

        private void CancelActiveRequests()
        {
            foreach (var pair in _activeRequests.ToArray())
            {
                if (_activeRequests.TryRemove(pair.Key, out var source))
                {
                    source.Cancel();
                    source.Dispose();
                }
            }
        }

        private static BridgeResponseFrame CreateErrorResponse(
            BridgeRequestFrame request,
            string code,
            string message,
            string category)
        {
            return BridgeResponseFrame.Failure(
                request.RequestId,
                code,
                message,
                category,
                retryable: false,
                correlation: request.Correlation,
                sentAt: DateTimeOffset.UtcNow,
                schemaVersion: request.SchemaVersion);
        }
    }
}
