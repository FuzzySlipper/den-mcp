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
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class ReviewWorkflowApiTests : IAsyncLifetime
{
    private ReviewWorkflowAppFactory _factory = null!;
    private HttpClient _client = null!;
    private const string ProjectId = "proj";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new ReviewWorkflowAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Test" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetTaskDetail_IncludesReviewWorkflowSummary()
    {
        var task = await CreateTaskAsync("Workflow summary");
        var round = await CreateRoundAsync(task.Id);
        await CreateFindingAsync(task.Id, round.Id, "Still open");
        var resolved = await CreateFindingAsync(task.Id, round.Id, "Resolved later");

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{resolved.Id}/status", new
        {
            status = "verified_fixed",
            updated_by = "codex",
            notes = "Confirmed"
        });

        var response = await _client.GetAsync($"/api/projects/{ProjectId}/tasks/{task.Id}");
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<TaskDetail>(JsonOpts);
        Assert.NotNull(detail);
        Assert.NotNull(detail!.ReviewWorkflow.CurrentRound);
        Assert.Equal(1, detail.ReviewWorkflow.ReviewRoundCount);
        Assert.Equal(1, detail.ReviewWorkflow.UnresolvedFindingCount);
        Assert.Equal(1, detail.ReviewWorkflow.ResolvedFindingCount);
        Assert.Single(detail.ReviewWorkflow.Timeline);
        Assert.Equal(2, detail.ReviewWorkflow.Timeline[0].TotalFindingCount);
    }

    [Fact]
    public async Task RequestReview_PostsStandardizedPacket()
    {
        var task = await CreateTaskAsync("Request review");
        var firstRound = await CreateRoundAsync(task.Id, "bbb222");
        var addressed = await CreateFindingAsync(task.Id, firstRound.Id, "Handled");
        var stillOpen = await CreateFindingAsync(task.Id, firstRound.Id, "Still open");

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{addressed.Id}/response", new
        {
            responded_by = "claude-code",
            response_notes = "Fixed on branch",
            status = "claimed_fixed",
            status_notes = "Ready for rereview"
        });

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review/request", new
        {
            requested_by = "claude-code",
            branch = "task/597-review-packet-ux",
            base_branch = "task/596-stacked-diff-metadata",
            base_commit = "ccc333",
            head_commit = "ddd444",
            commits_since_last_review = 2,
            tests_run = new[] { "dotnet test den-mcp.slnx --filter ReviewWorkflowApiTests" },
            preferred_diff_base_ref = "task/596-stacked-diff-metadata",
            alternate_diff_base_ref = "main",
            alternate_diff_base_commit = "aaa111",
            delta_base_commit = "bbb222",
            notes = "Scope narrowed to workflow polish"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ReviewPacketResult>(JsonOpts);

        Assert.NotNull(result);
        Assert.Equal(2, result!.ReviewRound!.RoundNumber);
        Assert.Equal(ReviewPacketKind.RereviewRequest, result.Packet.Kind);
        Assert.Contains(addressed.FindingKey, result.Packet.Content);
        Assert.Contains(stillOpen.FindingKey, result.Packet.Content);
        Assert.Contains("Tests run by implementer:", result.Packet.Content);
        Assert.Equal("claude-code", result.Message.Sender);
        Assert.Equal("reviewer", result.Message.Metadata!.Value.GetProperty("target_role").GetString());

        var dispatches = await ListDispatchesAsync(targetAgent: "codex", statuses: [DispatchStatus.Pending]);
        Assert.Empty(dispatches);

        var streamEntries = await ListAgentStreamAsync(taskId: task.Id);
        Assert.Contains(streamEntries, entry => entry.EventType == "rereview_requested" && entry.RecipientRole == "reviewer");
        Assert.DoesNotContain(streamEntries, entry => entry.EventType == "dispatch_created");
    }

    [Fact]
    public async Task PostReviewFindings_PostsStructuredPacket()
    {
        var task = await CreateTaskAsync("Post findings");
        var round = await CreateRoundAsync(task.Id);
        var finding = await CreateFindingAsync(task.Id, round.Id, "Wrong diff selected");

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds/{round.Id}/verdict", new
        {
            verdict = "changes_requested",
            decided_by = "codex",
            notes = "Needs another pass"
        });

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review/findings/post", new
        {
            review_round_id = round.Id,
            sender = "codex",
            notes = "Please address before merge"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ReviewPacketResult>(JsonOpts);

        Assert.NotNull(result);
        Assert.Equal(ReviewPacketKind.ReviewFindings, result!.Packet.Kind);
        Assert.Contains("Verdict: `changes_requested`", result.Packet.Content);
        Assert.Contains(finding.FindingKey, result.Packet.Content);
        Assert.Contains("Files: `src/DenMcp.Server/Routes/TaskRoutes.cs:320`", result.Packet.Content);
        Assert.Contains("Tests: `dotnet test den-mcp.slnx --filter ReviewWorkflowApiTests`", result.Packet.Content);
    }

    [Fact]
    public async Task RequestReview_Returns400_WhenThreadBelongsToDifferentTask()
    {
        var task = await CreateTaskAsync("Request review");
        var otherTask = await CreateTaskAsync("Other task");
        var thread = await CreateMessageAsync(otherTask.Id, "codex", "Other task thread");

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review/request", new
        {
            requested_by = "claude-code",
            branch = "task/597-review-packet-ux",
            base_branch = "main",
            base_commit = "aaa111",
            head_commit = "bbb222",
            thread_id = thread.Id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RestSetVerdict_PostsFeedbackHandoffAndResolvesReviewerDispatch()
    {
        var task = await CreateTaskAsync("REST verdict handoff", assignedTo: "claude-code");
        var reviewRequest = await CreateReviewRequestPacketAsync(task.Id, "claude-code");
        await CreateFindingAsync(task.Id, reviewRequest.ReviewRound!.Id, "Missing merge guard");
        var reviewerDispatch = await CreateApprovedReviewerDispatchAsync(task.Id, reviewRequest.Message.Id, "codex");

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds/{reviewRequest.ReviewRound.Id}/verdict",
            new
            {
                verdict = "changes_requested",
                decided_by = "codex",
                notes = "Please address before merge"
            });

        response.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        var taskMessages = await messages.GetMessagesAsync(ProjectId, task.Id, limit: 20);
        var handoff = Assert.Single(taskMessages, message =>
            message.Intent == MessageIntent.ReviewFeedback &&
            message.Metadata.HasValue &&
            message.Metadata.Value.GetProperty("handoff_kind").GetString() == "review_feedback");

        Assert.Equal(reviewRequest.Message.Id, handoff.ThreadId);
        Assert.Equal("claude-code", handoff.Metadata!.Value.GetProperty("recipient").GetString());

        var implementerDispatches = await dispatches.ListAsync(ProjectId, "claude-code", [DispatchStatus.Pending]);
        Assert.Empty(implementerDispatches);

        var completedReviewerDispatch = await dispatches.GetByIdAsync(reviewerDispatch.Id);
        Assert.Equal(DispatchStatus.Completed, completedReviewerDispatch!.Status);

        var streamEntries = await ListAgentStreamAsync(taskId: task.Id);
        Assert.Contains(streamEntries, entry => entry.EventType == "changes_requested" && entry.RecipientAgent == "claude-code");
        Assert.DoesNotContain(streamEntries, entry => entry.EventType == "dispatch_created" && entry.RecipientAgent == "claude-code");
        Assert.DoesNotContain(streamEntries, entry => entry.EventType == "merge_handoff");
    }

    [Fact]
    public async Task McpSetVerdict_PostsMergeHandoff()
    {
        var task = await CreateTaskAsync("MCP merge handoff");
        var reviewRequest = await CreateReviewRequestPacketAsync(task.Id, "claude-code");

        using (var scope = _factory.Services.CreateScope())
        {
            var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();
            var result = await TaskTools.SetReviewVerdict(
                workflow,
                reviewRequest.ReviewRound!.Id,
                "looks_good",
                "codex",
                "Approved for merge");

            Assert.Contains("looks_good", result);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
            var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

            var taskMessages = await messages.GetMessagesAsync(ProjectId, task.Id, limit: 20);
            var handoff = Assert.Single(taskMessages, message =>
                message.Intent == MessageIntent.ReviewApproval &&
                message.Metadata.HasValue &&
                message.Metadata.Value.GetProperty("handoff_kind").GetString() == "merge_request");

            Assert.Equal("claude-code", handoff.Metadata!.Value.GetProperty("recipient").GetString());
            Assert.Contains("pick up your next task", handoff.Content, StringComparison.OrdinalIgnoreCase);

            var implementerDispatches = await dispatches.ListAsync(ProjectId, "claude-code", [DispatchStatus.Pending]);
            Assert.Empty(implementerDispatches);

            var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
            var streamEntries = await stream.ListAsync(new AgentStreamListOptions
            {
                ProjectId = ProjectId,
                TaskId = task.Id,
                StreamKind = AgentStreamKind.Ops,
                Limit = 20
            });

            Assert.Contains(streamEntries, entry => entry.EventType == "review_approved" && entry.RecipientAgent == "claude-code");
            Assert.Contains(streamEntries, entry => entry.EventType == "merge_handoff" && entry.RecipientAgent == "claude-code");
            Assert.DoesNotContain(streamEntries, entry => entry.EventType == "dispatch_created");
        }
    }

    private async Task<List<DispatchEntry>> ListDispatchesAsync(string? targetAgent = null, DispatchStatus[]? statuses = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        return await dispatches.ListAsync(ProjectId, targetAgent, statuses);
    }

    private async Task<List<AgentStreamEntry>> ListAgentStreamAsync(int? taskId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var stream = scope.ServiceProvider.GetRequiredService<IAgentStreamRepository>();
        return await stream.ListAsync(new AgentStreamListOptions
        {
            ProjectId = ProjectId,
            TaskId = taskId,
            StreamKind = AgentStreamKind.Ops,
            Limit = 50
        });
    }

    private async Task<ProjectTask> CreateTaskAsync(string title, string? assignedTo = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = title, AssignedTo = assignedTo });
    }

    private async Task<ReviewRound> CreateRoundAsync(int taskId, string headCommit = "bbb222")
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/597-review-packet-ux",
            base_branch = "main",
            base_commit = "aaa111",
            head_commit = headCommit
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReviewRound>(JsonOpts))!;
    }

    private async Task<ReviewFinding> CreateFindingAsync(int taskId, int roundId, string summary)
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}/review-rounds/{roundId}/findings", new
        {
            created_by = "codex",
            category = "blocking_bug",
            summary,
            notes = "Need a stable packet",
            file_references = new[] { "src/DenMcp.Server/Routes/TaskRoutes.cs:320" },
            test_commands = new[] { "dotnet test den-mcp.slnx --filter ReviewWorkflowApiTests" }
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ReviewFinding>(JsonOpts))!;
    }

    private async Task<Message> CreateMessageAsync(int taskId, string sender, string content)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        return await repo.CreateAsync(new Message
        {
            ProjectId = ProjectId,
            TaskId = taskId,
            Sender = sender,
            Content = content
        });
    }

    private async Task<ReviewPacketResult> CreateReviewRequestPacketAsync(int taskId, string requestedBy)
    {
        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();
        return await workflow.RequestReviewAsync(ProjectId, new RequestReviewInput
        {
            TaskId = taskId,
            RequestedBy = requestedBy,
            Branch = "task/658-post-review-automation",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });
    }

    private async Task<DispatchEntry> CreateApprovedReviewerDispatchAsync(int taskId, int triggerId, string reviewer)
    {
        using var scope = _factory.Services.CreateScope();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var (entry, _) = await dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = ProjectId,
            TargetAgent = reviewer,
            TriggerType = DispatchTriggerType.Message,
            TriggerId = triggerId,
            TaskId = taskId,
            Summary = "Review dispatch",
            ContextPrompt = "Review this task",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, triggerId, reviewer),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        return await dispatches.ApproveAsync(entry.Id, "signal-user");
    }

    private sealed class ReviewWorkflowAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-reviewworkflow-{Guid.NewGuid()}.db");

        public string DatabasePath => _dbPath;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["db-path"] = _dbPath,
                    ["llm-endpoint"] = "http://localhost/fake",
                    ["llm-api-key"] = "test-key",
                    ["llm-model"] = "fake"
                });
            });

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmClient>();
                services.AddSingleton<ILlmClient, FakeLlmClient>();
                services.RemoveAll<DbConnectionFactory>();
                services.AddSingleton(new DbConnectionFactory($"Data Source={_dbPath}"));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct = default)
            => Task.FromResult("{}");
    }
}
