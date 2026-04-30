using Den.Bridge.Abstractions;

namespace DenMcp.Desktop.Sidecar;

/// <summary>
/// Bridge command handler for delivering compiled collaboration responses.
///
/// This handler follows the Den-post-first + capability-gated delivery
/// pattern: the compiled response is always saved to Den first, then
/// optionally delivered to a live OperatorSession using capability checks.
/// </summary>
public sealed class CollaborationSendCompiledResponseHandler
    : IBridgeCommandHandler<CollaborationSendCompiledResponseRequest, CollaborationSendCompiledResponseResponse>
{
    private readonly CollaborationResponseDeliveryService _delivery;

    public CollaborationSendCompiledResponseHandler(CollaborationResponseDeliveryService delivery)
    {
        _delivery = delivery;
    }

    public async ValueTask<CollaborationSendCompiledResponseResponse?> HandleAsync(
        CollaborationSendCompiledResponseRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _delivery.DeliverAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
