using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

public class DispatchApiTests : IAsyncLifetime
{
    private DispatchAppFactory _factory = null!;
    private HttpClient _client = null!;

    // Use the _global project which is auto-seeded by DatabaseInitializer
    private const string ProjectId = "_global";

    public Task InitializeAsync()
    {
        _factory = new DispatchAppFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<DispatchEntry> CreatePendingDispatchAsync(
        int triggerId = 1,
        string agent = "claude-code",
        string? contextJson = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var (entry, _) = await repo.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = ProjectId,
            TargetAgent = agent,
            TriggerType = DispatchTriggerType.TaskStatus,
            TriggerId = triggerId,
            Summary = $"Test dispatch {triggerId}",
            ContextJson = contextJson,
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.TaskStatus, triggerId, agent),
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        return entry;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    #region List

    [Fact]
    public async Task List_ReturnsAllDispatches()
    {
        await CreatePendingDispatchAsync(1);
        await CreatePendingDispatchAsync(2);

        var response = await _client.GetAsync("/api/dispatch");
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<List<DispatchEntry>>(JsonOpts);
        Assert.Equal(2, entries!.Count);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var entry = await CreatePendingDispatchAsync(1);
        await CreatePendingDispatchAsync(2);

        // Approve one
        await _client.PostAsJsonAsync($"/api/dispatch/{entry.Id}/approve", new { decided_by = "user" });

        var pending = await _client.GetFromJsonAsync<List<DispatchEntry>>("/api/dispatch?status=pending", JsonOpts);
        Assert.Single(pending!);

        var approved = await _client.GetFromJsonAsync<List<DispatchEntry>>("/api/dispatch?status=approved", JsonOpts);
        Assert.Single(approved!);
    }

    [Fact]
    public async Task List_FiltersByProjectAndAgent()
    {
        await CreatePendingDispatchAsync(1, "claude-code");
        await CreatePendingDispatchAsync(2, "codex");

        var claudeOnly = await _client.GetFromJsonAsync<List<DispatchEntry>>(
            "/api/dispatch?targetAgent=claude-code", JsonOpts);
        Assert.Single(claudeOnly!);

        var projOnly = await _client.GetFromJsonAsync<List<DispatchEntry>>(
            "/api/dispatch?projectId=_global", JsonOpts);
        Assert.Equal(2, projOnly!.Count);
    }

    #endregion

    #region Get by ID

    [Fact]
    public async Task GetById_ReturnsDispatch()
    {
        var entry = await CreatePendingDispatchAsync();

        var response = await _client.GetAsync($"/api/dispatch/{entry.Id}");
        response.EnsureSuccessStatusCode();

        var fetched = await response.Content.ReadFromJsonAsync<DispatchEntry>(JsonOpts);
        Assert.Equal(entry.Id, fetched!.Id);
        Assert.Equal(ProjectId, fetched.ProjectId);
    }

    [Fact]
    public async Task GetById_NotFound_Returns404()
    {
        var response = await _client.GetAsync("/api/dispatch/9999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetContext_ReturnsStructuredDispatchContext()
    {
        var contextJson = JsonSerializer.Serialize(new DispatchContextSnapshot
        {
            ContextKind = "review_feedback",
            ProjectId = ProjectId,
            TargetAgent = "claude-code",
            ActivityHint = "working",
            WorkflowGuardrails = ["guardrail"],
            NextActions = ["next step"]
        });
        var entry = await CreatePendingDispatchAsync(contextJson: contextJson);

        var response = await _client.GetAsync($"/api/dispatch/{entry.Id}/context");
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<DispatchContextEnvelope>(JsonOpts);
        Assert.NotNull(envelope);
        Assert.Equal(entry.Id, envelope!.Dispatch.Id);
        Assert.Equal("review_feedback", envelope.Context.ContextKind);
        Assert.Equal("claude-code", envelope.Context.TargetAgent);
        Assert.Equal("working", envelope.Context.ActivityHint);
        Assert.Equal("next step", Assert.Single(envelope.Context.NextActions));
    }

    [Fact]
    public async Task GetContext_FallbackUsesMatchedTriggerToResolveAddressedVia()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
            await docs.UpsertAsync(new Document
            {
                ProjectId = ProjectId,
                Slug = "dispatch-routing",
                Title = "Dispatch Routing",
                DocType = DocType.Convention,
                Content = """
                    {
                      "roles": {
                        "reviewer": "codex"
                      },
                      "triggers": [
                        {
                          "event": "message_received",
                          "has_target_role": true,
                          "dispatch_to": "{target_role}"
                        }
                      ],
                      "defaults": {
                        "auto_approve": false,
                        "expiry_minutes": 1440
                      }
                    }
                    """
            });

            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var message = await messages.CreateAsync(new Message
            {
                ProjectId = ProjectId,
                Sender = "claude-code",
                Content = "Please review this role-targeted handoff.",
                Intent = MessageIntent.ReviewRequest,
                Metadata = JsonSerializer.Deserialize<JsonElement>(
                    """{"recipient":"claude-code","target_role":"reviewer","handoff_kind":"review_request"}""")
            });

            var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
            await dispatches.CreateIfAbsentAsync(new DispatchEntry
            {
                ProjectId = ProjectId,
                TargetAgent = "codex",
                TriggerType = DispatchTriggerType.Message,
                TriggerId = message.Id,
                Summary = "Fallback addressedVia test",
                ContextJson = null,
                DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, message.Id, "codex"),
                ExpiresAt = DateTime.UtcNow.AddHours(24)
            });
        }

        var entries = await _client.GetFromJsonAsync<List<DispatchEntry>>("/api/dispatch?targetAgent=codex", JsonOpts);
        var entry = Assert.Single(entries!);

        var response = await _client.GetAsync($"/api/dispatch/{entry.Id}/context");
        response.EnsureSuccessStatusCode();

        var envelope = await response.Content.ReadFromJsonAsync<DispatchContextEnvelope>(JsonOpts);
        Assert.NotNull(envelope);
        Assert.Equal("target_role", envelope!.Context.AddressedVia);
        Assert.Equal("reviewer", envelope.Context.MessageTargetRole);
        Assert.Equal("claude-code", envelope.Context.Recipient);
    }

