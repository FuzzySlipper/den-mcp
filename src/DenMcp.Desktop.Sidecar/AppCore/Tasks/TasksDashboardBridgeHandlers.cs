using Den.Bridge.Abstractions;

namespace DenMcp.Desktop.Sidecar;

public sealed class TasksDashboardSnapshotHandler
    : IBridgeCommandHandler<TasksDashboardSnapshotRequest, TasksDashboardSnapshot>
{
    private readonly TasksDashboardProjectionService _projection;

    public TasksDashboardSnapshotHandler(TasksDashboardProjectionService projection) => _projection = projection;

    public async ValueTask<TasksDashboardSnapshot?> HandleAsync(
        TasksDashboardSnapshotRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        return await _projection.GetSnapshotAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
