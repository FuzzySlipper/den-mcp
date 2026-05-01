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

/// <summary>
/// Tests for optional reviewer identity audit enforcement via subagent_role and run_id parameters
/// on review mutation MCP tools.
/// </summary>
public class ReviewAuditEnforcementTests : IAsyncLifetime
{
    private ReviewAuditAppFactory _factory = null!;
    private const string ProjectId = "audit-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new ReviewAuditAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Audit Test" });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── Backwards compatibility: no validation when subagent_role omitted ───

    [Fact]
    public async Task CreateFinding_NoSubagentRole_BackwardsCompatible()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var json = await TaskTools.CreateReviewFinding(
            repo,
            round.Id,
            created_by: "pi",
            category: "blocking_bug",
            summary: "Should work without subagent_role",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("finding_key", out var key));
        Assert.NotNull(key.GetString());
        // Also verify verbose mode returns full data including summary
        var verboseJson = await TaskTools.CreateReviewFinding(
            repo,
            round.Id,
            created_by: "pi",
            category: "blocking_bug",
            summary: "Should work without subagent_role",
            verbose: true);
        using var verboseDoc = JsonDocument.Parse(verboseJson);
        Assert.Equal("Should work without subagent_role", verboseDoc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task SetVerdict_NoSubagentRole_BackwardsCompatible()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.SetReviewVerdict(
            workflow,
            round.Id,
            "looks_good",
            "pi",
            "Approved by orchestrator",
            verbose: false);

        Assert.Contains("looks_good", json);
    }

    [Fact]
    public async Task PostReviewFindings_NoSubagentRole_BackwardsCompatible()
    {
        var (task, round) = await CreateRoundAsync();
        await CreateFindingAsync(task.Id, round.Id, "Test finding");

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.PostReviewFindings(
            workflow,
            ProjectId, task.Id, round.Id,
            sender: "pi",
            notes: "Findings packet",
            verbose: false);

        // Verify the response is well-formed (backwards compatible: no validation error)
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("summary", out var summary));
        Assert.Contains("posted findings", summary.GetString());
    }

    // ─── Identity validation: accepted when convention is followed ─────────

    [Fact]
    public async Task CreateFinding_WithSubagentRole_ValidIdentityAccepted()
    {
        var (task, round) = await CreateRoundAsync();
        const string runId = "run-abc-123";

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var json = await TaskTools.CreateReviewFinding(
            repo,
            round.Id,
            created_by: "pi-reviewer",
            category: "blocking_bug",
            summary: "Valid reviewer finding",
            run_id: runId,
            subagent_role: "reviewer",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("finding_key", out var key));
        Assert.NotNull(key.GetString());
        // Verify full verbose record still includes the summary
        var verboseJson = await TaskTools.CreateReviewFinding(
            repo,
            round.Id,
            created_by: "pi-reviewer",
            category: "blocking_bug",
            summary: "Valid reviewer finding",
            run_id: runId,
            subagent_role: "reviewer",
            verbose: true);
        using var verboseDoc = JsonDocument.Parse(verboseJson);
        Assert.Equal("Valid reviewer finding", verboseDoc.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task SetVerdict_WithSubagentRole_ValidIdentityAccepted()
    {
        var (task, round) = await CreateRoundAsync();
        const string runId = "run-456-def";

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.SetReviewVerdict(
            workflow,
            round.Id,
            "changes_requested",
            "pi-reviewer",
            "Changes requested by reviewer",
            run_id: runId,
            subagent_role: "reviewer",
            verbose: false);

        Assert.Contains("changes_requested", json);
    }

    // ─── Identity validation: rejected when convention is broken ──────────

    [Fact]
    public async Task CreateFinding_WithSubagentRole_MismatchedIdentityRejected()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.CreateReviewFinding(
                repo,
                round.Id,
                created_by: "pi",
                category: "blocking_bug",
                summary: "Should be rejected",
                subagent_role: "reviewer"));

        Assert.Contains("pi-reviewer", ex.Message);
        Assert.Contains("created_by", ex.Message);
    }

    [Fact]
    public async Task SetVerdict_WithSubagentRole_MismatchedIdentityRejected()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.SetReviewVerdict(
                workflow,
                round.Id,
                "looks_good",
                "orchestrator",
                "Should be rejected",
                subagent_role: "reviewer"));

        Assert.Contains("-reviewer", ex.Message);
        Assert.Contains("decided_by", ex.Message);
    }

    [Fact]
    public async Task PostReviewFindings_WithSubagentRole_MismatchedIdentityRejected()
    {
        var (task, round) = await CreateRoundAsync();
        await CreateFindingAsync(task.Id, round.Id, "Test finding");

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.PostReviewFindings(
                workflow,
                ProjectId, task.Id, round.Id,
                sender: "pi",
                subagent_role: "reviewer"));

        Assert.Contains("pi-reviewer", ex.Message);
        Assert.Contains("sender", ex.Message);
    }

    [Fact]
    public async Task SetFindingStatus_WithSubagentRole_MismatchedIdentityRejected()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id, "Test finding");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.SetReviewFindingStatus(
                repo, taskRepo,
                finding.Id,
                status: "verified_fixed",
                updated_by: "pi",
                subagent_role: "reviewer"));

        Assert.Contains("pi-reviewer", ex.Message);
        Assert.Contains("updated_by", ex.Message);
    }

    [Fact]
    public async Task RespondToFinding_WithSubagentRole_MismatchedIdentityRejected()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id, "Test finding");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.RespondToReviewFinding(
                repo, taskRepo,
                finding.Id,
                responded_by: "pi",
                subagent_role: "reviewer"));

        Assert.Contains("pi-reviewer", ex.Message);
        Assert.Contains("responded_by", ex.Message);
    }

    // ─── Run ID traceability in message metadata ──────────────────────────

    [Fact]
    public async Task PostReviewFindings_WithRunId_IncludesInMessageMetadata()
    {
        var (task, round) = await CreateRoundAsync();
        await CreateFindingAsync(task.Id, round.Id, "Test finding");
        const string expectedRunId = "run-post-findings-test-789";

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.PostReviewFindings(
            workflow,
            ProjectId, task.Id, round.Id,
            sender: "pi-reviewer",
            subagent_role: "reviewer",
            run_id: expectedRunId,
            notes: "Findings with run ID in metadata",
            verbose: true);

        // Verify run_id is in the message metadata
        using var scope2 = _factory.Services.CreateScope();
        var messages = scope2.ServiceProvider.GetRequiredService<IMessageRepository>();
        var taskMessages = await messages.GetMessagesAsync(ProjectId, taskId: task.Id, limit: 10);
        var findingsMessage = taskMessages.FirstOrDefault(m =>
            m.Content.Contains("Findings with run ID in metadata"));

        Assert.NotNull(findingsMessage);
        Assert.NotNull(findingsMessage!.Metadata);

        var metaJson = JsonSerializer.Serialize(findingsMessage.Metadata);
        Assert.Contains(expectedRunId, metaJson);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private async Task<(ProjectTask Task, ReviewRound Round)> CreateRoundAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Audit test target" });

        var rounds = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        var round = await rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "test",
            Branch = "task/audit-enforcement-test",
            BaseBranch = "main",
            BaseCommit = "abc123",
            HeadCommit = "def456"
        });

        return (task, round);
    }

    private async Task<ReviewFinding> CreateFindingAsync(int taskId, int roundId, string summary = "Test finding")
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        return await repo.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = roundId,
            CreatedBy = "test",
            Category = ReviewFindingCategory.AcceptanceGap,
            Summary = summary
        });
    }

    private sealed class ReviewAuditAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-audit-{Guid.NewGuid()}.db");

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