    #endregion

    #region Approve / Reject / Complete

    [Fact]
    public async Task Approve_TransitionsToPending()
    {
        var entry = await CreatePendingDispatchAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/dispatch/{entry.Id}/approve", new { decided_by = "george" });
        response.EnsureSuccessStatusCode();

        var approved = await response.Content.ReadFromJsonAsync<DispatchEntry>(JsonOpts);
        Assert.Equal(DispatchStatus.Approved, approved!.Status);
        Assert.Equal("george", approved.DecidedBy);

        var streamEntries = await ListAgentStreamAsync(entry.Id);
        Assert.Contains(streamEntries, streamEntry => streamEntry.EventType == "dispatch_created");
        Assert.Contains(streamEntries, streamEntry => streamEntry.EventType == "dispatch_approved");
        Assert.Contains(streamEntries, streamEntry => streamEntry.EventType == "wake_requested");
    }

    [Fact]
    public async Task Reject_TransitionsToRejected()
    {
        var entry = await CreatePendingDispatchAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/dispatch/{entry.Id}/reject", new { decided_by = "george" });
        response.EnsureSuccessStatusCode();

        var rejected = await response.Content.ReadFromJsonAsync<DispatchEntry>(JsonOpts);
        Assert.Equal(DispatchStatus.Rejected, rejected!.Status);

        var streamEntries = await ListAgentStreamAsync(entry.Id);
        Assert.Contains(streamEntries, streamEntry => streamEntry.EventType == "dispatch_created");
        Assert.Contains(streamEntries, streamEntry => streamEntry.EventType == "dispatch_rejected");
    }

    [Fact]
    public async Task Complete_AfterApproval()
    {
        var entry = await CreatePendingDispatchAsync();
        await _client.PostAsJsonAsync($"/api/dispatch/{entry.Id}/approve", new { decided_by = "george" });

        var response = await _client.PostAsJsonAsync(
            $"/api/dispatch/{entry.Id}/complete", new { completed_by = "claude-code" });
        response.EnsureSuccessStatusCode();

        var completed = await response.Content.ReadFromJsonAsync<DispatchEntry>(JsonOpts);
        Assert.Equal(DispatchStatus.Completed, completed!.Status);
        Assert.Equal("george", completed.DecidedBy);
        Assert.Equal("claude-code", completed.CompletedBy);
    }

