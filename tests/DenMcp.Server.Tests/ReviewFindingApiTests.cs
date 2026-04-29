using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Llm;
using DenMcp.Core.Models;
using DenMcp.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class ReviewFindingApiTests : IAsyncLifetime
{
    private ReviewFindingAppFactory _factory = null!;
    private HttpClient _client = null!;
    private const string ProjectId = "proj";
    private const string OtherProjectId = "other";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new ReviewFindingAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Test" });
        await projects.CreateAsync(new Project { Id = OtherProjectId, Name = "Other" });
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateReviewFinding_ReturnsStructuredFinding()
    {
        var (task, round) = await CreateRoundAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds/{round.Id}/findings", new
        {
            created_by = "codex",
            category = "blocking_bug",
            summary = "Wrong diff selected",
            notes = "Need stacked diff awareness",
            file_references = new[] { "src/DenMcp.Server/Routes/TaskRoutes.cs:120" },
            test_commands = new[] { "dotnet test den-mcp.slnx --filter ReviewFindingApiTests" }
        });
        response.EnsureSuccessStatusCode();

        var finding = await response.Content.ReadFromJsonAsync<ReviewFinding>(JsonOpts);
        Assert.Equal($"R{task.Id}-1", finding!.FindingKey);
        Assert.Equal(ReviewFindingCategory.BlockingBug, finding.Category);
        Assert.Equal(round.RoundNumber, finding.ReviewRoundNumber);
        Assert.Single(finding.FileReferences!);
    }

    [Fact]
    public async Task RespondToReviewFinding_RecordsResponseAndClaimedFixedStatus()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{finding.Id}/response", new
        {
            responded_by = "claude-code",
            response_notes = "Addressed on the branch",
            status = "claimed_fixed",
            status_notes = "Ready for rereview"
        });
        response.EnsureSuccessStatusCode();

        var updated = await response.Content.ReadFromJsonAsync<ReviewFinding>(JsonOpts);
        Assert.Equal(ReviewFindingStatus.ClaimedFixed, updated!.Status);
        Assert.Equal("claude-code", updated.ResponseBy);
        Assert.Equal("Addressed on the branch", updated.ResponseNotes);
    }

    [Fact]
    public async Task ListReviewFindings_CanFilterResolvedFindings()
    {
        var (task, round) = await CreateRoundAsync();
        var openFinding = await CreateFindingAsync(task.Id, round.Id, "Still open");
        var resolvedFinding = await CreateFindingAsync(task.Id, round.Id, "Moved to follow-up");
        var followUp = await CreateTaskAsync("Follow-up item");

        await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{resolvedFinding.Id}/status", new
        {
            status = "split_to_follow_up",
            updated_by = "codex",
            notes = "Tracked separately",
            follow_up_task_id = followUp.Id
        });

        var unresolved = await _client.GetAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings?resolved=false");
        unresolved.EnsureSuccessStatusCode();
        var unresolvedFindings = await unresolved.Content.ReadFromJsonAsync<List<ReviewFinding>>(JsonOpts);

        var resolved = await _client.GetAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings?resolved=true");
        resolved.EnsureSuccessStatusCode();
        var resolvedFindings = await resolved.Content.ReadFromJsonAsync<List<ReviewFinding>>(JsonOpts);

        Assert.NotNull(unresolvedFindings);
        Assert.NotNull(resolvedFindings);
        Assert.Single(unresolvedFindings);
        Assert.Equal(openFinding.Id, unresolvedFindings[0].Id);
        Assert.Single(resolvedFindings);
        Assert.Equal(resolvedFinding.Id, resolvedFindings[0].Id);
        Assert.Equal(followUp.Id, resolvedFindings[0].FollowUpTaskId);
    }

    [Fact]
    public async Task GetTaskDetail_GroupsOpenAndResolvedFindings()
    {
        var (task, round) = await CreateRoundAsync();
        await CreateFindingAsync(task.Id, round.Id, "Still open");
        var resolvedFinding = await CreateFindingAsync(task.Id, round.Id, "Verified");

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{resolvedFinding.Id}/status", new
        {
            status = "verified_fixed",
            updated_by = "codex",
            notes = "Confirmed in rereview"
        });
        response.EnsureSuccessStatusCode();

        var detailResponse = await _client.GetAsync($"/api/projects/{ProjectId}/tasks/{task.Id}");
        detailResponse.EnsureSuccessStatusCode();

        var detail = await detailResponse.Content.ReadFromJsonAsync<TaskDetail>(JsonOpts);
        Assert.Single(detail!.OpenReviewFindings);
        Assert.Single(detail.ResolvedReviewFindings);
        Assert.Equal("Still open", detail.OpenReviewFindings[0].Summary);
        Assert.Equal("Verified", detail.ResolvedReviewFindings[0].Summary);
    }

    [Fact]
    public async Task SetReviewFindingStatus_Returns400_WhenFollowUpTaskProvidedForNonSplitStatus()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);
        var followUp = await CreateTaskAsync("Follow-up item");

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{finding.Id}/status", new
        {
            status = "verified_fixed",
            updated_by = "codex",
            follow_up_task_id = followUp.Id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadAsStringAsync();
        Assert.Contains("follow_up_task_id", error);
    }

    [Fact]
    public async Task RespondToReviewFinding_Returns400_WhenFollowUpTaskIsInDifferentProject()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);
        var otherProjectTask = await CreateTaskAsync("Cross-project task", OtherProjectId);

        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-findings/{finding.Id}/response", new
        {
            responded_by = "claude-code",
            status = "split_to_follow_up",
            follow_up_task_id = otherProjectTask.Id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task McpRespondToReviewFinding_RejectsCrossProjectFollowUpTask()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);
        var otherProjectTask = await CreateTaskAsync("Cross-project task", OtherProjectId);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskTools.RespondToReviewFinding(
            findingRepo,
            taskRepo,
            finding.Id,
            "claude-code",
            status: "split_to_follow_up",
            follow_up_task_id: otherProjectTask.Id));

        Assert.Contains("same project", ex.Message);
    }

    [Fact]
    public async Task McpSetReviewFindingStatus_RejectsCrossProjectFollowUpTask()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);
        var otherProjectTask = await CreateTaskAsync("Cross-project task", OtherProjectId);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => TaskTools.SetReviewFindingStatus(
            findingRepo,
            taskRepo,
            finding.Id,
            "split_to_follow_up",
            "codex",
            follow_up_task_id: otherProjectTask.Id));

        Assert.Contains("same project", ex.Message);
    }

    private async Task<(ProjectTask Task, ReviewRound Round)> CreateRoundAsync()
    {
        var task = await CreateTaskAsync("Review target");
        var roundResponse = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{task.Id}/review-rounds", new
        {
            requested_by = "claude-code",
            branch = "task/595-structured-review-findings",
            base_branch = "main",
            base_commit = "abc123",
            head_commit = Guid.NewGuid().ToString("N")[..7]
        });
        roundResponse.EnsureSuccessStatusCode();

        var round = await roundResponse.Content.ReadFromJsonAsync<ReviewRound>(JsonOpts);
        return (task, round!);
    }

    private async Task<ProjectTask> CreateTaskAsync(string title, string projectId = ProjectId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await repo.CreateAsync(new ProjectTask { ProjectId = projectId, Title = title });
    }

    private async Task<ReviewFinding> CreateFindingAsync(int taskId, int roundId, string summary = "Structured finding")
    {
        var response = await _client.PostAsJsonAsync($"/api/projects/{ProjectId}/tasks/{taskId}/review-rounds/{roundId}/findings", new
        {
            created_by = "codex",
            category = "acceptance_gap",
            summary
        });
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<ReviewFinding>(JsonOpts))!;
    }

    private sealed class ReviewFindingAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-reviewfinding-{Guid.NewGuid()}.db");

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
