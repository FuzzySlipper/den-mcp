using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;

namespace Den.Bridge.InMemory;

public sealed class InMemoryBridgeHarness : IBridgeClient, IBridgeCommandRouter, IBridgeEventPublisher, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BridgeCommandHandler> _handlers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRequests = new(StringComparer.Ordinal);
    private readonly Channel<BridgeEventFrame> _events = Channel.CreateUnbounded<BridgeEventFrame>(CreateChannelOptions());
    private readonly Channel<BridgeProgressFrame> _progress = Channel.CreateUnbounded<BridgeProgressFrame>(CreateChannelOptions());
    private long _eventSequence;
    private bool _disposed;

    public void RegisterCommand(string command, BridgeCommandHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);

        if (!_handlers.TryAdd(command, handler))
        {
            throw new InvalidOperationException($"Bridge command '{command}' is already registered.");
        }
    }

    public ValueTask<BridgeResponseFrame> SendAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default)
    {
        return DispatchAsync(request, cancellationToken);
    }

    public async ValueTask<BridgeResponseFrame> DispatchAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (!_handlers.TryGetValue(request.Command, out var handler))
        {
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.CommandUnsupported,
                $"Bridge command '{request.Command}' is not registered.",
                BridgeErrorCategories.UnsupportedCapability,
                retryable: false);
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_activeRequests.TryAdd(request.RequestId, linkedCancellation))
        {
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.RequestDuplicate,
                $"Bridge request '{request.RequestId}' is already active.",
                BridgeErrorCategories.Conflict,
                retryable: false);
        }

        try
        {
            var context = new BridgeRequestContext(request.RequestId, request.Correlation, ReportProgressAsync);
            var result = await handler(request, context, linkedCancellation.Token).ConfigureAwait(false);

            return BridgeResponseFrame.Success(
                request.RequestId,
                result ?? BridgeJson.EmptyObject(),
                request.Correlation,
                DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.RequestCancelled,
                $"Bridge request '{request.RequestId}' was cancelled.",
                BridgeErrorCategories.Cancelled,
                retryable: false);
        }
        catch (BridgeHandlerException ex)
        {
            return CreateErrorResponse(
                request,
                ex.Code,
                ex.Message,
                ex.Category,
                ex.Retryable,
                ex.Details,
                ex.CausedBy);
        }
        finally
        {
            _activeRequests.TryRemove(request.RequestId, out _);
        }
    }

    public ValueTask SendCancelAsync(
        BridgeCancelFrame cancel,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(cancel);
        cancellationToken.ThrowIfCancellationRequested();

        if (_activeRequests.TryGetValue(cancel.RequestId, out var source))
        {
            source.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask PublishAsync(
        BridgeEventFrame frame,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        var sequenced = frame.Sequence > 0
            ? frame
            : frame with { Sequence = Interlocked.Increment(ref _eventSequence) };

        await _events.Writer.WriteAsync(sequenced, cancellationToken).ConfigureAwait(false);
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

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        foreach (var activeRequest in _activeRequests.Values)
        {
            activeRequest.Cancel();
            activeRequest.Dispose();
        }

        _events.Writer.TryComplete();
        _progress.Writer.TryComplete();
        return ValueTask.CompletedTask;
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

    private async ValueTask ReportProgressAsync(BridgeProgressFrame frame, CancellationToken cancellationToken)
    {
        await _progress.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static BridgeResponseFrame CreateErrorResponse(
        BridgeRequestFrame request,
        string code,
        string message,
        string category,
        bool retryable,
        System.Text.Json.JsonElement? details = null,
        IReadOnlyList<BridgeError>? causedBy = null)
    {
        return BridgeResponseFrame.Failure(
            request.RequestId,
            code,
            message,
            category,
            retryable,
            details,
            causedBy,
            request.Correlation,
            DateTimeOffset.UtcNow);
    }
}