    [Fact]
    public async Task Complete_WithoutApproval_Returns400()
    {
        var entry = await CreatePendingDispatchAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/dispatch/{entry.Id}/complete", new { completed_by = "claude-code" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    #endregion

    private async Task<List<AgentStreamEntry>> ListAgentStreamAsync(int dispatchId)
    {
        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        return await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            DispatchId = dispatchId,
            StreamKind = AgentStreamKind.Ops,
            Limit = 20
        });
    }

    #region Pending count

    [Fact]
    public async Task PendingCount_ReturnsCount()
    {
        await CreatePendingDispatchAsync(1);
        await CreatePendingDispatchAsync(2);
        var entry3 = await CreatePendingDispatchAsync(3);
        await _client.PostAsJsonAsync($"/api/dispatch/{entry3.Id}/approve", new { decided_by = "user" });

        var response = await _client.GetAsync("/api/dispatch/pending/count");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"count\":2", json);
    }

    [Fact]
    public async Task PendingCount_FiltersByProject()
    {
        await CreatePendingDispatchAsync(1);

        var response = await _client.GetAsync("/api/dispatch/pending/count?projectId=nonexistent");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"count\":0", json);
    }

    #endregion

    #region Not found vs bad transition

    [Fact]
    public async Task Approve_NonexistentId_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/dispatch/9999/approve", new { decided_by = "user" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reject_NonexistentId_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/dispatch/9999/reject", new { decided_by = "user" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Complete_NonexistentId_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/dispatch/9999/complete", new { completed_by = "agent" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    #endregion

    #region Invalid status filter

    [Fact]
    public async Task List_InvalidStatusFilter_Returns400()
    {
        var response = await _client.GetAsync("/api/dispatch?status=pendng");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", json);
    }

    #endregion

    #region Snake case JSON

    [Fact]
    public async Task Response_UsesSnakeCaseJson()
    {
        var entry = await CreatePendingDispatchAsync();

        var response = await _client.GetAsync($"/api/dispatch/{entry.Id}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"project_id\"", json);
        Assert.Contains("\"target_agent\"", json);
        Assert.Contains("\"trigger_type\"", json);
        Assert.Contains("\"dedup_key\"", json);
        Assert.Contains("\"created_at\"", json);
        Assert.Contains("\"expires_at\"", json);
        Assert.DoesNotContain("\"ProjectId\"", json);
        Assert.DoesNotContain("\"TargetAgent\"", json);
    }

    [Fact]
    public async Task McpGetDispatchContext_ReturnsStructuredContext()
    {
        var contextJson = JsonSerializer.Serialize(new DispatchContextSnapshot
        {
            ContextKind = "handoff",
            ProjectId = ProjectId,
            TargetAgent = "claude-code",
            WorkflowGuardrails = ["guardrail"],
            NextActions = ["continue the handoff"]
        });
        var entry = await CreatePendingDispatchAsync(contextJson: contextJson);

        using var scope = _factory.Services.CreateScope();
        var contexts = scope.ServiceProvider.GetRequiredService<IDispatchContextService>();

        var json = await DispatchTools.GetDispatchContext(contexts, entry.Id);
        var envelope = JsonSerializer.Deserialize<DispatchContextEnvelope>(json, JsonOpts);

        Assert.NotNull(envelope);
        Assert.Equal(entry.Id, envelope!.Dispatch.Id);
        Assert.Equal("handoff", envelope.Context.ContextKind);
        Assert.Equal("continue the handoff", Assert.Single(envelope.Context.NextActions));
    }

    #endregion

    private sealed class DispatchAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-dispatch-test-{Guid.NewGuid()}.db");

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
                // Override DB to use isolated test database
                var initializer = new DatabaseInitializer(_dbPath,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseInitializer>.Instance);
                initializer.InitializeAsync().GetAwaiter().GetResult();

                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory(initializer.ConnectionString));

                // Provide a no-op LLM client since dispatch tests don't need librarian
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
                => Task.FromResult("{}");
        }
    }
}
