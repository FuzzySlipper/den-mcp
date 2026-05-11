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
using TaskStatus = DenMcp.Core.Models.TaskStatus;

namespace DenMcp.Server.Tests;

public class OrchestratorStateMachineToolsTests : IAsyncLifetime
{
    private StateMachineAppFactory _factory = null!;
    private const string ProjectId = "proj";

    public async Task InitializeAsync()
    {
        _factory = new StateMachineAppFactory();
        var initializer = new DatabaseInitializer(_factory.DatabasePath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Test" });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DetermineNextAction_NoImplementation_LaunchesCoder()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "No implementation yet");

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("launch_coder", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DetermineNextAction_NoImplementationButFailedWorkerRunsRespectRetryCap()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Coder keeps failing before packet");
        await CreateWorkerFailureAsync(scope, task.Id, "coder");
        await CreateWorkerFailureAsync(scope, task.Id, "coder");
        await CreateWorkerFailureAsync(scope, task.Id, "coder");

        var json = await DetermineAsync(scope, task.Id, maxAttempts: 3);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_implementation_retry_cap", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("attempts").GetProperty("coder").GetInt32());
    }

    [Fact]
    public async Task DetermineNextAction_CompleteImplementationWithoutHead_EscalatesFailClosed()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Missing head");
        await CreateCompletionAsync(scope, task.Id, "implementation_packet", "coder", "completed", branch: "task/missing-head", headCommit: null);

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("missing_repo_identity", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.True(doc.RootElement.GetProperty("fail_closed").GetBoolean());
    }

    [Fact]
    public async Task DetermineNextAction_ValidationWithoutEvidence_Escalates()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Missing validation evidence");
        await CreateCompletionAsync(scope, task.Id, "implementation_packet", "coder", "completed", branch: "task/validation-evidence", headCommit: "bbb222", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "validation_packet", "validator", "completed", branch: "task/validation-evidence", headCommit: "bbb222");

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("validation_evidence_missing", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.True(doc.RootElement.GetProperty("fail_closed").GetBoolean());
    }

    [Fact]
    public async Task DetermineNextAction_ReviewPacketWithoutStructuredVerdict_Escalates()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Needs real verdict");
        await CreateCompletionAsync(scope, task.Id, "implementation_packet", "coder", "completed", branch: "task/needs-verdict", headCommit: "bbb222", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "validation_packet", "validator", "completed", branch: "task/needs-verdict", headCommit: "bbb222", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "drift_check_packet", "drift_checker", "completed", branch: "task/needs-verdict", headCommit: "bbb222");
        await CreateCompletionAsync(scope, task.Id, "packet_audit_packet", "packet_auditor", "completed", branch: "task/needs-verdict", headCommit: "bbb222");
        var reviewRound = await CreateReviewRoundAsync(scope, task.Id, "task/needs-verdict", "bbb222");
        await CreateCompletionAsync(scope, task.Id, "review_findings_packet", "reviewer", "completed", branch: "task/needs-verdict", headCommit: "bbb222", reviewRoundId: reviewRound.Id);

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("escalate", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("review_verdict_missing", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
        Assert.True(doc.RootElement.GetProperty("fail_closed").GetBoolean());
    }

    [Fact]
    public async Task DetermineNextAction_LooksGoodWithMatchingValidation_ReturnsReady()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Ready");
        await CreateCompletionAsync(scope, task.Id, "implementation_packet", "coder", "completed", branch: "task/ready", headCommit: "ccc333", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "validation_packet", "validator", "completed", branch: "task/ready", headCommit: "ccc333", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "drift_check_packet", "drift_checker", "completed", branch: "task/ready", headCommit: "ccc333");
        await CreateCompletionAsync(scope, task.Id, "packet_audit_packet", "packet_auditor", "completed", branch: "task/ready", headCommit: "ccc333");
        var reviewRound = await CreateReviewRoundAsync(scope, task.Id, "task/ready", "ccc333");
        await CreateCompletionAsync(scope, task.Id, "review_findings_packet", "reviewer", "completed", branch: "task/ready", headCommit: "ccc333", reviewRoundId: reviewRound.Id);
        var rounds = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        await rounds.SetVerdictAsync(reviewRound.Id, ReviewVerdict.LooksGood, "reviewer");

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("ready_for_done_or_merge", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("looks_good_validated", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task DetermineNextAction_LooksGoodWithClaimedFixedFinding_ReturnsCoder()
    {
        using var scope = _factory.Services.CreateScope();
        var task = await CreateTaskAsync(scope, "Claimed fixed still unresolved");
        await CreateCompletionAsync(scope, task.Id, "implementation_packet", "coder", "completed", branch: "task/claimed-fixed", headCommit: "ddd444", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "validation_packet", "validator", "completed", branch: "task/claimed-fixed", headCommit: "ddd444", testsRun: new[] { "dotnet test" });
        await CreateCompletionAsync(scope, task.Id, "drift_check_packet", "drift_checker", "completed", branch: "task/claimed-fixed", headCommit: "ddd444");
        await CreateCompletionAsync(scope, task.Id, "packet_audit_packet", "packet_auditor", "completed", branch: "task/claimed-fixed", headCommit: "ddd444");
        var reviewRound = await CreateReviewRoundAsync(scope, task.Id, "task/claimed-fixed", "ddd444");
        await CreateCompletionAsync(scope, task.Id, "review_findings_packet", "reviewer", "completed", branch: "task/claimed-fixed", headCommit: "ddd444", reviewRoundId: reviewRound.Id);
        var findings = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var finding = await findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = reviewRound.Id,
            CreatedBy = "reviewer",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Still needs verification"
        });
        await findings.SetStatusAsync(finding.Id, new UpdateReviewFindingStatusInput
        {
            Status = ReviewFindingStatus.ClaimedFixed,
            UpdatedBy = "coder",
            Notes = "Claimed fixed but not verified"
        });
        var rounds = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        await rounds.SetVerdictAsync(reviewRound.Id, ReviewVerdict.LooksGood, "reviewer");

        var json = await DetermineAsync(scope, task.Id);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("launch_coder", doc.RootElement.GetProperty("decision").GetProperty("next_action").GetString());
        Assert.Equal("changes_requested", doc.RootElement.GetProperty("decision").GetProperty("reason").GetString());
    }

    private static async Task<string> DetermineAsync(IServiceScope scope, int taskId, int maxAttempts = 3)
    {
        return await OrchestratorStateMachineTools.DetermineOrchestratorNextAction(
            scope.ServiceProvider.GetRequiredService<ITaskRepository>(),
            scope.ServiceProvider.GetRequiredService<IMessageRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>(),
            scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>(),
            ProjectId,
            taskId,
            max_attempts: maxAttempts,
            verbose: true);
    }

    private static async Task<ProjectTask> CreateTaskAsync(IServiceScope scope, string title)
    {
        var tasks = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = title,
            Status = TaskStatus.InProgress,
            Description = "Task description"
        });
    }

    private static async Task<ReviewRound> CreateReviewRoundAsync(IServiceScope scope, int taskId, string branch, string headCommit)
    {
        var rounds = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        return await rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = taskId,
            RequestedBy = "runner",
            Branch = branch,
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = headCommit
        });
    }

    private static async Task CreateCompletionAsync(
        IServiceScope scope,
        int taskId,
        string packetType,
        string role,
        string status,
        string? branch = null,
        string? headCommit = null,
        string[]? testsRun = null,
        int? reviewRoundId = null)
    {
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = packetType,
            ["packet_kind"] = packetType,
            ["schema"] = "den_worker_completion",
            ["schema_version"] = 1,
            ["completion_packet"] = true,
            ["malformed"] = false,
            ["status"] = status,
            ["role"] = role,
            ["project_id"] = ProjectId,
            ["task_id"] = taskId,
            ["run_id"] = $"run-{packetType}-{role}-{Guid.NewGuid():N}",
            ["session_id"] = $"session-{Guid.NewGuid():N}",
            ["branch"] = branch,
            ["head_commit"] = headCommit,
            ["base_commit"] = "aaa111",
            ["tests_run"] = testsRun,
            ["review_round_id"] = reviewRoundId,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        await messages.CreateAsync(new Message
        {
            ProjectId = ProjectId,
            TaskId = taskId,
            Sender = role,
            Intent = packetType == "review_findings_packet" ? MessageIntent.ReviewFeedback : MessageIntent.StatusUpdate,
            Content = $"# {packetType}",
            Metadata = metadata
        });
    }

    private static async Task CreateWorkerFailureAsync(IServiceScope scope, int taskId, string role)
    {
        var messages = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var metadata = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "worker_failure_packet",
            ["packet_kind"] = "worker_failure_packet",
            ["role"] = role,
            ["project_id"] = ProjectId,
            ["task_id"] = taskId,
            ["run_id"] = $"run-failure-{role}-{Guid.NewGuid():N}",
            ["status"] = "failed",
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        await messages.CreateAsync(new Message
        {
            ProjectId = ProjectId,
            TaskId = taskId,
            Sender = role,
            Intent = MessageIntent.StatusUpdate,
            Content = "# worker_failure_packet",
            Metadata = metadata
        });
    }

    private sealed class StateMachineAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-state-machine-{Guid.NewGuid()}.db");

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
