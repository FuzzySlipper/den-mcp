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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Server.Tests;

public class SplitReviewFindingsTests : IAsyncLifetime
{
    private SplitAppFactory _factory = null!;
    private const string ProjectId = "split-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new SplitAppFactory();
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Split Test" });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── MCP tool: concise response ──────────────────────────────────────

    [Fact]
    public async Task SplitReviewFindings_ConciseDefault_ReturnsSummaryWithIds()
    {
        var (task, round) = await CreateRoundAsync();
        var f1 = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Gap 1");
        var f2 = await CreateFindingAsync(round.Id, ReviewFindingCategory.TestWeakness, "Weak test");

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var json = await TaskTools.SplitReviewFindingsToFollowUp(
            triage,
            ProjectId, task.Id,
            finding_ids: JsonSerializer.Serialize(new[] { f1.Id, f2.Id }),
            split_by: "codex",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("split 2 findings", summary);
        Assert.Contains("follow-up task #", summary);

        Assert.True(root.TryGetProperty("follow_up_task_id", out var followUpId));
        Assert.True(followUpId.GetInt32() > 0);
        Assert.Equal(2, root.GetProperty("split_count").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped_count").GetInt32());

        var findingIds = root.GetProperty("finding_ids").EnumerateArray().Select(e => e.GetInt32()).ToList();
        Assert.Equal(2, findingIds.Count);
        Assert.Contains(f1.Id, findingIds);
        Assert.Contains(f2.Id, findingIds);

        // Must NOT contain full description or finding summaries
        Assert.DoesNotContain("Gap 1", json);
        Assert.DoesNotContain("Weak test", json);
    }

    [Fact]
    public async Task SplitReviewFindings_Concise_IncludesSkippedCount()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Bad bug");
        var nonBlocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.FollowUpCandidate, "Minor");

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var json = await TaskTools.SplitReviewFindingsToFollowUp(
            triage,
            ProjectId, task.Id,
            finding_ids: JsonSerializer.Serialize(new[] { blocking.Id, nonBlocking.Id }),
            split_by: "codex",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("1 skipped", summary);
        Assert.Equal(1, root.GetProperty("split_count").GetInt32());
        Assert.Equal(1, root.GetProperty("skipped_count").GetInt32());
    }

    [Fact]
    public async Task SplitReviewFindings_VerboseTrue_ReturnsFullResult()
    {
        var (task, round) = await CreateRoundAsync();
        var f = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Detail finding");

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var json = await TaskTools.SplitReviewFindingsToFollowUp(
            triage,
            ProjectId, task.Id,
            finding_ids: JsonSerializer.Serialize(new[] { f.Id }),
            split_by: "codex",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full result with follow-up task and updated findings
        Assert.True(root.TryGetProperty("follow_up_task", out _));
        Assert.True(root.TryGetProperty("updated_findings", out var findingsArr));
        Assert.Equal(1, findingsArr.GetArrayLength());
    }

    [Fact]
    public async Task SplitReviewFindings_Concise_ResponseLengthIsReasonable()
    {
        var (task, round) = await CreateRoundAsync();
        var findings = new List<ReviewFinding>();
        for (int i = 0; i < 5; i++)
            findings.Add(await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, $"Finding {i}"));

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var json = await TaskTools.SplitReviewFindingsToFollowUp(
            triage,
            ProjectId, task.Id,
            finding_ids: JsonSerializer.Serialize(findings.Select(f => f.Id).ToArray()),
            split_by: "codex",
            verbose: false);

        // Concise response should be short
        Assert.True(json.Length < 500, $"Concise response too long: {json.Length} chars");
    }

    // ─── Error handling ──────────────────────────────────────────────────

    [Fact]
    public async Task SplitReviewFindings_ThrowsOnEmptyFindingIds()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            TaskTools.SplitReviewFindingsToFollowUp(
                triage,
                ProjectId, task.Id,
                finding_ids: "[]",
                split_by: "codex"));
    }

    [Fact]
    public async Task SplitReviewFindings_ThrowsWhenAllBlocking()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Critical");

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TaskTools.SplitReviewFindingsToFollowUp(
                triage,
                ProjectId, task.Id,
                finding_ids: JsonSerializer.Serialize(new[] { blocking.Id }),
                split_by: "codex"));

        Assert.Contains("blocking", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task SplitReviewFindings_OverrideBlocking_IncludesBlockingFindings()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Critical");

        using var scope = _factory.Services.CreateScope();
        var triage = scope.ServiceProvider.GetRequiredService<IReviewFindingTriageService>();

        var json = await TaskTools.SplitReviewFindingsToFollowUp(
            triage,
            ProjectId, task.Id,
            finding_ids: JsonSerializer.Serialize(new[] { blocking.Id }),
            split_by: "codex",
            override_blocking: true,
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("split_count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("skipped_count").GetInt32());
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private async Task<(ProjectTask Task, ReviewRound Round)> CreateRoundAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var roundRepo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Review target" });
        var round = await roundRepo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "codex",
            Branch = $"task/{task.Id}-test",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });
        return (task, round);
    }

    private async Task<ReviewFinding> CreateFindingAsync(
        int roundId,
        ReviewFindingCategory category,
        string summary)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        return await repo.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = roundId,
            CreatedBy = "codex",
            Category = category,
            Summary = summary
        });
    }

    private sealed class SplitAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-split-{Guid.NewGuid()}.db");

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
                    NullLogger<DatabaseInitializer>.Instance);
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
                => Task.FromResult("{}");
        }
    }
}
