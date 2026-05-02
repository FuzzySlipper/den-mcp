using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class ConsoleCommandRunnerTests
{
    private static ConsoleCommandRunner CreateRunner(OperatorRuntimeService? runtime = null)
    {
        return new ConsoleCommandRunner(
            runtime: runtime ?? null!,
            sessionRegistry: new OperatorSessionRegistry(),
            now: () => new DateTimeOffset(2026, 4, 30, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task RunCommandAsync_EmitsProgressThroughAsyncCallback()
    {
        var runner = CreateRunner();
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
        var runner = CreateRunner();

        var command = Assert.Single(runner.ListCommands(), definition => definition.Name == "publish-snapshot");

        Assert.Contains("snapshot", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("publish", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", command.Description, StringComparison.OrdinalIgnoreCase);
    }

    // --- Cancellation semantics: cancellation should propagate, not become an error response ---

    [Fact]
    public async Task RunCommandAsync_HelpCommand_PropagatesProgressCallbackCancellation()
    {
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "help" },
                (line, ct) =>
                {
                    cts.Cancel();
                    return ValueTask.FromCanceled(ct);
                },
                cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task RunCommandAsync_ListSessionsEmptyCommand_PropagatesProgressCallbackCancellation()
    {
        // list-sessions uses _sessionRegistry (no runtime) and emits multiple lines.
        // Cancel partway through to verify mid-command cancellation propagation.
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();
        var emitCount = 0;

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "list-sessions" },
                (line, ct) =>
                {
                    emitCount++;
                    if (emitCount >= 2)
                    {
                        cts.Cancel();
                        return ValueTask.FromCanceled(ct);
                    }
                    return ValueTask.CompletedTask;
                },
                cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task RunCommandAsync_UnknownCommand_PropagatesProgressCallbackCancellation()
    {
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "bogus" },
                (line, ct) =>
                {
                    cts.Cancel();
                    return ValueTask.FromCanceled(ct);
                },
                cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task RunCommandAsync_ListSessionsCommand_PropagatesProgressCallbackCancellation()
    {
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "list-sessions" },
                (line, ct) =>
                {
                    cts.Cancel();
                    return ValueTask.FromCanceled(ct);
                },
                cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    [Fact]
    public async Task RunCommandAsync_ListSessionsCommand_PropagatesPreCancelledToken()
    {
        // list-sessions does not require runtime — it uses the session registry directly.
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // With a pre-cancelled token and a progress callback that returns canceled tasks,
        // the first EmitAsync should propagate OperationCanceledException.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "list-sessions" },
                (line, ct) => ValueTask.FromCanceled(ct),
                cts.Token));
    }

    // --- Best-effort: non-cancellation progress callback failures are swallowed ---

    [Fact]
    public async Task RunCommandAsync_SwallowsNonCancellationProgressCallbackException()
    {
        var runner = CreateRunner();
        var callbackInvocations = 0;

        var response = await runner.RunCommandAsync(
            new ConsoleCommandRunRequest { Command = "help" },
            (line, ct) =>
            {
                callbackInvocations++;
                if (callbackInvocations == 1)
                {
                    throw new InvalidOperationException("progress consumer broken");
                }

                return ValueTask.CompletedTask;
            });

        // Command still succeeds — non-cancellation exceptions in progress callback are best-effort
        Assert.Equal("success", response.Status);
        Assert.True(response.Lines.Count > 1, "Expected multiple lines for help command");
        Assert.True(callbackInvocations > 1, "Callback should have been called for subsequent lines after the first failure");
    }

    // --- Pre-cancelled token propagates immediately for commands that check it ---

    [Fact]
    public async Task RunCommandAsync_PreCancelledToken_PropagatesForHelpCommand()
    {
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // help does not have a runtime call before the first EmitAsync, so with a
        // progress callback that respects the token, cancellation should propagate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunCommandAsync(
                new ConsoleCommandRunRequest { Command = "help" },
                (line, ct) => ValueTask.FromCanceled(ct),
                cts.Token));
    }

    [Fact]
    public async Task RunCommandAsync_NoProgressCallback_ReturnsSuccessWithoutCancellation()
    {
        // When no progress callback is provided, the command should complete normally
        // even if the token is technically cancellable (but not yet cancelled).
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();

        var response = await runner.RunCommandAsync(
            new ConsoleCommandRunRequest { Command = "help" },
            onProgress: null,
            cts.Token);

        Assert.Equal("success", response.Status);
    }

    // --- Lines are preserved even when progress callback fails ---

    [Fact]
    public async Task RunCommandAsync_PreservesLinesWhenProgressCallbackThrowsNonCancellation()
    {
        var runner = CreateRunner();

        var response = await runner.RunCommandAsync(
            new ConsoleCommandRunRequest { Command = "help" },
            (line, ct) => throw new InvalidOperationException("broken"));

        // All lines should still be collected in the response despite progress failures
        Assert.Equal("success", response.Status);
        Assert.NotEmpty(response.Lines);
        Assert.Contains(response.Lines, l => l.Message.Contains("Available commands", StringComparison.Ordinal));
    }
}
