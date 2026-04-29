using System.Text.Json;
using Den.Bridge.InMemory;
using Den.Bridge.Protocol;

namespace Den.Bridge.Tests;

public class InMemoryBridgeHarnessTests
{
    [Fact]
    public void BridgeErrorCodes_ExposeStableHarnessWireValues()
    {
        Assert.Equal("bridge.command.unsupported", BridgeErrorCodes.CommandUnsupported);
        Assert.Equal("bridge.request.duplicate", BridgeErrorCodes.RequestDuplicate);
        Assert.Equal("bridge.request.cancelled", BridgeErrorCodes.RequestCancelled);
    }

    [Fact]
    public async Task Harness_DispatchesSampleRequestAndPublishesProgressAndEvents()
    {
        await using var harness = new InMemoryBridgeHarness();
        harness.RegisterCommand(
            "sample.echo",
            async (request, context, cancellationToken) =>
            {
                await context.ReportProgressAsync(
                    "echoing",
                    "Echoing payload",
                    percent: 50,
                    cancellationToken: cancellationToken);

                var message = request.Payload.GetProperty("message").GetString();
                return BridgeJson.ToElement(new { Echo = message, RequestId = context.RequestId });
            });

        var request = new BridgeRequestFrame
        {
            RequestId = "req_echo",
            Command = "sample.echo",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = new BridgeCorrelation { TraceId = "tr_echo" },
        };

        var response = await harness.SendAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        Assert.Equal("hello", response.Result.Value.GetProperty("echo").GetString());
        Assert.Equal("req_echo", response.Result.Value.GetProperty("request_id").GetString());

        var progress = await ReadOneAsync(harness.ReadProgressAsync());
        Assert.Equal("req_echo", progress.RequestId);
        Assert.Equal("echoing", progress.Stage);
        Assert.Equal(50, progress.Percent);

        await harness.PublishAsync(new BridgeEventFrame
        {
            EventId = "evt_echo",
            Event = "sample.echoed",
            Payload = BridgeJson.ToElement(new { Message = "hello" }),
            Correlation = request.Correlation,
        });

        var evt = await ReadOneAsync(harness.ReadEventsAsync());
        Assert.Equal("evt_echo", evt.EventId);
        Assert.Equal(1, evt.Sequence);
        Assert.Equal("sample.echoed", evt.Event);
        Assert.Equal("hello", evt.Payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Harness_MapsUnknownCommandToUnsupportedCapabilityError()
    {
        await using var harness = new InMemoryBridgeHarness();

        var response = await harness.SendAsync(new BridgeRequestFrame
        {
            RequestId = "req_missing",
            Command = "sample.missing",
        });

        Assert.Null(response.Result);
        Assert.NotNull(response.Error);
        Assert.Equal(BridgeErrorCodes.CommandUnsupported, response.Error.Code);
        Assert.Equal(BridgeErrorCategories.UnsupportedCapability, response.Error.Category);
    }

    [Fact]
    public async Task Harness_MapsDuplicateActiveRequestToDuplicateError()
    {
        await using var harness = new InMemoryBridgeHarness();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.RegisterCommand(
            "sample.wait",
            async (_, _, cancellationToken) =>
            {
                handlerStarted.SetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
                return BridgeJson.EmptyObject();
            });

        var firstRequest = new BridgeRequestFrame
        {
            RequestId = "req_duplicate",
            Command = "sample.wait",
        };
        var firstResponseTask = harness.SendAsync(firstRequest).AsTask();

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicateResponse = await harness.SendAsync(new BridgeRequestFrame
        {
            RequestId = firstRequest.RequestId,
            Command = firstRequest.Command,
        });

        releaseHandler.SetResult();
        var firstResponse = await firstResponseTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(firstResponse.Error);
        Assert.NotNull(firstResponse.Result);
        Assert.Null(duplicateResponse.Result);
        Assert.NotNull(duplicateResponse.Error);
        Assert.Equal(BridgeErrorCodes.RequestDuplicate, duplicateResponse.Error.Code);
        Assert.Equal(BridgeErrorCategories.Conflict, duplicateResponse.Error.Category);
    }

    [Fact]
    public async Task Harness_CancelsActiveRequestCooperatively()
    {
        await using var harness = new InMemoryBridgeHarness();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.RegisterCommand(
            "sample.wait",
            async (_, _, cancellationToken) =>
            {
                handlerStarted.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                return BridgeJson.EmptyObject();
            });

        var sendTask = harness.SendAsync(new BridgeRequestFrame
        {
            RequestId = "req_wait",
            Command = "sample.wait",
        }).AsTask();

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.SendCancelAsync(new BridgeCancelFrame
        {
            RequestId = "req_wait",
            Reason = "user_requested",
        });

        var response = await sendTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(response.Error);
        Assert.Equal(BridgeErrorCodes.RequestCancelled, response.Error.Code);
        Assert.Equal(BridgeErrorCategories.Cancelled, response.Error.Category);
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
}
