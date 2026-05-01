using System.Text;
using System.Text.Json;
using DenMcp.Desktop.Sidecar;

namespace DenMcp.Desktop.Sidecar.Tests;

public class PiSessionSnapshotBuilderTests
{
    [Fact]
    public void ScanPiSessionSnapshots_ReportsUnavailableAndMissingRoots()
    {
        var noRootBuilder = new PiSessionSnapshotBuilder(_ => null, FixedNow);

        var noRoot = noRootBuilder.ScanPiSessionSnapshots(Settings(), [Project(rootPath: "/repo")]);

        Assert.Empty(noRoot.Snapshots);
        Assert.Equal(1, noRoot.WarningCount);

        var missingRoot = Path.Combine(Path.GetTempPath(), "den-missing-runs-" + Guid.NewGuid().ToString("N"));
        var missingRootBuilder = BuilderForRoot(missingRoot);

        var missing = missingRootBuilder.ScanPiSessionSnapshots(Settings(), [Project(rootPath: "/repo")]);

        Assert.Empty(missing.Snapshots);
        Assert.Equal(0, missing.WarningCount);
    }

    [Fact]
    public void ScanPiSessionSnapshots_SortsCandidatesByDirectoryModifiedTimeAndTruncates()
    {
        var root = CreateTempDirectory();
        try
        {
            var modifiedBase = new DateTime(2026, 4, 29, 0, 0, 0, DateTimeKind.Utc);
            for (var index = 0; index < PiSessionSnapshotBuilder.MaxRunDirs + 5; index++)
            {
                var runDirectory = CreateRun(root, $"run-{index}", new
                {
                    run_id = $"run-{index}",
                    cwd = "/repo",
                    state = "running",
                    backend = "pi-cli",
                });
                Directory.SetLastWriteTimeUtc(runDirectory, modifiedBase.AddMinutes(index));
            }

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(Settings(), [Project(rootPath: "/repo")]);

            Assert.Equal(PiSessionSnapshotBuilder.MaxRunDirs, result.Snapshots.Count);
            Assert.Equal("pi-run-run-44", result.Snapshots[0].Request.SessionId);
            Assert.Equal("pi-run-run-5", result.Snapshots[^1].Request.SessionId);
            Assert.All(result.Snapshots, snapshot => Assert.Equal("pending", snapshot.LastPublishStatus));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanPiSessionSnapshots_ParsesStatusMatchesLongestProjectRootAndBuildsObserveOnlyPayloads()
    {
        var root = CreateTempDirectory();
        var repo = Path.Combine(root, "repo");
        var nestedRepo = Path.Combine(repo, "den-mcp");
        Directory.CreateDirectory(nestedRepo);
        try
        {
            var sessionFile = Path.Combine(root, "session.jsonl");
            File.WriteAllLines(sessionFile,
            [
                MessageLine("user", new object[] { new { type = "text", text = "hello" } }),
                MessageLine("assistant", new object[]
                {
                    new { type = "text", text = "Checking status" },
                    new { type = "toolCall", name = "bash", arguments = new { command = "git status" } },
                }),
                ToolResultLine("bash", "git status output"),
            ]);
            var runDirectory = CreateRun(root, "run-status", new
            {
                run_id = "status-run",
                role = "coder",
                task_id = 999,
                cwd = Path.Combine(nestedRepo, "src"),
                state = "running",
                backend = "pi-cli",
                pid = 1234,
                started_at = "2026-04-29T00:00:00.000Z",
                current_phase = (string?)null,
                current_command = (string?)null,
                pi_session_id = "session-123",
                pi_session_file_path = sessionFile,
                workspace_id = "ws-999",
                artifacts = new { dir = Path.Combine(root, "run-status"), session_file_path = sessionFile },
            });
            Directory.SetLastWriteTimeUtc(runDirectory, new DateTime(2026, 4, 29, 1, 0, 0, DateTimeKind.Utc));

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(
                Settings(),
                [Project("dev", rootPath: repo), Project("den-mcp", rootPath: nestedRepo)]);

            var snapshot = Assert.Single(result.Snapshots);
            Assert.Equal("den-mcp", snapshot.ProjectId);
            Assert.Equal(Path.Combine(root, "run-status"), snapshot.ArtifactRoot);
            Assert.Equal(999, snapshot.Request.TaskId);
            Assert.Equal("ws-999", snapshot.Request.WorkspaceId);
            Assert.Equal("session-123", snapshot.Request.SessionId);
            Assert.Equal("pi", snapshot.Request.AgentIdentity);
            Assert.Equal("coder", snapshot.Request.Role);
            Assert.Equal("bash", snapshot.Request.CurrentCommand);
            Assert.Equal("running", snapshot.Request.CurrentPhase);
            Assert.Equal("desktop-test", snapshot.Request.SourceInstanceId);
            Assert.Equal(FixedNow(), snapshot.Request.ObservedAt);
            Assert.Empty(snapshot.Request.Warnings);

            var activityItems = snapshot.Request.RecentActivity.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(3, activityItems.Length);
            Assert.Equal("assistant_tool_call", activityItems[1].GetProperty("kind").GetString());
            Assert.Contains("tool: bash", activityItems[1].GetProperty("summary").GetString(), StringComparison.Ordinal);
            Assert.Equal("tool_result", activityItems[2].GetProperty("kind").GetString());
            Assert.Equal("bash", activityItems[2].GetProperty("tool").GetString());

            // Legacy control_capabilities preserved
            var controlCapabilities = snapshot.Request.ControlCapabilities;
            Assert.Equal("den_desktop_session_capabilities", controlCapabilities.GetProperty("schema").GetString());
            Assert.False(controlCapabilities.GetProperty("can_focus").GetBoolean());
            Assert.False(controlCapabilities.GetProperty("can_stream_raw_terminal").GetBoolean());
            Assert.False(controlCapabilities.GetProperty("can_send_input").GetBoolean());
            Assert.False(controlCapabilities.GetProperty("can_stop").GetBoolean());
            Assert.False(controlCapabilities.GetProperty("can_launch_managed_session").GetBoolean());

            // New first-class OperatorSession fields (task #1009)
            Assert.Equal("status-run", snapshot.Request.Title);
            Assert.Equal("coder", snapshot.Request.DisplayName);
            Assert.Equal(Path.Combine(nestedRepo, "src"), snapshot.Request.Cwd);
            Assert.Equal("artifact_observer", snapshot.Request.Kind);
            Assert.Equal("pi_artifact", snapshot.Request.Backend);
            Assert.Equal("running", snapshot.Request.Status);
            Assert.Equal("2026-04-29T00:00:00.000Z", snapshot.Request.StartedAt);
            Assert.Null(snapshot.Request.ExitedAt);
            Assert.Null(snapshot.Request.ExitCode);
            Assert.Equal("Desktop Test", snapshot.Request.SourceDisplayName);

            // Structured capabilities
            Assert.NotNull(snapshot.Request.Capabilities);
            var capabilities = snapshot.Request.Capabilities!.Value;
            Assert.Equal("den_desktop_session_capabilities_v2", capabilities.GetProperty("schema").GetString());
            Assert.False(capabilities.GetProperty("can_attach").GetBoolean());
            Assert.False(capabilities.GetProperty("can_terminate").GetBoolean());
            Assert.False(capabilities.GetProperty("can_send_input").GetBoolean());
            Assert.True(capabilities.GetProperty("can_read_activity").GetBoolean());

            var children = snapshot.Request.ChildSessions;
            Assert.Equal("den_desktop_session_children", children.GetProperty("schema").GetString());
            Assert.Empty(children.GetProperty("items").EnumerateArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanPiSessionSnapshots_FallsBackToRepoRootShortNameWhenAbsoluteRootDiffers()
    {
        var root = CreateTempDirectory();
        try
        {
            var alternateCheckout = Path.Combine(root, "server", "den-mcp");
            var nestedCwd = Path.Combine(alternateCheckout, "src", "DenMcp.Server");
            Directory.CreateDirectory(nestedCwd);
            Directory.CreateDirectory(Path.Combine(alternateCheckout, ".git"));
            CreateRun(root, "alternate-root", new { cwd = nestedCwd, state = "running" });

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(
                Settings(),
                [Project("den-mcp", rootPath: Path.Combine(root, "dev", "den-mcp")), Project("other", rootPath: Path.Combine(root, "dev", "other"))]);

            var snapshot = Assert.Single(result.Snapshots);
            Assert.Equal("den-mcp", snapshot.ProjectId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanPiSessionSnapshots_FallsBackToPathSegmentShortNameWhenCwdIsNotVisible()
    {
        var root = CreateTempDirectory();
        try
        {
            CreateRun(root, "unmounted-root", new { cwd = "/not-mounted/server/den-mcp/src/DenMcp.Server", state = "running" });

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(
                Settings(),
                [Project("den-mcp", rootPath: "/home/patch/dev/den-mcp"), Project("other", rootPath: "/home/patch/dev/other")]);

            var snapshot = Assert.Single(result.Snapshots);
            Assert.Equal("den-mcp", snapshot.ProjectId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanPiSessionSnapshots_UsesSingleProjectFallbackAndFallbackRunSessionIds()
    {
        var root = CreateTempDirectory();
        try
        {
            CreateRun(root, "fallback-dir", new
            {
                role = "planner",
                state = (string?)null,
                backend = "pi-cli",
                ended_at = "2026-04-29T00:10:00.000Z",
                exit_code = 0,
            });

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(Settings(), [Project(rootPath: "/only-project")]);

            var snapshot = Assert.Single(result.Snapshots);
            Assert.Equal("den-mcp", snapshot.ProjectId);
            Assert.Equal("pi-run-fallback-dir", snapshot.Request.SessionId);
            Assert.Equal("complete", snapshot.Request.CurrentPhase);
            Assert.Equal("pi-cli", snapshot.Request.CurrentCommand);
            Assert.Contains(snapshot.Request.Warnings, warning => warning.Contains("session file path", StringComparison.Ordinal));
            Assert.Contains(snapshot.Request.Warnings, warning => warning.Contains("Session is complete", StringComparison.Ordinal));

            // New first-class fields for completed Pi artifact session
            Assert.Equal("fallback-dir", snapshot.Request.Title);
            Assert.Equal("planner", snapshot.Request.DisplayName);
            Assert.Null(snapshot.Request.Cwd);
            Assert.Equal("artifact_observer", snapshot.Request.Kind);
            Assert.Equal("pi_artifact", snapshot.Request.Backend);
            Assert.Equal("exited", snapshot.Request.Status);
            Assert.Equal("2026-04-29T00:10:00.000Z", snapshot.Request.ExitedAt);
            Assert.Equal(0L, snapshot.Request.ExitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScanPiSessionSnapshots_CapsRecentActivityAndWarnsOnLargeTruncatedSessionFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            var sessionFile = Path.Combine(root, "large-session.jsonl");
            var builder = new StringBuilder();
            builder.Append('x', PiSessionSnapshotBuilder.MaxJsonlBytes);
            builder.Append('\n');
            for (var index = 0; index < 10; index++)
            {
                builder.AppendLine(MessageLine("assistant", new object[] { new { type = "text", text = $"activity-{index}" } }));
            }

            File.WriteAllText(sessionFile, builder.ToString(), Encoding.UTF8);
            CreateRun(root, "large-run", new
            {
                run_id = "large-run",
                cwd = "/repo",
                state = "running",
                pi_session_file_path = sessionFile,
            });

            var result = BuilderForRoot(root).ScanPiSessionSnapshots(Settings(), [Project(rootPath: "/repo")]);

            var snapshot = Assert.Single(result.Snapshots);
            Assert.Contains(snapshot.Request.Warnings, warning => warning.Contains("large/truncated", StringComparison.Ordinal));
            var items = snapshot.Request.RecentActivity.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(PiSessionSnapshotBuilder.MaxRecentActivity, items.Length);
            Assert.Equal("activity-2", items[0].GetProperty("summary").GetString());
            Assert.Equal("activity-9", items[^1].GetProperty("summary").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolvePiRunRoot_PrefersExplicitEnvironmentAndFallsBackToHome()
    {
        var explicitBuilder = new PiSessionSnapshotBuilder(name => name switch
        {
            "PI_SUBAGENT_RUNS_DIR" => " /explicit/runs ",
            "HOME" => "/home/user",
            _ => null,
        }, FixedNow);
        var homeBuilder = new PiSessionSnapshotBuilder(name => name switch
        {
            "HOME" => "/home/user",
            _ => null,
        }, FixedNow);

        Assert.Equal("/explicit/runs", explicitBuilder.ResolvePiRunRoot());
        Assert.Equal(Path.Combine("/home/user", ".pi", "agent", "den-subagent-runs"), homeBuilder.ResolvePiRunRoot());
    }

    private static PiSessionSnapshotBuilder BuilderForRoot(string root)
    {
        return new PiSessionSnapshotBuilder(name => name switch
        {
            "PI_SUBAGENT_RUNS_DIR" => root,
            _ => null,
        }, FixedNow);
    }

    private static OperatorSettings Settings()
    {
        return new OperatorSettings
        {
            DenBaseUrl = "http://localhost:5199",
            SourceInstanceId = "desktop-test",
            SourceDisplayName = "Desktop Test",
            PollIntervalSeconds = 30,
            MaxChangedFiles = 200,
        };
    }

    private static DenProject Project(string id = "den-mcp", string rootPath = "/repo")
    {
        return new DenProject
        {
            Id = id,
            Name = id,
            RootPath = rootPath,
        };
    }

    private static string CreateRun(string root, string runId, object status)
    {
        var runDirectory = Path.Combine(root, runId);
        Directory.CreateDirectory(runDirectory);
        File.WriteAllText(
            Path.Combine(runDirectory, "status.json"),
            JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8);
        return runDirectory;
    }

    private static string MessageLine(string role, object[] content)
    {
        return JsonSerializer.Serialize(new
        {
            type = "message",
            timestamp = "2026-04-29T00:00:00.000Z",
            message = new { role, content },
        });
    }

    private static string ToolResultLine(string toolName, string text)
    {
        return JsonSerializer.Serialize(new
        {
            type = "message",
            timestamp = "2026-04-29T00:00:01.000Z",
            message = new
            {
                role = "toolResult",
                toolName,
                content = new object[] { new { type = "text", text } },
            },
        });
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "den-session-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FixedNow() => "2026-04-29T12:00:00.000Z";
}
