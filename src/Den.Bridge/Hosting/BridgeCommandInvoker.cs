using System.Text.Json;
using Den.Bridge.Abstractions;
using Den.Bridge.Protocol;
using Den.Bridge.Registry;
using Microsoft.Extensions.Logging;

namespace Den.Bridge.Hosting;

public sealed class BridgeCommandInvoker : IBridgeCommandRouter
{
    private readonly IBridgeCommandRegistry _commandRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly IBridgeProgressPublisher _progressPublisher;
    private readonly IBridgeCapabilityGate _capabilityGate;
    private readonly ILogger<BridgeCommandInvoker> _logger;

    public BridgeCommandInvoker(
        IBridgeCommandRegistry commandRegistry,
        IServiceProvider serviceProvider,
        IBridgeProgressPublisher? progressPublisher = null,
        IBridgeCapabilityGate? capabilityGate = null,
        ILogger<BridgeCommandInvoker>? logger = null)
    {
        _commandRegistry = commandRegistry;
        _serviceProvider = serviceProvider;
        _progressPublisher = progressPublisher ?? NoopBridgeProgressPublisher.Instance;
        _capabilityGate = capabilityGate ?? AllowAllBridgeCapabilityGate.Instance;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BridgeCommandInvoker>.Instance;
    }

    public async ValueTask<BridgeResponseFrame> DispatchAsync(
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_commandRegistry.TryGetCommand(request.Command, out var descriptor))
        {
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.CommandUnsupported,
                $"Bridge command '{request.Command}' is not registered.",
                BridgeErrorCategories.UnsupportedCapability,
                retryable: false);
        }

        try
        {
            if (!await _capabilityGate.IsAllowedAsync(descriptor.RequiredCapabilities, request, cancellationToken).ConfigureAwait(false))
            {
                return CreateErrorResponse(
                    request,
                    BridgeErrorCodes.CapabilityUnsupported,
                    $"Bridge command '{request.Command}' requires unsupported capabilities.",
                    BridgeErrorCategories.UnsupportedCapability,
                    retryable: false,
                    BridgeJson.ToElement(new { RequiredCapabilities = descriptor.RequiredCapabilities }));
            }

            var context = new BridgeRequestContext(request.RequestId, request.Correlation, ReportProgressAsync);
            var result = await descriptor.InvokeAsync(
                _serviceProvider,
                request.Payload,
                context,
                cancellationToken).ConfigureAwait(false);

            return BridgeResponseFrame.Success(
                request.RequestId,
                result ?? BridgeJson.EmptyObject(),
                request.Correlation,
                DateTimeOffset.UtcNow,
                request.SchemaVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Bridge command {Command} rejected invalid payload for request {RequestId}.", request.Command, request.RequestId);
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.RequestInvalid,
                $"Bridge command '{request.Command}' payload is invalid.",
                BridgeErrorCategories.Validation,
                retryable: false,
                BridgeJson.ToElement(new { ex.Message }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bridge command {Command} failed for request {RequestId}.", request.Command, request.RequestId);
            return CreateErrorResponse(
                request,
                BridgeErrorCodes.HandlerFailed,
                $"Bridge command '{request.Command}' failed.",
                BridgeErrorCategories.Internal,
                retryable: false);
        }
    }

    private async ValueTask ReportProgressAsync(BridgeProgressFrame frame, CancellationToken cancellationToken)
    {
        await _progressPublisher.PublishAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    private static BridgeResponseFrame CreateErrorResponse(
        BridgeRequestFrame request,
        string code,
        string message,
        string category,
        bool retryable,
        JsonElement? details = null,
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
            DateTimeOffset.UtcNow,
            request.SchemaVersion);
    }
}

public sealed class NoopBridgeProgressPublisher : IBridgeProgressPublisher
{
    public static NoopBridgeProgressPublisher Instance { get; } = new();

    private NoopBridgeProgressPublisher()
    {
    }

    public ValueTask PublishAsync(BridgeProgressFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class AllowAllBridgeCapabilityGate : IBridgeCapabilityGate
{
    public static AllowAllBridgeCapabilityGate Instance { get; } = new();

    private AllowAllBridgeCapabilityGate()
    {
    }

    public ValueTask<bool> IsAllowedAsync(
        IReadOnlyList<string> requiredCapabilities,
        BridgeRequestFrame request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }
}
