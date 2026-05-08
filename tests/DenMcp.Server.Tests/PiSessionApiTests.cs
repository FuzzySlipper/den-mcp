using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public sealed class PiSessionApiTests : IAsyncLifetime
{
    private const string ProjectId = "den-mcp";
    private PiSessionAppFactory _factory = null!;
    private HttpClient _client = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        _factory = new PiSessionAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Den MCP" });
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Launch pi",
            Status = DenMcp.Core.Models.TaskStatus.InProgress,
        });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Launch_List_Status_AndAttach_ReturnDurableSessionMetadata()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-a",
            task_id = _task.Id,
            workspace_id = "workspace-a",
            run_id = "run-a",
            title = "Pi coder",
            requested_by = "hermes",
            tool_profile = "coding",
            model = "openai-codex/gpt-5.5",
            provider = "openai-codex",
            callback_ports = new[]
            {
                new { host_port = 21455, container_port = 1455 },
            },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var createdJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = createdJson.RootElement;
        var session = detail.GetProperty("session");
        Assert.Equal("session-a", session.GetProperty("session_id").GetString());
        Assert.Equal(ProjectId, session.GetProperty("project_id").GetString());
        Assert.Equal(_task.Id, session.GetProperty("task_id").GetInt32());
        Assert.Equal("workspace-a", session.GetProperty("workspace_id").GetString());
        Assert.Equal("run-a", session.GetProperty("run_id").GetString());
        Assert.Equal("host-test", session.GetProperty("host_id").GetString());
        Assert.Equal("running", session.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("tmux_session_name").GetString()));
        var launchCommand = session.GetProperty("launch_command").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains(launchCommand, value => value is "docker" or "/usr/bin/docker");
        Assert.Contains("DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock", launchCommand);
        Assert.Contains("OPENAI_API_KEY=", launchCommand);
        Assert.DoesNotContain(launchCommand, value => value?.Contains("test-key", StringComparison.Ordinal) == true);
        Assert.Equal("pi_docker_compose", session.GetProperty("launch_profile_kind").GetString());
        Assert.Equal("coding", session.GetProperty("tool_profile").GetString());
        Assert.Equal("openai-codex/gpt-5.5", session.GetProperty("model").GetString());
        Assert.Equal("external_attach_info", detail.GetProperty("attach").GetProperty("mode").GetString());

        var fakeHost = _factory.FakeHost;
        Assert.Single(fakeHost.Launches);
        Assert.Equal("session-a", fakeHost.Launches[0].Record.SessionId);
        Assert.Equal("/srv/dev", fakeHost.Launches[0].LaunchProfile.DevDir);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", fakeHost.Launches[0].LaunchProfile.DockerHost);
        Assert.Equal("unix:///run/den-mcp/docker-rt/docker.sock", fakeHost.Launches[0].LaunchProfile.Environment["DOCKER_HOST"]);
        Assert.Equal(string.Empty, fakeHost.Launches[0].LaunchProfile.Environment["OPENAI_API_KEY"]);
        Assert.Contains("OPENAI_API_KEY", fakeHost.Launches[0].LaunchProfile.ScrubbedEnvironmentVariables);

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}");
        listResponse.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.Equal("session-a", listed.GetProperty("session_id").GetString());
        Assert.Equal("running", listed.GetProperty("state").GetString());
        Assert.True(listed.TryGetProperty("last_activity_at", out _));

        var statusResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-a");
        statusResponse.EnsureSuccessStatusCode();
        using var statusJson = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        Assert.Equal("session-a", statusJson.RootElement.GetProperty("session").GetProperty("session_id").GetString());

        var attachResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-a/attach", new
        {
            requested_by = "hermes",
            mode = "external_attach_info",
        });
        attachResponse.EnsureSuccessStatusCode();
        using var attachJson = JsonDocument.Parse(await attachResponse.Content.ReadAsStringAsync());
        Assert.Equal("tmux", attachJson.RootElement.GetProperty("backend").GetString());
        Assert.Contains("attach-session", attachJson.RootElement.GetProperty("command_args").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task StatusRefreshCapturesBoundedOutputAndSurfacesAttention()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-attention",
            task_id = _task.Id,
            run_id = "run-attention",
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21458, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "working\nDo you want to continue? [y/N]",
            OutputTailCapturedAt = now.AddSeconds(1),
            OutputTailTruncated = true,
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-attention");
        detailResponse.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var session = detailJson.RootElement.GetProperty("session");
        Assert.Equal("run-attention", session.GetProperty("run_id").GetString());
        Assert.Contains("Do you want to continue", session.GetProperty("output_tail").GetString());
        Assert.True(session.GetProperty("output_tail_truncated").GetBoolean());
        Assert.Equal("user_input_needed", session.GetProperty("attention_state").GetString());
        Assert.True(session.GetProperty("needs_user_input").GetBoolean());
        Assert.True(session.TryGetProperty("attention_since_at", out _));
        Assert.True(session.TryGetProperty("last_activity_at", out _));

        var listResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}");
        listResponse.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = listJson.RootElement.EnumerateArray().Single(s => s.GetProperty("session_id").GetString() == "session-attention");
        Assert.Equal("user_input_needed", listed.GetProperty("attention_state").GetString());
        Assert.True(listed.GetProperty("needs_user_input").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        var attentionEntry = Assert.Single(entries, e => e.EventType == "pi_session_attention_needed");
        Assert.Equal(AgentStreamDeliveryMode.Notify, attentionEntry.DeliveryMode);
        Assert.Contains(entries, e => e.EventType == "pi_session_output_tail_updated");
    }

    [Fact]
    public async Task ListCanFilterByAttentionStateAndNeedsUserInput()
    {
        var attentionLaunch = await LaunchSessionAsync("session-filter-attention");
        attentionLaunch.EnsureSuccessStatusCode();
        var passiveLaunch = await LaunchSessionAsync("session-filter-passive");
        passiveLaunch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-filter-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "approval required before proceeding",
            OutputTailCapturedAt = now,
        });
        _factory.FakeHost.SetStatus("session-filter-passive", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now.AddSeconds(-1),
            OutputTail = "still working",
            OutputTailCapturedAt = now,
        });

        var needsInput = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&needsUserInput=true");
        needsInput.EnsureSuccessStatusCode();
        using var needsInputJson = JsonDocument.Parse(await needsInput.Content.ReadAsStringAsync());
        var needsInputSession = Assert.Single(needsInputJson.RootElement.EnumerateArray());
        Assert.Equal("session-filter-attention", needsInputSession.GetProperty("session_id").GetString());
        Assert.True(needsInputSession.GetProperty("needs_user_input").GetBoolean());

        var attentionState = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&attentionState=waiting_for_direction");
        attentionState.EnsureSuccessStatusCode();
        using var attentionStateJson = JsonDocument.Parse(await attentionState.Content.ReadAsStringAsync());
        var attentionStateSession = Assert.Single(attentionStateJson.RootElement.EnumerateArray());
        Assert.Equal("session-filter-attention", attentionStateSession.GetProperty("session_id").GetString());

        var noInput = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions?taskId={_task.Id}&needsUserInput=false");
        noInput.EnsureSuccessStatusCode();
        using var noInputJson = JsonDocument.Parse(await noInput.Content.ReadAsStringAsync());
        var noInputSessions = noInputJson.RootElement.EnumerateArray().Select(s => s.GetProperty("session_id").GetString()).ToList();
        Assert.Contains("session-filter-passive", noInputSessions);
        Assert.DoesNotContain("session-filter-attention", noInputSessions);
    }

    [Fact]
    public async Task StatusRefreshClearsAttentionWhenSessionCompletesAndPostsEvent()
    {
        var launch = await LaunchSessionAsync("session-clear-attention");
        launch.EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        _factory.FakeHost.SetStatus("session-clear-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = now,
            OutputTail = "Do you want to continue? [y/N]",
            OutputTailCapturedAt = now,
        });

        var promptResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-clear-attention");
        promptResponse.EnsureSuccessStatusCode();
        using (var promptJson = JsonDocument.Parse(await promptResponse.Content.ReadAsStringAsync()))
        {
            var prompted = promptJson.RootElement.GetProperty("session");
            Assert.Equal("user_input_needed", prompted.GetProperty("attention_state").GetString());
            Assert.True(prompted.GetProperty("needs_user_input").GetBoolean());
        }

        _factory.FakeHost.SetStatus("session-clear-attention", new PiSessionHostStatus
        {
            State = PiSessionStates.Completed,
            LastActivityAt = now.AddSeconds(10),
            OutputTail = "completed",
            OutputTailCapturedAt = now.AddSeconds(10),
        });

        var completedResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-clear-attention");
        completedResponse.EnsureSuccessStatusCode();
        using var completedJson = JsonDocument.Parse(await completedResponse.Content.ReadAsStringAsync());
        var completed = completedJson.RootElement.GetProperty("session");
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.True(!completed.TryGetProperty("attention_state", out var clearedAttentionState) || clearedAttentionState.ValueKind == JsonValueKind.Null);
        Assert.True(!completed.TryGetProperty("attention_reason", out var clearedAttentionReason) || clearedAttentionReason.ValueKind == JsonValueKind.Null);
        Assert.False(completed.GetProperty("needs_user_input").GetBoolean());

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        Assert.Contains(entries, e => e.EventType == "pi_session_attention_cleared");
    }

    [Fact]
    public async Task StaleActivityIsSurfacedAsStalledAttention()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-stalled",
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21459, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        _factory.FakeHost.SetStatus("session-stalled", new PiSessionHostStatus
        {
            State = PiSessionStates.Running,
            LastActivityAt = DateTime.UtcNow.AddMinutes(-45),
            OutputTail = "still compiling",
            OutputTailCapturedAt = DateTime.UtcNow,
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/session-stalled");
        detailResponse.EnsureSuccessStatusCode();
        using var detailJson = JsonDocument.Parse(await detailResponse.Content.ReadAsStringAsync());
        var session = detailJson.RootElement.GetProperty("session");
        Assert.Equal("stalled", session.GetProperty("attention_state").GetString());
        Assert.False(session.GetProperty("needs_user_input").GetBoolean());
        Assert.Contains("No host-reported activity", session.GetProperty("attention_reason").GetString());
    }

    [Fact]
    public async Task TerminateAndCleanupAreExplicitAndAudited()
    {
        var launch = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-b",
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21456, container_port = 1455 } },
        });
        launch.EnsureSuccessStatusCode();

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-b/terminate", new
        {
            requested_by = "hermes",
            reason = "done",
        });
        terminate.EnsureSuccessStatusCode();
        using var terminatedJson = JsonDocument.Parse(await terminate.Content.ReadAsStringAsync());
        var terminated = terminatedJson.RootElement.GetProperty("session");
        Assert.Equal("completed", terminated.GetProperty("state").GetString());
        Assert.Equal("hermes", terminated.GetProperty("termination_requested_by").GetString());
        Assert.Equal("done", terminated.GetProperty("termination_reason").GetString());
        Assert.True(terminated.TryGetProperty("ended_at", out _));

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-b/cleanup", new
        {
            requested_by = "hermes",
            reason = "remove compose leftovers",
        });
        cleanup.EnsureSuccessStatusCode();
        using var cleanupJson = JsonDocument.Parse(await cleanup.Content.ReadAsStringAsync());
        var cleaned = cleanupJson.RootElement.GetProperty("session");
        Assert.Equal("hermes", cleaned.GetProperty("cleanup_requested_by").GetString());
        Assert.Equal("remove compose leftovers", cleaned.GetProperty("cleanup_reason").GetString());
        Assert.True(cleaned.TryGetProperty("cleanup_completed_at", out _));

        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var entries = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = _task.Id,
            StreamKind = AgentStreamKind.Ops,
            IncludeDebug = true,
            Limit = 20,
        });
        Assert.Contains(entries, e => e.EventType == "pi_session_terminate_requested");
        Assert.Contains(entries, e => e.EventType == "pi_session_cleanup_completed");
        foreach (var piSessionEntry in entries.Where(e => e.EventType.StartsWith("pi_session_", StringComparison.Ordinal)))
        {
            Assert.NotNull(piSessionEntry.DedupKey);
            Assert.StartsWith("pi-session-event:", piSessionEntry.DedupKey);
        }
    }

    [Fact]
    public async Task TerminateAlreadyCompletedSessionReturnsConflict()
    {
        var launch = await LaunchSessionAsync("session-completed");
        launch.EnsureSuccessStatusCode();

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-completed/terminate", new
        {
            requested_by = "hermes",
            reason = "done",
        });
        terminate.EnsureSuccessStatusCode();

        var secondTerminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-completed/terminate", new
        {
            requested_by = "hermes",
            reason = "retry",
        });

        Assert.Equal(HttpStatusCode.Conflict, secondTerminate.StatusCode);
        using var json = JsonDocument.Parse(await secondTerminate.Content.ReadAsStringAsync());
        Assert.Contains("already completed", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CleanupActiveSessionReturnsConflict()
    {
        var launch = await LaunchSessionAsync("session-active-cleanup");
        launch.EnsureSuccessStatusCode();

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/session-active-cleanup/cleanup", new
        {
            requested_by = "hermes",
            reason = "too soon",
        });

        Assert.Equal(HttpStatusCode.Conflict, cleanup.StatusCode);
        using var json = JsonDocument.Parse(await cleanup.Content.ReadAsStringAsync());
        Assert.Contains("terminate it before cleanup", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MissingSessionEndpointsReturnNotFound()
    {
        var detail = await _client.GetAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        var attach = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/attach", new
        {
            requested_by = "hermes",
        });
        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);

        var terminate = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/terminate", new
        {
            requested_by = "hermes",
            reason = "not there",
        });
        Assert.Equal(HttpStatusCode.NotFound, terminate.StatusCode);

        var cleanup = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions/missing-session/cleanup", new
        {
            requested_by = "hermes",
            reason = "not there",
        });
        Assert.Equal(HttpStatusCode.NotFound, cleanup.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("session with spaces")]
    [InlineData("session\twith-tab")]
    public async Task LaunchRejectsExplicitInvalidSessionId(string sessionId)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = sessionId,
            task_id = _task.Id,
            callback_ports = new[] { new { host_port = 21460, container_port = 1455 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("session_id must", json.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LaunchRejectsMissingTaskLink()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = "session-c",
            callback_ports = new[] { new { host_port = 21457, container_port = 1455 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("task_id is required", json.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> LaunchSessionAsync(string sessionId) =>
        _client.PostAsJsonAsync($"/api/projects/{ProjectId}/pi-sessions", new
        {
            session_id = sessionId,
            task_id = _task.Id,
            requested_by = "hermes",
            callback_ports = new[] { new { host_port = 21461, container_port = 1455 } },
        });

    private sealed class PiSessionAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-pi-session-api-{Guid.NewGuid()}.db");
        public FakePiSessionHost FakeHost { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model",
                    ["DenMcp:PiSessionHost:ComposeFile"] = "/opt/pi-docker/compose.yaml",
                    ["DenMcp:PiSessionHost:DevDir"] = "/srv/dev",
                    ["DenMcp:PiSessionHost:PiStateRootDir"] = "/srv/pi-state",
                    ["DenMcp:PiSessionHost:Image"] = "pi-sandbox:test",
                    ["DenMcp:PiSessionHost:PiVersion"] = "0.71.0",
                    ["DenMcp:PiSessionHost:NodeVersion"] = "22",
                    ["DenMcp:PiSessionHost:GitConfigPath"] = "/home/patch/.gitconfig",
                    ["DenMcp:PiSessionHost:SshDir"] = "/home/patch/.ssh",
                    ["DenMcp:PiSessionHost:GhConfigDir"] = "/home/patch/.config/gh",
                    ["DenMcp:PiSessionHost:HostId"] = "host-test",
                    ["DenMcp:PiSessionHost:DockerHost"] = "unix:///run/den-mcp/docker-rt/docker.sock",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());

                services.RemoveAll<PiDockerLaunchProfileOptions>();
                services.AddSingleton(new PiDockerLaunchProfileOptions
                {
                    ComposeFile = "/opt/pi-docker/compose.yaml",
                    DevDir = "/srv/dev",
                    PiStateRootDir = "/srv/pi-state",
                    Image = "pi-sandbox:test",
                    PiVersion = "0.71.0",
                    NodeVersion = "22",
                    GitConfigPath = "/home/patch/.gitconfig",
                    SshDir = "/home/patch/.ssh",
                    GhConfigDir = "/home/patch/.config/gh",
                    HostId = "host-test",
                    DockerHost = "unix:///run/den-mcp/docker-rt/docker.sock",
                });

                services.RemoveAll<IPiSessionHost>();
                services.AddSingleton<IPiSessionHost>(FakeHost);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private sealed class FakePiSessionHost : IPiSessionHost
    {
        private readonly Dictionary<string, PiSessionHostStatus> _statuses = new(StringComparer.Ordinal);
        public List<PiSessionLaunchPlan> Launches { get; } = [];
        public string HostId => "host-test";

        public Task<PiSessionHostLaunchResult> LaunchAsync(PiSessionLaunchPlan plan, CancellationToken cancellationToken = default)
        {
            Launches.Add(plan);
            var now = DateTime.UtcNow;
            _statuses[plan.Record.SessionId] = new PiSessionHostStatus
            {
                State = PiSessionStates.Running,
                LastActivityAt = now.AddSeconds(1),
                ContainerName = plan.Record.ContainerName,
            };
            return Task.FromResult(new PiSessionHostLaunchResult
            {
                State = PiSessionStates.Running,
                StartedAt = now,
                LastActivityAt = now,
                ContainerName = plan.Record.ContainerName,
            });
        }

        public void SetStatus(string sessionId, PiSessionHostStatus status) => _statuses[sessionId] = status;

        public Task<PiSessionHostStatus> GetStatusAsync(PiSessionRecord session, CancellationToken cancellationToken = default) =>
            Task.FromResult(_statuses.GetValueOrDefault(session.SessionId) ?? new PiSessionHostStatus { State = PiSessionStates.Stale });

        public Task<PiSessionHostControlResult> TerminateAsync(PiSessionRecord session, CancellationToken cancellationToken = default)
        {
            _statuses[session.SessionId] = new PiSessionHostStatus { State = PiSessionStates.Completed };
            return Task.FromResult(new PiSessionHostControlResult
            {
                Succeeded = true,
                State = PiSessionStates.Completed,
                EndedAt = DateTime.UtcNow,
                StateReason = "terminated by fake host",
            });
        }

        public Task<PiSessionHostControlResult> CleanupAsync(PiSessionRecord session, PiDockerLaunchProfile? profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PiSessionHostControlResult
            {
                Succeeded = true,
                State = session.State,
                StateReason = "cleanup by fake host",
            });
    }

    private sealed class NoOpLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default) => Task.FromResult("{}");
    }
}
