using DenMcp.Core.Services;
using Microsoft.Extensions.Hosting;

namespace DenMcp.Server.Notifications;

public sealed class NotificationListenerHostedService : BackgroundService
{
    private readonly INotificationChannel _channel;
    private readonly ILogger<NotificationListenerHostedService> _logger;

    public NotificationListenerHostedService(
        INotificationChannel channel,
        ILogger<NotificationListenerHostedService> logger)
    {
        _channel = channel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _channel.StartListeningAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification listener crashed; retrying.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
