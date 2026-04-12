using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
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

    private async Task<ProjectTask> CreateTaskAsync(string title)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = title });
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
