using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class ConsoleCommandRunnerTests
{
    [Fact]
    public async Task RunCommandAsync_EmitsProgressThroughAsyncCallback()
    {
        var runner = new ConsoleCommandRunner(
            runtime: null!,
            sessionRegistry: new OperatorSessionRegistry(),
            now: () => new DateTimeOffset(2026, 4, 30, 8, 0, 0, TimeSpan.Zero));
        var progressLines = new List<ConsoleCommandLine>();
        var observedAsyncContinuation = false;

        var response = await runner.RunCommandAsync(
            new ConsoleCommandRunRequest { Command = "help" },
            async (line, cancellationToken) =>
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                observedAsyncContinuation = true;
                progressLines.Add(line);
            });

        Assert.Equal("success", response.Status);
        Assert.Equal(response.Lines.Count, progressLines.Count);
        Assert.True(observedAsyncContinuation);
        Assert.Contains(response.Lines, line => line.Message.Contains("publish-snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void PublishSnapshotCommand_DescribesRefreshBackedPublishCycle()
    {
        var runner = new ConsoleCommandRunner(runtime: null!, sessionRegistry: new OperatorSessionRegistry());

        var command = Assert.Single(runner.ListCommands(), definition => definition.Name == "publish-snapshot");

        Assert.Contains("snapshot", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("publish", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", command.Description, StringComparison.OrdinalIgnoreCase);
    }
}
