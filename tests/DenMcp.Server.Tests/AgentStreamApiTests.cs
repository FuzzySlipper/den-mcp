using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DenMcp.Server.Tests;

public class AgentStreamApiTests : IAsyncLifetime
{
    private const string ProjectId = "agent-stream-api-test";
    private const string OtherProjectId = "agent-stream-other";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    private AgentStreamAppFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AgentStreamAppFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Agent Stream API Test" });
        await projects.CreateAsync(new Project { Id = OtherProjectId, Name = "Other Project" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RestAgentStream_GlobalAndProjectRoutesHonorFilters()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "note",
            ProjectId = ProjectId,
            Sender = "user",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "FYI"
        });

        var projectOps = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = ProjectId,
            Sender = "codex",
            RecipientAgent = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "review_requested",
            ProjectId = OtherProjectId,
            Sender = "claude-code",
            DeliveryMode = AgentStreamDeliveryMode.Wake
        });

        var globalResponse = await _client.GetAsync("/api/agent-stream?streamKind=ops&sender=codex");
        globalResponse.EnsureSuccessStatusCode();

        var globalEntries = await globalResponse.Content.ReadFromJsonAsync<List<AgentStreamEntry>>(JsonOpts);
        var globalEntry = Assert.Single(globalEntries!);
        Assert.Equal(projectOps.Id, globalEntry.Id);

        var projectResponse = await _client.GetAsync($"/api/projects/{ProjectId}/agent-stream?streamKind=ops");
        projectResponse.EnsureSuccessStatusCode();

        var projectEntries = await projectResponse.Content.ReadFromJsonAsync<List<AgentStreamEntry>>(JsonOpts);
        var projectEntry = Assert.Single(projectEntries!);
        Assert.Equal(projectOps.Id, projectEntry.Id);
        Assert.Equal(ProjectId, projectEntry.ProjectId);
    }

    [Fact]
    public async Task RestSubagentRuns_GroupsOpsByRunId()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = "Sub-agent run host task"
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "Started planner sub-agent.",
            Metadata = Metadata("""{"run_id":"run-a","role":"planner","backend":"pi-cli","model":"gpt-5.5","artifacts":{"dir":"/tmp/run-a"}}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_process_started",
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "planner sub-agent process started.",
            Metadata = Metadata("""{"run_id":"run-a","role":"planner","backend":"pi-cli","model":"gpt-5.5","event":{"type":"subagent.process_started","pid":444}}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_heartbeat",
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "planner sub-agent still running.",
            Metadata = Metadata("""{"run_id":"run-a","role":"planner","backend":"pi-cli","model":"gpt-5.5","event":{"type":"subagent.heartbeat","duration_ms":500}}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_startup_timeout",
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "planner sub-agent hit startup timeout.",
            Metadata = Metadata("""{"run_id":"run-a","role":"planner","backend":"pi-cli","event":{"type":"subagent.startup_timeout"}}""")
        });

        var timeout = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_timeout",
            ProjectId = ProjectId,
            TaskId = task.Id,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "planner sub-agent failed: startup timeout.",
            Metadata = Metadata("""{"run_id":"run-a","role":"planner","backend":"pi-cli","duration_ms":1014,"exit_code":143,"signal":"SIGTERM","timeout_kind":"startup","output_status":"no_assistant_final","infrastructure_failure_reason":"timeout","stderr_preview":"startup timeout"}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = OtherProjectId,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"other-run","role":"coder"}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "dispatch_created",
            ProjectId = ProjectId,
            Sender = "codex",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"not-a-subagent-run"}""")
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/subagent-runs?taskId={task.Id}");
        response.EnsureSuccessStatusCode();

        var runs = await response.Content.ReadFromJsonAsync<List<SubagentRunSummary>>(JsonOpts);
        var run = Assert.Single(runs!);
        Assert.Equal("run-a", run.RunId);
        Assert.Equal("timeout", run.State);
        Assert.Equal(timeout.Id, run.Latest.Id);
        Assert.NotNull(run.Started);
        Assert.Equal("planner", run.Role);
        Assert.Equal(task.Id, run.TaskId);
        Assert.Equal(ProjectId, run.ProjectId);
        Assert.Equal("pi-cli", run.Backend);
        Assert.Equal("gpt-5.5", run.Model);
        Assert.Equal("no_assistant_final", run.OutputStatus);
        Assert.Equal("startup", run.TimeoutKind);
        Assert.Equal("timeout", run.InfrastructureFailureReason);
        Assert.Equal(143, run.ExitCode);
        Assert.Equal("SIGTERM", run.Signal);
        Assert.Equal(444, run.Pid);
        Assert.Equal("startup timeout", run.StderrPreview);
        Assert.Equal(1, run.HeartbeatCount);
        Assert.Equal(0, run.AssistantOutputCount);
        Assert.NotNull(run.LastHeartbeatAt);
        Assert.Null(run.LastAssistantOutputAt);
        Assert.Equal(1014, run.DurationMs);
        Assert.Equal("/tmp/run-a", run.ArtifactDir);
        Assert.Equal(5, run.EventCount);

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/subagent-runs/run-a?taskId={task.Id}");
        detailResponse.EnsureSuccessStatusCode();

        var detail = await detailResponse.Content.ReadFromJsonAsync<SubagentRunDetail>(JsonOpts);
        Assert.NotNull(detail);
        Assert.Equal("run-a", detail!.Summary.RunId);
        Assert.Equal("timeout", detail.Summary.State);
        Assert.Equal(5, detail.Events.Count);
        Assert.Equal("subagent_started", detail.Events[0].EventType);
        Assert.Equal("subagent_process_started", detail.Events[1].EventType);
        Assert.Equal("subagent_heartbeat", detail.Events[2].EventType);
        Assert.Equal("subagent_startup_timeout", detail.Events[3].EventType);
        Assert.Equal("subagent_timeout", detail.Events[4].EventType);

        var scopedMiss = await _client.GetAsync($"/api/projects/{OtherProjectId}/subagent-runs/run-a");
        Assert.Equal(HttpStatusCode.NotFound, scopedMiss.StatusCode);
    }

    [Fact]
    public async Task RestSubagentRuns_ProgressEventsKeepRunRunning()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = ProjectId,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"run-progress","role":"coder","backend":"pi-cli"}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_assistant_output",
            ProjectId = ProjectId,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Body = "coder sub-agent produced assistant output.",
            Metadata = Metadata("""{"run_id":"run-progress","role":"coder","event":{"type":"subagent.assistant_output","chars":24}}""")
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/subagent-runs/run-progress");
        detailResponse.EnsureSuccessStatusCode();

        var detail = await detailResponse.Content.ReadFromJsonAsync<SubagentRunDetail>(JsonOpts);
        Assert.NotNull(detail);
        Assert.Equal("running", detail!.Summary.State);
        Assert.Equal("subagent_assistant_output", detail.Summary.Latest.EventType);
        Assert.Equal(0, detail.Summary.HeartbeatCount);
        Assert.Equal(1, detail.Summary.AssistantOutputCount);
        Assert.NotNull(detail.Summary.LastAssistantOutputAt);
        Assert.Equal(2, detail.Summary.EventCount);
    }

    [Fact]
    public async Task RestSubagentRuns_SurfacesInfrastructureWarningReason()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_started",
            ProjectId = ProjectId,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"run-warning","role":"planner","backend":"pi-cli"}""")
        });

        await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "subagent_completed",
            ProjectId = ProjectId,
            Sender = "pi",
            DeliveryMode = AgentStreamDeliveryMode.RecordOnly,
            Metadata = Metadata("""{"run_id":"run-warning","role":"planner","backend":"pi-cli","output_status":"assistant_final","infrastructure_warning_reason":"extension_runtime"}""")
        });

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/subagent-runs/run-warning");
        detailResponse.EnsureSuccessStatusCode();

        var detail = await detailResponse.Content.ReadFromJsonAsync<SubagentRunDetail>(JsonOpts);
        Assert.NotNull(detail);
        Assert.Equal("complete", detail!.Summary.State);
        Assert.Null(detail.Summary.InfrastructureFailureReason);
        Assert.Equal("extension_runtime", detail.Summary.InfrastructureWarningReason);
    }

    [Fact]
    public async Task RestAgentStream_ProjectScopedGetRejectsEntryFromAnotherProject()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        var otherEntry = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Ops,
            EventType = "wake_requested",
            ProjectId = OtherProjectId,
            Sender = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/agent-stream/{otherEntry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RestAgentStream_ProjectOpsPostCreatesAuditableWakeDelivery()
    {
        var payload = new
        {
            sender = "den-codex-bridge",
            sender_instance_id = "codex-agent-stream-api-test-bridge",
            event_type = "wake_delivered",
            recipient_agent = "codex",
            recipient_role = "implementer",
            recipient_instance_id = "codex-agent-stream-api-test-bridge",
            delivery_mode = "record_only",
            body = "Delivered agent stream entry #101 to Codex bridge.",
            metadata = """{"source_entry_id":101,"thread_id":"thread-1"}""",
            dedup_key = "wake-delivered:agent-stream:101:codex-agent-stream-api-test-bridge"
        };

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-stream/ops", payload);
        response.EnsureSuccessStatusCode();

        var entry = await response.Content.ReadFromJsonAsync<AgentStreamEntry>(JsonOpts);
        Assert.NotNull(entry);
        Assert.Equal(AgentStreamKind.Ops, entry!.StreamKind);
        Assert.Equal("wake_delivered", entry.EventType);
        Assert.Equal(ProjectId, entry.ProjectId);
        Assert.Equal(AgentStreamDeliveryMode.RecordOnly, entry.DeliveryMode);
        Assert.Equal("codex-agent-stream-api-test-bridge", entry.RecipientInstanceId);
        Assert.Equal(101, entry.Metadata!.Value.GetProperty("source_entry_id").GetInt32());

        var repeat = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-stream/ops", payload);
        repeat.EnsureSuccessStatusCode();

        var repeatedEntry = await repeat.Content.ReadFromJsonAsync<AgentStreamEntry>(JsonOpts);
        Assert.Equal(entry.Id, repeatedEntry!.Id);
    }

    [Fact]
    public async Task McpAgentStreamTools_ListAndGetSupportProjectScope()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();

        var entry = await repo.AppendAsync(new AgentStreamEntry
        {
            StreamKind = AgentStreamKind.Message,
            EventType = "question",
            ProjectId = ProjectId,
            Sender = "user",
            RecipientAgent = "codex",
            DeliveryMode = AgentStreamDeliveryMode.Notify,
            Body = "Should I merge this?"
        });

        var listJson = await AgentStreamTools.ListAgentStream(
            repo,
            project_id: ProjectId,
            stream_kind: "message",
            recipient_agent: "codex");
        var list = JsonSerializer.Deserialize<List<AgentStreamEntry>>(listJson, JsonOpts);
        var listed = Assert.Single(list!);
        Assert.Equal(entry.Id, listed.Id);

        var getJson = await AgentStreamTools.GetAgentStreamEntry(repo, entry.Id, project_id: ProjectId);
        var fetched = JsonSerializer.Deserialize<AgentStreamEntry>(getJson, JsonOpts);
        Assert.NotNull(fetched);
        Assert.Equal("question", fetched!.EventType);

        var scopedMissJson = await AgentStreamTools.GetAgentStreamEntry(repo, entry.Id, project_id: OtherProjectId);
        Assert.Contains("not found", scopedMissJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestAgentStream_ProjectMessagePostCreatesWakeableQuestion()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "codex-impl-1",
                ProjectId = ProjectId,
                AgentIdentity = "codex",
                AgentFamily = "codex",
                Role = "implementer",
                TransportKind = "codex_app_server",
                Status = AgentInstanceBindingStatus.Active
            });
        }

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-stream/messages", new
        {
            sender = "user",
            event_type = "question",
            recipient_agent = "codex",
            delivery_mode = "wake",
            body = "Can you take another pass at this?"
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AgentStreamMessageCreateResult>(JsonOpts);
        Assert.NotNull(result);
        Assert.Equal("question", result!.Entry.EventType);
        Assert.Equal(AgentStreamDeliveryMode.Wake, result.Entry.DeliveryMode);
        Assert.NotNull(result.WakeResolution);
        Assert.Equal(AgentRecipientResolutionStatus.Resolved, result.WakeResolution!.Status);
        Assert.Equal("codex-impl-1", result.WakeResolution.Binding!.InstanceId);
    }

    [Fact]
    public async Task RestAgentStream_ProjectMessagePost_RecordOnlyNoteDoesNotWake()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "codex-reviewer-1",
                ProjectId = ProjectId,
                AgentIdentity = "codex",
                AgentFamily = "codex",
                Role = "reviewer",
                TransportKind = "codex_app_server",
                Status = AgentInstanceBindingStatus.Active
            });
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "claude-reviewer-1",
                ProjectId = ProjectId,
                AgentIdentity = "claude-code",
                AgentFamily = "claude",
                Role = "reviewer",
                TransportKind = "claude_channel",
                Status = AgentInstanceBindingStatus.Active
            });
        }

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/agent-stream/messages", new
        {
            sender = "codex",
            event_type = "note",
            recipient_role = "reviewer",
            delivery_mode = "record_only",
            body = "FYI only."
        });
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AgentStreamMessageCreateResult>(JsonOpts);
        Assert.NotNull(result);
        Assert.Equal(AgentStreamDeliveryMode.RecordOnly, result!.Entry.DeliveryMode);
        Assert.Null(result.WakeResolution);

        using var verificationScope = _factory.Services.CreateScope();
        var stream = verificationScope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        var wakeDrops = await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            EventType = "wake_dropped"
        });

        Assert.Empty(wakeDrops);
    }

    [Fact]
    public async Task McpAgentStreamTools_SendMessageSupportsWakeableAnswer()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "codex-reviewer-2",
                ProjectId = ProjectId,
                AgentIdentity = "codex",
                AgentFamily = "codex",
                Role = "reviewer",
                TransportKind = "codex_app_server",
                Status = AgentInstanceBindingStatus.Active
            });
        }

        using var serviceScope = _factory.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<IAgentStreamMessageService>();
        var json = await AgentStreamTools.SendAgentStreamMessage(
            service,
            sender: "user",
            event_type: "answer",
            body: "Yes, proceed.",
            recipient_instance_id: "codex-reviewer-2",
            delivery_mode: "wake");

        var result = JsonSerializer.Deserialize<AgentStreamMessageCreateResult>(json, JsonOpts);
        Assert.NotNull(result);
        Assert.Equal("answer", result!.Entry.EventType);
        Assert.NotNull(result.WakeResolution);
        Assert.Equal(AgentRecipientResolutionStatus.Resolved, result.WakeResolution!.Status);
    }

    private sealed class AgentStreamAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-agent-stream-api-{Guid.NewGuid()}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DenMcp:DatabasePath"] = _dbPath,
                    ["DenMcp:Llm:Endpoint"] = "",
                    ["DenMcp:Llm:Model"] = "test-model"
                });
            });

            builder.ConfigureServices(services =>
            {
                var initializer = new DatabaseInitializer(_dbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient>(new NoOpLlmClient());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private sealed class NoOpLlmClient : ILlmClient
        {
            public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
        }
    }

    private static JsonElement Metadata(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
