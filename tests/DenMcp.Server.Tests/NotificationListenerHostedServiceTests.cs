using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class NotificationListenerHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_DelegatesToNotificationChannelEvenWithoutSignalSpecificGate()
    {
        using var cts = new CancellationTokenSource();
        var channel = new RecordingNotificationChannel(() => cts.Cancel());
        var service = new NotificationListenerHostedService(
            channel,
            NullLogger<NotificationListenerHostedService>.Instance);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await channel.WaitForInvocationAsync();
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            cts.Cancel();
            service.Dispose();
        }

        Assert.Equal(1, channel.StartListeningCallCount);
    }

    private sealed class RecordingNotificationChannel(Action onStartListening) : INotificationChannel
    {
        private readonly TaskCompletionSource _invoked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int StartListeningCallCount { get; private set; }

        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            StartListeningCallCount++;
            _invoked.TrySetResult();
            onStartListening();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitForInvocationAsync() => _invoked.Task;
    }
}
