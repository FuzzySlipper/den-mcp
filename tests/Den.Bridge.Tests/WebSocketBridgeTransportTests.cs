using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Transport.WebSockets;

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

    private static async Task<T> ReadOneAsync<T>(IAsyncEnumerable<T> source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in source.WithCancellation(timeout.Token))
        {
            return item;
        }

        throw new JsonException("The bridge stream completed before an item was available.");
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
