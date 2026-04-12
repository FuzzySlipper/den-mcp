using DenMcp.Core.Models;

namespace DenMcp.Core.Services;

public interface INotificationChannel
{
    Task SendDispatchNotificationAsync(
        DispatchEntry dispatch,
        string summary,
        CancellationToken cancellationToken = default);

    Task SendAgentStatusAsync(
        string projectId,
        string agent,
        string status,
        int? taskId = null,
        CancellationToken cancellationToken = default);

    Task StartListeningAsync(CancellationToken cancellationToken);
}
