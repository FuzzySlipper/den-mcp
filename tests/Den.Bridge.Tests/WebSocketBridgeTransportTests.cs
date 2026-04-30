using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Transport.WebSockets;
using Microsoft.Extensions.Logging;

namespace Den.Bridge.Tests;

public class WebSocketBridgeTransportTests
{
    [Fact]
    public void Server_RejectsNonLoopbackBindAddress()
    {
        var router = new DelegatingRouter((request, _) =>
            ValueTask.FromResult(BridgeResponseFrame.Success(request.RequestId)));

        Assert.Throws<ArgumentException>(() => new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions
            {
                ListenAddress = IPAddress.Any,
                AuthToken = "token",
            },
            router));
    }

    [Fact]
    public async Task ClientConnectAsync_RejectsMissingOrInvalidAuthTokenDuringHandshake()
    {
        var router = new DelegatingRouter((request, _) =>
            ValueTask.FromResult(BridgeResponseFrame.Success(request.RequestId)));
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "expected-token" },
            router);
        await server.StartAsync();

        var endpoint = Assert.IsType<Uri>(server.Endpoint);
        Assert.Equal(IPAddress.Loopback.ToString(), endpoint.Host);

        using var unauthenticatedSocket = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => unauthenticatedSocket.ConnectAsync(endpoint, CancellationToken.None));

        await Assert.ThrowsAnyAsync<WebSocketException>(() => WebSocketBridgeClient.ConnectAsync(
            new WebSocketBridgeClientOptions
            {
                Endpoint = endpoint,
                AuthToken = "wrong-token",
            }));
    }

    [Fact]
    public void Server_FormatsIPv6LoopbackEndpointWithBracketedAuthority()
    {
        var endpoint = CreateEndpointUri(IPAddress.IPv6Loopback, 12345, "/bridge");

        Assert.Equal("ws://[::1]:12345/bridge", endpoint.AbsoluteUri);
        Assert.Equal("[::1]:12345", endpoint.Authority);
    }

    [Fact]
    public async Task Server_CleansUpAndLogsUnexpectedConnectionHandlerFailures()
    {
        var logger = new CapturingLogger<WebSocketBridgeServer>();
        var router = new DelegatingRouter((request, _) =>
            ValueTask.FromResult(BridgeResponseFrame.Success(request.RequestId)));
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router,
            logger);
        await server.StartAsync();

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"{WebSocketBridgeAuth.AuthorizationScheme} token");
        await socket.ConnectAsync(Assert.IsType<Uri>(server.Endpoint), CancellationToken.None);
        await WaitUntilAsync(() => GetConnectionCount(server) == 1);

        var invalidJson = Encoding.UTF8.GetBytes("{not valid json");
        await socket.SendAsync(
            invalidJson,
            WebSocketMessageType.Text,
            WebSocketMessageFlags.EndOfMessage,
            CancellationToken.None);

        await WaitUntilAsync(() => GetConnectionCount(server) == 0);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is JsonException);
    }

    [Fact]
    public async Task DispatchRequestWithLoggingAsync_LogsUnhandledSendFailuresAfterRouterDispatch()
    {
        var logger = new CapturingLogger<WebSocketBridgeServer>();
        var routerDispatchCount = 0;
        var router = new DelegatingRouter((request, _) =>
        {
            routerDispatchCount++;
            Assert.Equal("req_send_failure", request.RequestId);
            return ValueTask.FromResult(BridgeResponseFrame.Success(request.RequestId));
        });
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router,
            logger);
        using var socket = new ThrowingStateWebSocket(() => new InvalidOperationException("Unexpected send state read."));
        var connection = CreateConnection(server, socket);
        var method = connection.GetType().GetMethod("DispatchRequestWithLoggingAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method.Invoke(connection, [new BridgeRequestFrame
        {
            RequestId = "req_send_failure",
            Command = "sample.invalid",
        }]);

        Assert.NotNull(result);
        await Assert.IsAssignableFrom<Task>(result).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, routerDispatchCount);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Error
            && entry.Exception is InvalidOperationException ex
            && ex.Message == "Unexpected send state read."
            && entry.Message.Contains("Unhandled bridge WebSocket request dispatch failure", StringComparison.Ordinal)
            && entry.Message.Contains("req_send_failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientAndServer_RouteRequestsAndResponsesOverJsonFrames()
    {
        var router = new DelegatingRouter((request, _) =>
        {
            Assert.Equal("sample.echo", request.Command);
            Assert.Equal("hello", request.Payload.GetProperty("message").GetString());

            return ValueTask.FromResult(BridgeResponseFrame.Success(
                request.RequestId,
                BridgeJson.ToElement(new { Echo = request.Payload.GetProperty("message").GetString() }),
                request.Correlation));
        });
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router);
        await server.StartAsync();
        await using var client = await ConnectAsync(server);

        var response = await client.SendAsync(new BridgeRequestFrame
        {
            RequestId = "req_echo",
            Command = "sample.echo",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_echo" },
        });

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        Assert.Equal("hello", response.Result.Value.GetProperty("echo").GetString());
        Assert.Equal("tr_echo", response.Correlation.TraceId);
    }

    [Fact]
    public async Task Server_PublishesEventsToConnectedClients()
    {
        var router = new DelegatingRouter((request, _) =>
            ValueTask.FromResult(BridgeResponseFrame.Success(request.RequestId)));
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router);
        await server.StartAsync();
        await using var client = await ConnectAsync(server);

        await server.PublishAsync(new BridgeEventFrame
        {
            EventId = "evt_sample",
            Sequence = 7,
            Event = "sample.changed",
            Payload = BridgeJson.ToElement(new { Message = "event" }),
        });

        var frame = await ReadOneAsync(client.ReadEventsAsync());
        Assert.Equal("evt_sample", frame.EventId);
        Assert.Equal(7, frame.Sequence);
        Assert.Equal("sample.changed", frame.Event);
        Assert.Equal("event", frame.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ClientCancelAsync_CancelsServerRequestAndProgressFramesAreDelivered()
    {
        WebSocketBridgeServer? server = null;
        var router = new DelegatingRouter(async (request, cancellationToken) =>
        {
            await ((IBridgeProgressPublisher)server!).PublishAsync(new BridgeProgressFrame
            {
                RequestId = request.RequestId,
                Stage = "working",
                Message = "Working",
                Percent = 25,
                Correlation = request.Correlation,
            }, cancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return BridgeResponseFrame.Success(request.RequestId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return BridgeResponseFrame.Failure(
                    request.RequestId,
                    BridgeErrorCodes.RequestCancelled,
                    "Request was cancelled.",
                    BridgeErrorCategories.Cancelled,
                    retryable: false,
                    correlation: request.Correlation);
            }
        });

        server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router);
        await using (server)
        {
            await server.StartAsync();
            await using var client = await ConnectAsync(server);

            var responseTask = client.SendAsync(new BridgeRequestFrame
            {
                RequestId = "req_wait",
                Command = "sample.wait",
                Correlation = new BridgeCorrelation { TraceId = "tr_wait" },
                ExpectsProgress = true,
            }).AsTask();

            var progress = await ReadOneAsync(client.ReadProgressAsync());
            Assert.Equal("req_wait", progress.RequestId);
            Assert.Equal("working", progress.Stage);
            Assert.Equal(25, progress.Percent);
            Assert.Equal("tr_wait", progress.Correlation.TraceId);

            await client.SendCancelAsync(new BridgeCancelFrame
            {
                RequestId = "req_wait",
                Reason = "user_requested",
            });

            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(response.Error);
            Assert.Equal(BridgeErrorCodes.RequestCancelled, response.Error.Code);
            Assert.Equal(BridgeErrorCategories.Cancelled, response.Error.Category);
        }
    }

    [Fact]
    public async Task ClientDisconnect_CancelsActiveServerRequestsAndFaultsPendingResponses()
    {
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var router = new DelegatingRouter(async (_, cancellationToken) =>
        {
            handlerStarted.SetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancellationObserved.SetResult();
            }

            return BridgeResponseFrame.Success("req_disconnect");
        });
        await using var server = new WebSocketBridgeServer(
            new WebSocketBridgeServerOptions { Port = 0, AuthToken = "token" },
            router);
        await server.StartAsync();
        var client = await ConnectAsync(server);

        var responseTask = client.SendAsync(new BridgeRequestFrame
        {
            RequestId = "req_disconnect",
            Command = "sample.wait",
        }).AsTask();

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<WebSocketBridgeConnectionClosedException>(async () =>
            await responseTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void BridgeFrameJson_RoundTripsProgressAndCancelFramesByFrameType()
    {
        var progressJson = BridgeFrameJson.Serialize(new BridgeProgressFrame
        {
            RequestId = "req_progress",
            Stage = "working",
            Percent = 50,
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
        });
        var cancelJson = BridgeFrameJson.Serialize(new BridgeCancelFrame
        {
            RequestId = "req_progress",
            Reason = "user_requested",
        });

        var progress = Assert.IsType<BridgeProgressFrame>(BridgeFrameJson.Deserialize(progressJson));
        var cancel = Assert.IsType<BridgeCancelFrame>(BridgeFrameJson.Deserialize(cancelJson));

        Assert.Equal("req_progress", progress.RequestId);
        Assert.Equal("working", progress.Stage);
        Assert.Equal(50, progress.Percent);
        Assert.Equal("hello", progress.Payload.GetProperty("message").GetString());
        Assert.Equal("req_progress", cancel.RequestId);
        Assert.Equal("user_requested", cancel.Reason);
    }

    private static async Task<WebSocketBridgeClient> ConnectAsync(WebSocketBridgeServer server)
    {
        return await WebSocketBridgeClient.ConnectAsync(new WebSocketBridgeClientOptions
        {
            Endpoint = Assert.IsType<Uri>(server.Endpoint),
            AuthToken = "token",
        });
    }

    private static Uri CreateEndpointUri(IPAddress listenAddress, int port, string path)
    {
        var method = typeof(WebSocketBridgeServer).GetMethod("CreateEndpointUri", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var endpoint = method.Invoke(null, [listenAddress, port, path]);
        return Assert.IsType<Uri>(endpoint);
    }

    private static object CreateConnection(WebSocketBridgeServer server, WebSocket socket)
    {
        var connectionType = typeof(WebSocketBridgeServer).GetNestedType("WebSocketBridgeConnection", BindingFlags.NonPublic);
        Assert.NotNull(connectionType);
        var constructor = connectionType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(WebSocketBridgeServer), typeof(WebSocket)],
            modifiers: null);
        Assert.NotNull(constructor);
        return constructor.Invoke([server, socket]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }

    private static int GetConnectionCount(WebSocketBridgeServer server)
    {
        var field = typeof(WebSocketBridgeServer).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var connections = field.GetValue(server);
        Assert.NotNull(connections);
        var count = connections.GetType().GetProperty("Count")?.GetValue(connections);
        return Assert.IsType<int>(count);
    }

    private static async Task<T> ReadOneAsync<T>(IAsyncEnumerable<T> source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in source.WithCancellation(timeout.Token))
        {
            return item;
        }

        throw new JsonException("The bridge stream completed before an item was available.");
    }

    private sealed class ThrowingStateWebSocket : WebSocket
    {
        private readonly Func<Exception> _stateExceptionFactory;

        public ThrowingStateWebSocket(Func<Exception> stateExceptionFactory)
        {
            _stateExceptionFactory = stateExceptionFactory;
        }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => throw _stateExceptionFactory();

        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue(new LogEntry(logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class DelegatingRouter : IBridgeCommandRouter
    {
        private readonly Func<BridgeRequestFrame, CancellationToken, ValueTask<BridgeResponseFrame>> _dispatchAsync;

        public DelegatingRouter(Func<BridgeRequestFrame, CancellationToken, ValueTask<BridgeResponseFrame>> dispatchAsync)
        {
            _dispatchAsync = dispatchAsync;
        }

        public ValueTask<BridgeResponseFrame> DispatchAsync(
            BridgeRequestFrame request,
            CancellationToken cancellationToken = default)
        {
            return _dispatchAsync(request, cancellationToken);
        }
    }
}
