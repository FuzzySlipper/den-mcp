using System.Net;
using System.Net.Http.Json;
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
using Microsoft.Extensions.Logging;

namespace DenMcp.Server.Tests;

/// <summary>
/// Integration tests proving the server wiring points keep primary writes safe while
/// automatic dispatch creation stays retired unless a legacy routing document opts in.
/// </summary>
public class DispatchDetectionWiringTests : IAsyncLifetime
{
    private WiringAppFactory _factory = null!;
    private HttpClient _client = null!;
    private const string ProjectId = "wiring-test";

    public async Task InitializeAsync()
    {
        _factory = new WiringAppFactory();
        _client = _factory.CreateClient();
        await SeedProjectAsync(_factory.Services);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task SeedProjectAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Wiring Test" });
    }

    private static async Task EnableLegacyDispatchRoutingAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        await docs.UpsertAsync(new Document
        {
            ProjectId = ProjectId,
            Slug = "dispatch-routing",
            Title = "Legacy Dispatch Routing",
            Content = """
            {
              "roles": {
                "implementer": "claude-code",
                "reviewer": "codex"
              },
              "triggers": [
                {
                  "event": "task_status_changed",
                  "to_status": "review",
                  "dispatch_to": "reviewer"
                },
                {
                  "event": "task_status_changed",
                  "from_status": "review",
                  "to_status": "planned",
                  "dispatch_to": "implementer"
                },
                {
                  "event": "message_received",
                  "has_recipient": true,
                  "dispatch_to": "{recipient}"
                },
                {
                  "event": "message_received",
                  "has_target_role": true,
                  "dispatch_to": "{target_role}"
                }
              ],
              "defaults": {
                "legacy_dispatch_enabled": true,
                "expiry_minutes": 1440
              }
            }
            """,
            DocType = DocType.Convention
        });
    }

    private async Task<int> SeedTaskAsync(IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Wiring test task" });
        return task.Id;
    }

    private async Task<List<DispatchEntry>> GetDispatchesAsync(IServiceProvider? services = null)
    {
        using var scope = (services ?? _factory.Services).CreateScope();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        return await dispatches.ListAsync(ProjectId, statuses: [DispatchStatus.Pending]);
    }

    private async Task<DispatchEntry> CreateDispatchAsync(
        int taskId,
        int triggerId,
        string targetAgent,
        bool approve = false)
    {
        using var scope = _factory.Services.CreateScope();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var (dispatch, _) = await dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = ProjectId,
            TargetAgent = targetAgent,
            TriggerType = DispatchTriggerType.Message,
            TriggerId = triggerId,
            TaskId = taskId,
            Summary = $"Dispatch {triggerId}",
            ContextPrompt = "Context",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, triggerId, targetAgent),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });

        return approve
            ? await dispatches.ApproveAsync(dispatch.Id, "user")
            : dispatch;
    }

    #region REST wiring — automatic dispatch creation is retired by default

    [Fact]
    public async Task RestMessageCreate_DoesNotCreateDispatchByDefault()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback via REST",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });
        response.EnsureSuccessStatusCode();

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task RestMessageCreate_WithIntentOnly_DoesNotCreateDispatchByDefault()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback via REST intent",
            intent = "review_feedback",
            metadata = """{"recipient":"claude-code","handoff_kind":"review_feedback"}"""
        });
        response.EnsureSuccessStatusCode();

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task RestMessageCreate_WithTargetRole_DoesNotCreateDispatchByDefault()
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "claude-code",
            content = "Review feedback via REST role targeting",
            intent = "review_request",
            metadata = """{"target_role":"reviewer","handoff_kind":"review_request"}"""
        });
        response.EnsureSuccessStatusCode();

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task RestTaskUpdate_DoesNotCreateDispatchByDefault()
    {
        var taskId = await SeedTaskAsync();

        var response = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}", new
        {
            agent = "claude-code",
            status = "review"
        });
        response.EnsureSuccessStatusCode();

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task RestTaskUpdate_ToDone_ExpiresOpenDispatchesForTask()
    {
        var taskId = await SeedTaskAsync();
        var pending = await CreateDispatchAsync(taskId, 100, "codex");
        var approved = await CreateDispatchAsync(taskId, 101, "claude-code", approve: true);

        var response = await _client.PutAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}", new
        {
            agent = "claude-code",
            status = "done"
        });
        response.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        Assert.Equal(DispatchStatus.Expired, (await dispatches.GetByIdAsync(pending.Id))!.Status);
        Assert.Equal(DispatchStatus.Expired, (await dispatches.GetByIdAsync(approved.Id))!.Status);
    }

    [Fact]
    public async Task RestMessageCreate_WithLegacyDispatchRouting_CreatesDispatch()
    {
        await EnableLegacyDispatchRoutingAsync(_factory.Services);

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Review feedback via REST",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });
        response.EnsureSuccessStatusCode();

        var dispatches = await GetDispatchesAsync();
        var dispatch = Assert.Single(dispatches);
        Assert.Equal("claude-code", dispatch.TargetAgent);
    }

    #endregion

    #region MCP tool wiring — automatic dispatch creation is retired by default

    [Fact]
    public async Task McpSendMessage_DoesNotCreateDispatchByDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();

        await MessageTools.SendMessage(repo, detection, logger,
            ProjectId, "codex", "MCP message content",
            metadata: """{"type":"review_feedback","recipient":"claude-code"}""");

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task McpSendMessage_WithIntentOnly_DoesNotCreateDispatchByDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();

        await MessageTools.SendMessage(repo, detection, logger,
            ProjectId, "codex", "MCP intent message",
            metadata: """{"recipient":"claude-code","handoff_kind":"review_feedback"}""",
            intent: "review_feedback");

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task McpSendMessage_WithTargetRole_DoesNotCreateDispatchByDefault()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();

        await MessageTools.SendMessage(repo, detection, logger,
            ProjectId, "claude-code", "MCP role-targeted message",
            metadata: """{"target_role":"reviewer","handoff_kind":"review_request"}""",
            intent: "review_request");

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    [Fact]
    public async Task McpUpdateTask_DoesNotCreateDispatchByDefault()
    {
        var taskId = await SeedTaskAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();

        await TaskTools.UpdateTask(repo, detection, logger,
            taskId, "claude-code", status: "review");

        var dispatches = await GetDispatchesAsync();
        Assert.Empty(dispatches);
    }

    #endregion

    #region Error isolation — primary write succeeds when detection throws

    [Fact]
    public async Task RestMessageCreate_SucceedsWhenDetectionFails()
    {
        using var factory = new WiringAppFactory(useFailingDetection: true);
        using var client = factory.CreateClient();
        await SeedProjectAsync(factory.Services);

        var response = await client.PostAsJsonAsync($"/api/projects/{ProjectId}/messages", new
        {
            sender = "codex",
            content = "Should persist despite detection failure",
            metadata = """{"type":"review_feedback","recipient":"claude-code"}"""
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task RestTaskUpdate_SucceedsWhenDetectionFails()
    {
        using var factory = new WiringAppFactory(useFailingDetection: true);
        using var client = factory.CreateClient();
        await SeedProjectAsync(factory.Services);
        var taskId = await SeedTaskAsync(factory.Services);

        var response = await client.PutAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}", new
        {
            agent = "claude-code",
            status = "review"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task McpSendMessage_SucceedsWhenDetectionFails()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();
        var failingDetection = new FailingDetectionService();

        var result = await MessageTools.SendMessage(repo, failingDetection, logger,
            ProjectId, "codex", "Should persist despite detection failure",
            metadata: """{"type":"review_feedback","recipient":"claude-code"}""");

        Assert.Contains("\"id\"", result);
        Assert.Contains(ProjectId, result);
    }

    [Fact]
    public async Task McpUpdateTask_SucceedsWhenDetectionFails()
    {
        var taskId = await SeedTaskAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();
        var failingDetection = new FailingDetectionService();

        var result = await TaskTools.UpdateTask(repo, failingDetection, logger,
            taskId, "claude-code", status: "review");

        Assert.Contains("\"id\"", result);
        Assert.Contains("review", result);
    }

    #endregion

    private sealed class FailingDetectionService : IDispatchDetectionService
    {
        public Task OnMessageCreatedAsync(Message message)
            => throw new InvalidOperationException("Simulated dispatch detection failure");

        public Task OnTaskStatusChangedAsync(ProjectTask task, string fromStatus, string toStatus, string changedBy)
            => throw new InvalidOperationException("Simulated dispatch detection failure");
    }

    private sealed class WiringAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-wiring-test-{Guid.NewGuid()}.db");
        private readonly bool _useFailingDetection;

        public WiringAppFactory(bool useFailingDetection = false)
        {
            _useFailingDetection = useFailingDetection;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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

                if (_useFailingDetection)
                {
                    services.RemoveAll<IDispatchDetectionService>();
                    services.AddSingleton<IDispatchDetectionService>(new FailingDetectionService());
                }
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
                => Task.FromResult("{}");
        }
    }
}
