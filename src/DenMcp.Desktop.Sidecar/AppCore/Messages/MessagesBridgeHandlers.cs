using Den.Bridge.Abstractions;

namespace DenMcp.Desktop.Sidecar;

public sealed class MessagesSnapshotHandler
    : IBridgeCommandHandler<MessagesSnapshotRequest, MessagesSnapshot>
{
    private readonly MessagesProjectionService _projection;

    public MessagesSnapshotHandler(MessagesProjectionService projection) => _projection = projection;

    public async ValueTask<MessagesSnapshot?> HandleAsync(
        MessagesSnapshotRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _projection.GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
