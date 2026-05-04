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

/// <summary>
/// Tests for the compact task workflow summary read (get_task_workflow_summary).
/// Verifies that output omits full message bodies and finding notes while preserving
/// identity fields, metadata, and review workflow state.
/// </summary>
public class TaskWorkflowSummaryTests : IAsyncLifetime
{
    private SummaryAppFactory _factory = null!;
    private const string ProjectId = "summary-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new SummaryAppFactory();
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Summary Test" });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── Core structure tests ────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_ReturnsTaskIdentityFields()
    {
        var task = await CreateTaskAsync("Test task", priority: 2, assignedTo: "pi",
            tags: ["workflow", "test"]);

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(task.Id, root.GetProperty("id").GetInt32());
        Assert.Equal(ProjectId, root.GetProperty("project_id").GetString());
        Assert.Equal("Test task", root.GetProperty("title").GetString());
        Assert.Equal("planned", root.GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("priority").GetInt32());
        Assert.Equal("pi", root.GetProperty("assigned_to").GetString());
        Assert.Equal("workflow", root.GetProperty("tags").EnumerateArray().First().GetString());
    }

    [Fact]
    public async Task WorkflowSummary_OmitsDescription()
    {
        var task = await CreateTaskAsync("Task with long description",
            description: "A very long description that should not appear in compact output. ".PadRight(500, 'x'));

        var json = await GetSummaryJsonAsync(task.Id);

        Assert.DoesNotContain("very long description", json);
        Assert.DoesNotContain("should not appear", json);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("description", out _),
            "Compact summary must not include description field");
    }

    [Fact]
    public async Task WorkflowSummary_ContainsDeepReadHint()
    {
        var task = await CreateTaskAsync("Hint task");
        var json = await GetSummaryJsonAsync(task.Id);

        using var doc = JsonDocument.Parse(json);
        var hint = doc.RootElement.GetProperty("deep_read_hint").GetString()!;
        Assert.Contains("get_task", hint);
        Assert.Contains(task.Id.ToString(), hint);
    }

    // ─── Dependencies ───────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IncludesDependencies()
    {
        var dep1 = await CreateTaskAsync("Dep task 1");
        var dep2 = await CreateTaskAsync("Dep task 2");
        var task = await CreateTaskAsync("Main task", dependsOn: [dep1.Id, dep2.Id]);

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var deps = doc.RootElement.GetProperty("dependencies").EnumerateArray().ToList();

        Assert.Equal(2, deps.Count);
        Assert.Contains(deps, d => d.GetProperty("title").GetString() == "Dep task 1");
        Assert.Contains(deps, d => d.GetProperty("title").GetString() == "Dep task 2");
    }

    // ─── Subtasks ───────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IncludesCompactSubtasks()
    {
        var parent = await CreateTaskAsync("Parent task");
        await CreateTaskAsync("Sub A", parentId: parent.Id, priority: 1);
        await CreateTaskAsync("Sub B", parentId: parent.Id, priority: 3);

        var json = await GetSummaryJsonAsync(parent.Id);
        using var doc = JsonDocument.Parse(json);
        var subs = doc.RootElement.GetProperty("subtasks").EnumerateArray().ToList();

        Assert.Equal(2, subs.Count);
        // Each subtask should have id, title, status, priority but NOT description
        foreach (var sub in subs)
        {
            Assert.True(sub.TryGetProperty("id", out _));
            Assert.True(sub.TryGetProperty("title", out _));
            Assert.True(sub.TryGetProperty("status", out _));
            Assert.True(sub.TryGetProperty("priority", out _));
            Assert.False(sub.TryGetProperty("description", out _),
                "Compact subtask must not include description");
        }
    }

    // ─── Message headers ────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IncludesMessageHeadersWithoutBodies()
    {
        var task = await CreateTaskAsync("Message host");
        await SendMessageAsync(task.Id, "pi",
            "# Implementation Packet\n\nThis is a very long implementation report body with lots of details that should NOT appear in compact output. ".PadRight(2000, 'x'),
            intent: "handoff",
            metadata: """{"type":"implementation_packet","branch":"task/42-test","head_commit":"abc123def456"}""");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("recent_messages").EnumerateArray().ToList();

        Assert.Single(messages);
        var msg = messages[0];

        // Header fields present
        Assert.True(msg.TryGetProperty("id", out var msgId));
        Assert.True(msgId.GetInt32() > 0);
        Assert.Equal("pi", msg.GetProperty("sender").GetString());
        Assert.Equal("handoff", msg.GetProperty("intent").GetString());
        Assert.Equal("implementation_packet", msg.GetProperty("metadata_type").GetString());
        Assert.Equal("task/42-test", msg.GetProperty("metadata_branch").GetString());
        Assert.Equal("abc123def456", msg.GetProperty("metadata_head_commit").GetString());

        // First line is present but truncated
        var firstLine = msg.GetProperty("first_line").GetString()!;
        Assert.Contains("Implementation Packet", firstLine);
        Assert.True(firstLine.Length <= 120, $"First line too long: {firstLine.Length}");

        // Body is NOT present
        Assert.False(msg.TryGetProperty("content", out _),
            "Compact message header must not include content field");
        Assert.DoesNotContain("very long implementation report", json);
        Assert.DoesNotContain("should NOT appear", json);
    }

    [Fact]
    public async Task WorkflowSummary_MultipleMessages_ReturnsAllHeaders()
    {
        var task = await CreateTaskAsync("Multi message");
        await SendMessageAsync(task.Id, "pi", "First message body", intent: "handoff",
            metadata: """{"type":"coder_context_packet"}""");
        await SendMessageAsync(task.Id, "codex", "Second message body", intent: "review_feedback",
            metadata: """{"type":"review_findings_packet","review_round_id":"5"}""");
        await SendMessageAsync(task.Id, "user", "Third message body", intent: "general");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var messages = doc.RootElement.GetProperty("recent_messages").EnumerateArray().ToList();

        // Returns up to 10, most recent first
        Assert.Equal(3, messages.Count);

        // Verify metadata extraction
        var secondMsg = messages[1]; // second most recent
        Assert.Equal("review_findings_packet", secondMsg.GetProperty("metadata_type").GetString());
        Assert.Equal("5", secondMsg.GetProperty("metadata_review_round_id").GetString());

        // No content field on any
        foreach (var msg in messages)
            Assert.False(msg.TryGetProperty("content", out _));
    }

    // ─── Review workflow ────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IncludesCompactReviewWorkflow()
    {
        var task = await CreateTaskAsync("Review task");
        var round = await CreateReviewRoundAsync(task.Id, "task/99-review");
        await CreateFindingAsync(task.Id, round.Id, "blocking_bug", "Bug in logic");
        await CreateFindingAsync(task.Id, round.Id, "acceptance_gap", "Missing edge case");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var workflow = doc.RootElement.GetProperty("review_workflow");

        Assert.Equal(1, workflow.GetProperty("review_round_count").GetInt32());
        Assert.Equal(2, workflow.GetProperty("unresolved_finding_count").GetInt32());
        Assert.Equal(0, workflow.GetProperty("resolved_finding_count").GetInt32());

        // Current round ref
        var currentRound = workflow.GetProperty("current_round");
        Assert.Equal(round.Id, currentRound.GetProperty("review_round_id").GetInt32());
        Assert.Equal(1, currentRound.GetProperty("review_round_number").GetInt32());
        Assert.Equal("task/99-review", currentRound.GetProperty("branch").GetString());

        // Timeline
        var timeline = workflow.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Single(timeline);
        Assert.Equal(2, timeline[0].GetProperty("total_finding_count").GetInt32());
        Assert.Equal(2, timeline[0].GetProperty("open_finding_count").GetInt32());
    }

    [Fact]
    public async Task WorkflowSummary_ReviewVerdict_WhenSet()
    {
        var task = await CreateTaskAsync("Verdict task");
        var round = await CreateReviewRoundAsync(task.Id, "task/100-verdict");

        await SetVerdictAsync(round.Id, "changes_requested", "codex");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var workflow = doc.RootElement.GetProperty("review_workflow");

        Assert.Equal("changes_requested", workflow.GetProperty("current_verdict").GetString());

        var currentRound = workflow.GetProperty("current_round");
        Assert.Equal("changes_requested", currentRound.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task WorkflowSummary_MultipleReviewRounds()
    {
        var task = await CreateTaskAsync("Multi-round task");
        var round1 = await CreateReviewRoundAsync(task.Id, "task/101-r1", headCommit: "rr1head");
        var round2 = await CreateReviewRoundAsync(task.Id, "task/101-r2", headCommit: "rr2head");

        await CreateFindingAsync(task.Id, round1.Id, "blocking_bug", "R1 bug");
        await CreateFindingAsync(task.Id, round1.Id, "acceptance_gap", "R1 gap");
        await CreateFindingAsync(task.Id, round2.Id, "test_weakness", "R2 test");

        // Resolve one finding from round 1
        var findings = await ListFindingsAsync(task.Id);
        var r1Bug = findings.First(f => f.Summary == "R1 bug");
        await SetFindingStatusAsync(r1Bug.Id, "verified_fixed", "codex");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var workflow = doc.RootElement.GetProperty("review_workflow");

        Assert.Equal(2, workflow.GetProperty("review_round_count").GetInt32());
        // 1 verified (round1) + 1 open (round1) + 1 open (round2) = 2 unresolved, 1 resolved
        Assert.Equal(2, workflow.GetProperty("unresolved_finding_count").GetInt32());
        Assert.Equal(1, workflow.GetProperty("resolved_finding_count").GetInt32());

        var timeline = workflow.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(2, timeline.Count);
    }

    // ─── Unresolved findings ────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IncludesUnresolvedFindingsCompact()
    {
        var task = await CreateTaskAsync("Findings task");
        var round = await CreateReviewRoundAsync(task.Id, "task/102-findings");

        var finding1 = await CreateFindingAsync(task.Id, round.Id, "blocking_bug",
            "Critical bug in calculation",
            notes: "Very detailed notes about the bug that should not appear in compact output".PadRight(500, 'n'));
        var finding2 = await CreateFindingAsync(task.Id, round.Id, "test_weakness",
            "Missing test coverage");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var unresolved = doc.RootElement.GetProperty("unresolved_findings").EnumerateArray().ToList();

        Assert.Equal(2, unresolved.Count);

        // Check first finding has key fields
        var f1 = unresolved.First(f => f.GetProperty("summary").GetString() == "Critical bug in calculation");
        Assert.True(f1.TryGetProperty("id", out _));
        Assert.True(f1.TryGetProperty("finding_key", out _));
        Assert.Equal("blocking_bug", f1.GetProperty("category").GetString());
        Assert.Equal("open", f1.GetProperty("status").GetString());
        Assert.Equal(round.Id, f1.GetProperty("review_round_id").GetInt32());

        // Must NOT include notes
        Assert.False(f1.TryGetProperty("notes", out _),
            "Compact finding must not include notes");
        Assert.DoesNotContain("Very detailed notes", json);
        Assert.DoesNotContain("should not appear in compact output", json);
    }

    [Fact]
    public async Task WorkflowSummary_OmitsResolvedFindings()
    {
        var task = await CreateTaskAsync("Resolved findings task");
        var round = await CreateReviewRoundAsync(task.Id, "task/103-resolved");

        await CreateFindingAsync(task.Id, round.Id, "blocking_bug", "Fixed bug");
        await CreateFindingAsync(task.Id, round.Id, "acceptance_gap", "Open gap");

        var findings = await ListFindingsAsync(task.Id);
        var fixedFinding = findings.First(f => f.Summary == "Fixed bug");
        await SetFindingStatusAsync(fixedFinding.Id, "verified_fixed", "codex");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var unresolved = doc.RootElement.GetProperty("unresolved_findings").EnumerateArray().ToList();

        // Only the open gap should be in unresolved
        Assert.Single(unresolved);
        Assert.Equal("Open gap", unresolved[0].GetProperty("summary").GetString());
    }

    // ─── Size comparison ────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_IsMuchSmallerThanFullDetail()
    {
        // Create a task with rich data: messages, review rounds, findings
        var task = await CreateTaskAsync("Size comparison task",
            description: "A detailed description ".PadRight(1000, 'd'));
        var round = await CreateReviewRoundAsync(task.Id, "task/200-size");

        await CreateFindingAsync(task.Id, round.Id, "blocking_bug", "Bug one",
            notes: "Detailed reviewer notes ".PadRight(500, 'n'));
        await CreateFindingAsync(task.Id, round.Id, "acceptance_gap", "Gap two",
            notes: "More detailed notes ".PadRight(500, 'n'));

        await SendMessageAsync(task.Id, "pi",
            "# Coder Context Packet\n\n## Task\n- Task: `#42`\n\n".PadRight(2000, 'p'),
            intent: "handoff",
            metadata: """{"type":"coder_context_packet","branch":"task/200-size"}""");
        await SendMessageAsync(task.Id, "codex",
            "# Review Findings Packet\n\n".PadRight(1500, 'r'),
            intent: "review_feedback",
            metadata: """{"type":"review_findings_packet"}""");

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var fullJson = await TaskTools.GetTask(repo, task.Id);
        var compactJson = await TaskTools.GetTaskWorkflowSummary(repo, task.Id);

        // Compact should be significantly smaller
        Assert.True(compactJson.Length < fullJson.Length / 2,
            $"Compact ({compactJson.Length} chars) should be less than half of full ({fullJson.Length} chars)");

        // Compact should NOT contain the long padded strings
        Assert.DoesNotContain(new string('d', 50), compactJson);
        Assert.DoesNotContain(new string('n', 50), compactJson);
        Assert.DoesNotContain(new string('p', 50), compactJson);
        Assert.DoesNotContain(new string('r', 50), compactJson);
    }

    [Fact]
    public async Task WorkflowSummary_TaskNotFound_ThrowsKeyNotFoundException()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => TaskTools.GetTaskWorkflowSummary(repo, 999999));
    }

    // ─── Edge cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task WorkflowSummary_NoReviewRounds_NoFindings_NoMessages()
    {
        var task = await CreateTaskAsync("Minimal task");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Structure is valid
        Assert.Equal(task.Id, root.GetProperty("id").GetInt32());
        Assert.Empty(root.GetProperty("dependencies").EnumerateArray().ToList());
        Assert.Empty(root.GetProperty("subtasks").EnumerateArray().ToList());
        Assert.Empty(root.GetProperty("recent_messages").EnumerateArray().ToList());
        Assert.Empty(root.GetProperty("unresolved_findings").EnumerateArray().ToList());

        var workflow = root.GetProperty("review_workflow");
        Assert.Equal(0, workflow.GetProperty("review_round_count").GetInt32());
        Assert.Equal(0, workflow.GetProperty("unresolved_finding_count").GetInt32());
    }

    [Fact]
    public async Task WorkflowSummary_FirstLineTruncation()
    {
        var task = await CreateTaskAsync("Truncation task");
        var longFirstLine = new string('A', 200);
        await SendMessageAsync(task.Id, "pi", longFirstLine + "\nSecond line content", intent: "general");

        var json = await GetSummaryJsonAsync(task.Id);
        using var doc = JsonDocument.Parse(json);
        var msg = doc.RootElement.GetProperty("recent_messages")[0];

        var firstLine = msg.GetProperty("first_line").GetString()!;
        Assert.True(firstLine.Length <= 120, $"First line should be truncated: {firstLine.Length}");
        Assert.DoesNotContain("Second line", firstLine);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private async Task<ProjectTask> CreateTaskAsync(string title, string? description = null,
        int priority = 3, string? assignedTo = null, List<string>? tags = null,
        int[]? dependsOn = null, int? parentId = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await repo.CreateAsync(new ProjectTask
        {
            ProjectId = ProjectId,
            Title = title,
            Description = description,
            Priority = priority,
            AssignedTo = assignedTo,
            Tags = tags,
            ParentId = parentId
        }, dependsOn);
    }

    private async Task<ReviewRound> CreateReviewRoundAsync(int taskId, string branch, string? headCommit = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        return await repo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = taskId,
            RequestedBy = "codex",
            Branch = branch,
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = headCommit ?? "bbb222"
        });
    }

    private async Task<ReviewFinding> CreateFindingAsync(int taskId, int roundId, string category,
        string summary, string? notes = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        return await repo.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = roundId,
            CreatedBy = "codex",
            Category = EnumExtensions.ParseReviewFindingCategory(category),
            Summary = summary,
            Notes = notes
        });
    }

    private async Task<Message> SendMessageAsync(int taskId, string sender, string content,
        string? intent = null, string? metadata = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();
        var json = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, sender, content,
            task_id: taskId,
            intent: intent,
            metadata: metadata is not null ? JsonSerializer.Deserialize<JsonElement>(metadata) : null,
            verbose: true);
        return JsonSerializer.Deserialize<Message>(json, JsonOpts)!;
    }

    private async Task SetVerdictAsync(int roundId, string verdict, string decidedBy)
    {
        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();
        await workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ReviewRoundId = roundId,
            Verdict = EnumExtensions.ParseReviewVerdict(verdict),
            DecidedBy = decidedBy
        });
    }

    private async Task<List<ReviewFinding>> ListFindingsAsync(int taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        return await repo.ListByTaskAsync(taskId);
    }

    private async Task SetFindingStatusAsync(int findingId, string status, string updatedBy)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        await repo.SetStatusAsync(findingId, new UpdateReviewFindingStatusInput
        {
            Status = EnumExtensions.ParseReviewFindingStatus(status),
            UpdatedBy = updatedBy
        });
    }

    private async Task<string> GetSummaryJsonAsync(int taskId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        return await TaskTools.GetTaskWorkflowSummary(repo, taskId);
    }

    // ─── Test app factory ────────────────────────────────────────────────

    private sealed class SummaryAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-summary-{Guid.NewGuid()}.db");

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
