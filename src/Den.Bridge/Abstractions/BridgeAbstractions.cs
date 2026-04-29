using System.Text.Json;
using Den.Bridge.Protocol;

namespace Den.Bridge.Abstractions;

public delegate ValueTask<JsonElement?> BridgeCommandHandler(
    BridgeRequestFrame request,
    BridgeRequestContext context,
    CancellationToken cancellationToken);

public interface IBridgeCommandHandler<in TRequest, TResponse>
{
    ValueTask<TResponse?> HandleAsync(
        TRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken);
}

public interface IBridgeProgressPublisher
{
    ValueTask PublishAsync(
        BridgeProgressFrame frame,
        CancellationToken cancellationToken = default);
}

public interface IBridgeCapabilityGate
{
    ValueTask<bool> IsAllowedAsync(
        IReadOnlyList<string> requiredCapabilities,
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default);
}

public interface IBridgeClient
{
    ValueTask<BridgeResponseFrame> SendAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default);

    ValueTask SendCancelAsync(
        BridgeCancelFrame cancel,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<BridgeEventFrame> ReadEventsAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<BridgeProgressFrame> ReadProgressAsync(CancellationToken cancellationToken = default);
}

public interface IBridgeCommandRouter
{
    ValueTask<BridgeResponseFrame> DispatchAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default);
}

public interface IBridgeEventPublisher
{
    ValueTask PublishAsync(
        BridgeEventFrame frame,
        CancellationToken cancellationToken = default);
}

public sealed class BridgeRequestContext
{
    private readonly Func<BridgeProgressFrame, CancellationToken, ValueTask> _reportProgress;

    public BridgeRequestContext(
        string requestId,
        BridgeCorrelation correlation,
        Func<BridgeProgressFrame, CancellationToken, ValueTask> reportProgress)
    {
        RequestId = requestId;
        Correlation = correlation;
        _reportProgress = reportProgress;
    }

    public string RequestId { get; }

    public BridgeCorrelation Correlation { get; }

    public ValueTask ReportProgressAsync(
        string stage,
        string? message = null,
        double? percent = null,
        JsonElement? payload = null,
        CancellationToken cancellationToken = default)
    {
        var frame = new BridgeProgressFrame
        {
            RequestId = RequestId,
            Stage = stage,
            Message = message,
            Percent = percent,
            Payload = payload ?? BridgeJson.EmptyObject(),
            Correlation = Correlation,
            SentAt = DateTimeOffset.UtcNow,
        };

        return _reportProgress(frame, cancellationToken);
    }
}
