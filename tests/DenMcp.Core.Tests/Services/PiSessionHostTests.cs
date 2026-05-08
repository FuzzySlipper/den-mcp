using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public sealed class PiSessionHostTests
{
    [Fact]
    public async Task Launch_BlanksProviderSecretsInTmuxEnvironmentAndCommand()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        var previousOpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "server-secret");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                ProviderSecretEnvironmentVariables = ["OPENAI_API_KEY"],
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner);
            var record = Session();
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
                ["OPENAI_API_KEY"] = string.Empty,
            }, ["OPENAI_API_KEY"]);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = record,
                LaunchProfile = profile,
                LaunchCommand = ["env", "OPENAI_API_KEY=", "docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            var newSessionArgs = Assert.Single(runner.Calls, args => args.Count > 0 && args[0] == "new-session");
            Assert.Equal(PiDockerLaunchProfileDefaults.TmuxShellCommand, newSessionArgs.TakeLast(PiDockerLaunchProfileDefaults.TmuxShellCommand.Count));
            Assert.Contains("OPENAI_API_KEY=", newSessionArgs);
            Assert.DoesNotContain(newSessionArgs, value => value.Contains("server-secret", StringComparison.Ordinal));
            var sendKeysArgs = Assert.Single(runner.Calls, args => args.Count > 0 && args[0] == "send-keys" && args.Contains("-l"));
            Assert.Contains(sendKeysArgs, value => value.Contains("OPENAI_API_KEY=", StringComparison.Ordinal));
            Assert.DoesNotContain(sendKeysArgs, value => value.Contains("server-secret", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousOpenAiKey);
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_StartsTmuxPaneWithExplicitConfiguredShellCommand()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"pi-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(piStateDir, "agent"));
        await File.WriteAllTextAsync(Path.Combine(piStateDir, "agent", "settings.json"), "{}");
        try
        {
            var runner = new FakeProcessRunner(string.Empty);
            var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
            {
                TmuxShellCommand = ["/bin/bash", "-i"],
                RequiredPiStatePaths = ["agent/settings.json"],
            }, runner);
            var record = Session();
            var profile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []);

            var result = await host.LaunchAsync(new PiSessionLaunchPlan
            {
                Record = record,
                LaunchProfile = profile,
                LaunchCommand = ["docker", "compose", "run", "pi"],
            });

            Assert.Equal(PiSessionStates.Running, result.State);
            var newSessionArgs = Assert.Single(runner.Calls, args => args.Count > 0 && args[0] == "new-session");
            Assert.Equal(["/bin/bash", "-i"], newSessionArgs.TakeLast(2));
        }
        finally
        {
            if (Directory.Exists(piStateDir))
                Directory.Delete(piStateDir, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_FailsBeforeTmuxWhenRequiredPiStateSettingsAreMissing()
    {
        var piStateDir = Path.Combine(Path.GetTempPath(), "den-mcp", $"missing-pi-state-{Guid.NewGuid():N}");
        var runner = new FakeProcessRunner(string.Empty);
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
        {
            RequiredPiStatePaths = ["agent/settings.json"],
        }, runner);

        var result = await host.LaunchAsync(new PiSessionLaunchPlan
        {
            Record = Session(),
            LaunchProfile = Profile(piStateDir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["PI_STATE_DIR"] = piStateDir,
            }, []),
            LaunchCommand = ["docker", "compose", "run", "pi"],
        });

        Assert.Equal(PiSessionStates.Failed, result.State);
        Assert.Contains("PI_STATE_DIR", result.StateReason);
        Assert.Contains("do not fall back to provider environment secrets", result.StateReason);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Cleanup_UsesLaunchProfileDockerEnvironment()
    {
        var runner = new FakeProcessRunner(string.Empty);
        var host = new TmuxDockerPiSessionHost(new PiDockerLaunchProfileOptions
        {
            DockerExecutable = "/usr/bin/docker",
            DockerHost = "unix:///run/den-mcp/docker-rt/fallback.sock",
        }, runner);
        var profile = Profile("/tmp/den-mcp/pi-state", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOCKER_HOST"] = "unix:///run/den-mcp/docker-rt/docker.sock",
            ["DEV_DIR"] = "/tmp/den-mcp/dev",
            ["OPENAI_API_KEY"] = string.Empty,
            ["PI_STATE_DIR"] = "/tmp/den-mcp/pi-state",
        }, ["OPENAI_API_KEY"]);

        var result = await host.CleanupAsync(Session(), profile);

        Assert.True(result.Succeeded);
        var cleanupCallIndex = runner.Calls.FindIndex(args => args.Count > 0 && args[0] == "compose" && args.Contains("down"));
        Assert.True(cleanupCallIndex >= 0);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", runner.Environments[cleanupCallIndex]["DOCKER_HOST"]);
        Assert.Equal("/tmp/den-mcp/dev", runner.Environments[cleanupCallIndex]["DEV_DIR"]);
        Assert.Equal(string.Empty, runner.Environments[cleanupCallIndex]["OPENAI_API_KEY"]);
    }

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

    private static PiDockerLaunchProfile Profile(string piStateDir, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> scrubbedEnvironmentVariables) => new()
    {
        ProfileId = "profile-a",
        ProjectId = "den-mcp",
        SessionId = "session-a",
        ComposeProjectName = "compose-a",
        ComposeFile = "/opt/pi-docker/compose.yaml",
        Service = "pi",
        DevDir = "/tmp/den-mcp/dev",
        PiStateDir = piStateDir,
        Image = "pi-sandbox:test",
        PiVersion = "0.71.0",
        NodeVersion = "22",
        Environment = environment,
        ScrubbedEnvironmentVariables = scrubbedEnvironmentVariables,
        DockerHost = environment.TryGetValue("DOCKER_HOST", out var dockerHost) ? dockerHost : null,
        DockerComposeRunArgs = ["compose", "run", "--name", "container-a", "pi"],
    };

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
        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public FakeProcessRunner(string capturedOutput)
        {
            _capturedOutput = capturedOutput;
        }

        public Task<ProcessRunResult> RunAsync(
            string executable,
            IReadOnlyList<string> args,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(args.ToArray());
            Environments.Add(environment is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(environment, StringComparer.Ordinal));
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

            if (args.Count > 0 && args[0] == "compose" && args.Contains("down"))
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
                });
            }

            if (args.Count > 0 && (args[0] == "new-session" || args[0] == "set-option" || args[0] == "send-keys"))
            {
                return Task.FromResult(new ProcessRunResult
                {
                    ExitCode = 0,
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
