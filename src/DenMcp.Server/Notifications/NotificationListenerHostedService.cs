using DenMcp.Core.Services;
using Microsoft.Extensions.Hosting;

namespace DenMcp.Server.Notifications;

public sealed class NotificationListenerHostedService : BackgroundService
{
    private readonly DenMcpOptions _options;
    private readonly INotificationChannel _channel;
    private readonly ILogger<NotificationListenerHostedService> _logger;

    public NotificationListenerHostedService(
        DenMcpOptions options,
        INotificationChannel channel,
        ILogger<NotificationListenerHostedService> logger)
    {
        _options = options;
        _channel = channel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Signal.Enabled)
            return;

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
                _logger.LogError(ex, "Signal notification listener crashed; retrying.");
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
