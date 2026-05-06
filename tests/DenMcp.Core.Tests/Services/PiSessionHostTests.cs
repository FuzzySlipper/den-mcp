using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class PiSessionHostTests
{
    [Fact]
    public async Task GetStatus_DoesNotMarkExactlyEightyCapturedLinesAsTruncated()
    {
        var runner = new FakeProcessRunner(Lines(80));
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner, () => new DateTime(2026, 5, 6, 8, 0, 0, DateTimeKind.Utc));

        var status = await host.GetStatusAsync(Session());

        Assert.Equal(PiSessionStates.Running, status.State);
        Assert.False(status.OutputTailTruncated);
        Assert.Equal(80, status.OutputTail!.Split('\n').Length);
        var captureArgs = Assert.Single(runner.Calls, args => args.Count > 0 && args[0] == "capture-pane");
        var startIndex = captureArgs.ToList().IndexOf("-S");
        Assert.Equal("-81", captureArgs[startIndex + 1]);
    }

    [Fact]
    public async Task GetStatus_MarksAndDropsExtraCapturedLineWhenLineLimitExceeded()
    {
        var runner = new FakeProcessRunner(Lines(81));
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions(), runner);

        var status = await host.GetStatusAsync(Session());

        Assert.True(status.OutputTailTruncated);
        var outputLines = status.OutputTail!.Split('\n');
        Assert.Equal(80, outputLines.Length);
        Assert.Equal("line-2", outputLines[0]);
        Assert.Equal("line-81", outputLines[^1]);
    }

    private static PiSessionRecord Session() => new()
    {
        SessionId = "session-a",
        ProjectId = "den-mcp",
        HostId = "host-test",
        TmuxSessionName = "tmux-session-a",
        State = PiSessionStates.Running,
        LaunchProfileKind = "test",
        LaunchProfileJson = "{}",
        LaunchCommandJson = "[]",
        LaunchCommandDisplay = "test",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private static string Lines(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"line-{i}"));

    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly string _capturedOutput;
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public FakeProcessRunner(string capturedOutput)
        {
            _capturedOutput = capturedOutput;
        }

        public Task<ProcessRunResult> RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Add(args.ToArray());
            if (args.Count > 0 && args[0] == "list-sessions")
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                    Stdout = "tmux-session-a\t1760000000\t1760000010\n",
                });
            }

            if (args.Count > 0 && args[0] == "capture-pane")
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                    Stdout = _capturedOutput,
                });
            }

            return Task.FromResult(new ProcessRunResult
            {
                ExitCode = 1,
                Stderr = "unexpected command",
            });
        }
    }
}
