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

public sealed class TaskUpdateHandler
    : IBridgeCommandHandler<TaskUpdateRequest, TaskUpdateResponse>
{
    private readonly DenHttpClient _den;
    private readonly OperatorRuntimeService _runtime;

    public TaskUpdateHandler(DenHttpClient den, OperatorRuntimeService runtime)
    {
        _den = den;
        _runtime = runtime;
    }

    public async ValueTask<TaskUpdateResponse?> HandleAsync(
        TaskUpdateRequest request,
        BridgeRequestContext context,
        CancellationToken cancellationToken)
    {
        var settings = await _runtime.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var update = new DenTaskUpdateRequest
        {
            Agent = request.Agent,
            Title = request.Title,
            Description = request.Description,
            Status = request.Status,
            Priority = request.Priority,
            AssignedTo = request.AssignedTo,
            Tags = request.Tags,
        };

        var updated = await _den.UpdateTaskAsync(
            settings.DenBaseUrl,
            request.ProjectId,
            request.TaskId,
            update,
            cancellationToken).ConfigureAwait(false);

        return new TaskUpdateResponse
        {
            TaskId = updated.Id,
            ProjectId = updated.ProjectId,
            Title = updated.Title,
            Status = updated.Status,
            Priority = updated.Priority,
            AssignedTo = updated.AssignedTo,
        };
    }
}
