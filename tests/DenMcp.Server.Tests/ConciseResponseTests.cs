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
/// Regression tests ensuring MCP mutation tools return concise summaries by default
/// and full records only when verbose=true is requested.
/// </summary>
public class ConciseResponseTests : IAsyncLifetime
{
    private ConciseAppFactory _factory = null!;
    private const string ProjectId = "concise-test";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task InitializeAsync()
    {
        _factory = new ConciseAppFactory();
        using var scope = _factory.Services.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await projects.CreateAsync(new Project { Id = ProjectId, Name = "Concise Response Test" });
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ─── Task create ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTask_ConciseDefault_ReturnsSummaryWithIdAndStatus()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.CreateTask(
            repo, ProjectId, "Implement feature X",
            description: "A very long description that should not appear in concise output. ".PadRight(500, 'x'),
            priority: 2,
            tags: """["core","api"]""",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("summary", out var summary));
        var summaryText = summary.GetString()!;
        Assert.Contains("created task #", summaryText);
        Assert.Contains("planned", summaryText);

        Assert.True(root.TryGetProperty("id", out var id));
        Assert.True(id.GetInt32() > 0);
        Assert.True(root.TryGetProperty("status", out var status));
        Assert.Equal("planned", status.GetString());

        // Must NOT contain echoed description or title (absent properties)
        Assert.False(root.TryGetProperty("title", out _));
        Assert.False(root.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task CreateTask_ConciseDefault_IncludesParentIdWhenSubtask()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var parent = await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Parent" });

        var json = await TaskTools.CreateTask(
            repo, ProjectId, "Subtask Y",
            parent_id: parent.Id,
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"parent #{parent.Id}", summary);
        Assert.Equal(parent.Id, root.GetProperty("parent_id").GetInt32());
    }

    [Fact]
    public async Task CreateTask_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.CreateTask(
            repo, ProjectId, "Full record task",
            description: "Detailed acceptance criteria",
            priority: 1,
            tags: """["urgent"]""",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full ProjectTask, so title and description must be present
        Assert.Equal("Full record task", root.GetProperty("title").GetString());
        Assert.Equal("Detailed acceptance criteria", root.GetProperty("description").GetString());
        Assert.Equal(1, root.GetProperty("priority").GetInt32());
        Assert.True(root.TryGetProperty("id", out _));
    }

    // ─── Task update ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTask_ConciseDefault_ReturnsSummaryWithChangedFields()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();

        var task = await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Original title" });

        var json = await TaskTools.UpdateTask(
            repo, detection, logger,
            task.Id, "codex",
            status: "in_progress",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"updated task #{task.Id}", summary);
        Assert.Contains("status=in_progress", summary);

        Assert.Equal(task.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("in_progress", root.GetProperty("status").GetString());

        var changes = root.GetProperty("changes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("status", changes);

        // Must NOT contain the task title (absent property)
        Assert.False(root.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task UpdateTask_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();

        var task = await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Verbose task" });

        var json = await TaskTools.UpdateTask(
            repo, detection, logger,
            task.Id, "codex",
            title: "Updated verbose task",
            status: "review",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Updated verbose task", root.GetProperty("title").GetString());
        Assert.Equal("review", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateTask_ConciseDefault_ListsAllChangedFields()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();

        var task = await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Multi-change task" });

        var json = await TaskTools.UpdateTask(
            repo, detection, logger,
            task.Id, "codex",
            title: "New title",
            priority: 1,
            assigned_to: "claude-code",
            tags: """["urgent"]""",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var changes = doc.RootElement.GetProperty("changes").EnumerateArray().Select(e => e.GetString()).ToHashSet();

        Assert.Contains("title", changes);
        Assert.Contains("priority", changes);
        Assert.Contains("assigned_to", changes);
        Assert.Contains("tags", changes);
    }

    // ─── Create review finding ───────────────────────────────────────────

    [Fact]
    public async Task CreateReviewFinding_ConciseDefault_ReturnsSummaryWithKeyAndCategory()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var json = await TaskTools.CreateReviewFinding(
            repo,
            round.Id, "codex", "blocking_bug",
            "Wrong diff selected",
            notes: "Very detailed reviewer notes that should not be echoed. ".PadRight(500, 'x'),
            file_references: """["src/Foo.cs:42"]""",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        // This replaces the old raw-JSON DoesNotContain for the finding summary text
        Assert.StartsWith("created finding ", summary);
        Assert.Contains($"round #{round.Id}", summary);
        Assert.Contains("blocking_bug", summary);

        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("finding_key", out _));
        Assert.Equal(round.Id, root.GetProperty("review_round_id").GetInt32());
        Assert.Equal("blocking_bug", root.GetProperty("category").GetString());

        // Must NOT contain the full notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
        // (finding summary coverage: Assert.StartsWith above)
    }

    [Fact]
    public async Task CreateReviewFinding_VerboseTrue_ReturnsFullRecord()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        var json = await TaskTools.CreateReviewFinding(
            repo,
            round.Id, "codex", "acceptance_gap",
            "Missing error handling",
            notes: "Need try/catch around the call",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Missing error handling", root.GetProperty("summary").GetString());
        Assert.Equal("Need try/catch around the call", root.GetProperty("notes").GetString());
    }

    // ─── Set review finding status ───────────────────────────────────────

    [Fact]
    public async Task SetReviewFindingStatus_ConciseDefault_ReturnsSummaryWithStatus()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.SetReviewFindingStatus(
            findingRepo, taskRepo,
            finding.Id, "verified_fixed", "codex",
            notes: "Detailed verification notes",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("updated finding ", summary);
        Assert.Contains("status=verified_fixed", summary);

        Assert.Equal(finding.Id, root.GetProperty("id").GetInt32());
        Assert.True(root.TryGetProperty("finding_key", out _));
        Assert.Equal("verified_fixed", root.GetProperty("status").GetString());

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task SetReviewFindingStatus_VerboseTrue_ReturnsFullRecord()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.SetReviewFindingStatus(
            findingRepo, taskRepo,
            finding.Id, "claimed_fixed", "codex",
            notes: "Fixed in commit abc123",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("claimed_fixed", root.GetProperty("status").GetString());
        // Verbose returns full ReviewFinding
        Assert.True(root.TryGetProperty("summary", out _));
    }

    // ─── Respond to review finding ───────────────────────────────────────

    [Fact]
    public async Task RespondToReviewFinding_ConciseDefault_ReturnsSummaryWithStatus()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.RespondToReviewFinding(
            findingRepo, taskRepo,
            finding.Id, "claude-code",
            response_notes: "Addressed on the branch with a comprehensive refactor that should not appear",
            status: "claimed_fixed",
            status_notes: "Ready for rereview",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("responded to finding ", summary);
        Assert.Contains("status=claimed_fixed", summary);

        Assert.Equal(finding.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("claimed_fixed", root.GetProperty("status").GetString());
        Assert.Equal("claude-code", root.GetProperty("response_by").GetString());

        // Must NOT contain the response notes or status notes (absent properties)
        Assert.False(root.TryGetProperty("response_notes", out _));
        Assert.False(root.TryGetProperty("status_notes", out _));
    }

    [Fact]
    public async Task RespondToReviewFinding_VerboseTrue_ReturnsFullRecord()
    {
        var (task, round) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(task.Id, round.Id);

        using var scope = _factory.Services.CreateScope();
        var findingRepo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.RespondToReviewFinding(
            findingRepo, taskRepo,
            finding.Id, "claude-code",
            response_notes: "Fix details here",
            status: "claimed_fixed",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Fix details here", root.GetProperty("response_notes").GetString());
        Assert.Equal("claimed_fixed", root.GetProperty("status").GetString());
    }

    // ─── Create review round ──────────────────────────────────────────────

    [Fact]
    public async Task CreateReviewRound_ConciseDefault_ReturnsSummaryWithRoundNumber()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var roundRepo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Review target" });

        var json = await TaskTools.CreateReviewRound(
            roundRepo,
            task.Id, "codex",
            branch: "task/999-test",
            base_branch: "main",
            base_commit: "aaa111",
            head_commit: "bbb222",
            notes: "Detailed review notes that should not appear in concise output",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("created review round #", summary);
        Assert.Contains($"task #{task.Id}", summary);

        Assert.True(root.TryGetProperty("id", out var id));
        Assert.True(id.GetInt32() > 0);
        Assert.Equal(task.Id, root.GetProperty("task_id").GetInt32());
        Assert.Equal(1, root.GetProperty("round_number").GetInt32());
        Assert.Equal("task/999-test", root.GetProperty("branch").GetString());

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task CreateReviewRound_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var roundRepo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Verbose review" });

        var json = await TaskTools.CreateReviewRound(
            roundRepo,
            task.Id, "codex",
            branch: "task/999-test",
            base_branch: "main",
            base_commit: "aaa111",
            head_commit: "bbb222",
            notes: "Full notes visible",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full ReviewRound
        Assert.Equal("task/999-test", root.GetProperty("branch").GetString());
        Assert.Equal("main", root.GetProperty("base_branch").GetString());
        Assert.Equal("Full notes visible", root.GetProperty("notes").GetString());
        Assert.True(root.TryGetProperty("head_commit", out _));
        Assert.True(root.TryGetProperty("requested_at", out _));
    }

    // ─── Request review ───────────────────────────────────────────────────

    [Fact]
    public async Task RequestReview_ConciseDefault_ReturnsSummaryWithMessageId()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Review target" });

        var json = await TaskTools.RequestReview(
            workflow,
            ProjectId, task.Id, "codex",
            branch: "task/999-test",
            base_branch: "main",
            base_commit: "aaa111",
            head_commit: "bbb222",
            notes: "Long notes that should not appear in concise mode",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("requested review", summary);
        Assert.Contains($"task #{task.Id}", summary);

        Assert.True(root.TryGetProperty("review_round_id", out _));
        Assert.Equal(task.Id, root.GetProperty("task_id").GetInt32());
        Assert.Equal(1, root.GetProperty("round_number").GetInt32());
        Assert.True(root.TryGetProperty("message_id", out var msgId));
        Assert.True(msgId.GetInt32() > 0);

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task RequestReview_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Verbose request" });

        var json = await TaskTools.RequestReview(
            workflow,
            ProjectId, task.Id, "codex",
            branch: "task/999-test",
            base_branch: "main",
            base_commit: "aaa111",
            head_commit: "bbb222",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full ReviewPacketResult
        Assert.True(root.TryGetProperty("review_round", out _));
        Assert.True(root.TryGetProperty("message", out _));
        Assert.True(root.TryGetProperty("packet", out _));
    }

    [Fact]
    public async Task RequestReview_AcceptsStructuredTestRunObjects()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Structured tests request" });

        var json = await TaskTools.RequestReview(
            workflow,
            ProjectId, task.Id, "codex",
            branch: "task/999-test",
            base_branch: "main",
            base_commit: "aaa111",
            head_commit: "bbb222",
            tests_run: "[{\"command\":\"dotnet test --no-restore\",\"result\":\"passed\"}]",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var tests = doc.RootElement.GetProperty("review_round").GetProperty("tests_run");
        Assert.Equal("dotnet test --no-restore: passed", tests[0].GetString());
    }

    // ─── Post review findings ─────────────────────────────────────────────

    [Fact]
    public async Task PostReviewFindings_ConciseDefault_ReturnsSummaryWithMessageId()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var roundRepo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Findings target" });
        var round = await roundRepo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "codex",
            Branch = "task/999-test",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var json = await TaskTools.PostReviewFindings(
            workflow,
            ProjectId, task.Id, round.Id, "codex",
            notes: "Summary note that should not appear in concise output",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("posted findings", summary);
        Assert.Contains($"round #{round.Id}", summary);

        Assert.Equal(round.Id, root.GetProperty("review_round_id").GetInt32());
        Assert.Equal(task.Id, root.GetProperty("task_id").GetInt32());
        Assert.True(root.TryGetProperty("message_id", out var msgId));
        Assert.True(msgId.GetInt32() > 0);

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task PostReviewFindings_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var roundRepo = scope.ServiceProvider.GetRequiredService<IReviewRoundRepository>();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Verbose findings" });
        var round = await roundRepo.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "codex",
            Branch = "task/999-test",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var json = await TaskTools.PostReviewFindings(
            workflow,
            ProjectId, task.Id, round.Id, "codex",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full ReviewPacketResult
        Assert.True(root.TryGetProperty("review_round", out _));
        Assert.True(root.TryGetProperty("message", out _));
        Assert.True(root.TryGetProperty("packet", out _));
    }

    // ─── Send message ────────────────────────────────────────────────────

    [Fact]
    public async Task SendMessage_ConciseDefault_ReturnsSummaryWithId()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();
        var taskRepo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();
        var task = await taskRepo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Message host" });

        var json = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, "pi",
            "A long message body with implementation details that should not be echoed in concise mode",
            task_id: task.Id,
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("sent message #", summary);
        Assert.Contains($"on task #{task.Id}", summary);

        Assert.True(root.TryGetProperty("id", out _));
        Assert.Equal(ProjectId, root.GetProperty("project_id").GetString());
        Assert.Equal(task.Id, root.GetProperty("task_id").GetInt32());
        Assert.Equal("pi", root.GetProperty("sender").GetString());

        // Must NOT contain the message body (absent property)
        Assert.False(root.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task SendMessage_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageTools>>();

        var json = await MessageTools.SendMessage(
            repo, detection, logger,
            ProjectId, "pi",
            "Full message content",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Full message content", root.GetProperty("content").GetString());
        Assert.Equal("pi", root.GetProperty("sender").GetString());
    }

    // ─── Set review verdict ──────────────────────────────────────────────

    [Fact]
    public async Task SetReviewVerdict_ConciseDefault_ReturnsSummaryWithVerdict()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.SetReviewVerdict(
            workflow, round.Id, "changes_requested", "codex",
            notes: "Detailed feedback that should not appear",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"round #{round.Id}", summary);
        Assert.Contains("changes_requested", summary);

        Assert.Equal(round.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("changes_requested", root.GetProperty("verdict").GetString());
        Assert.Equal("codex", root.GetProperty("decided_by").GetString());

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task SetReviewVerdict_VerboseTrue_ReturnsFullRecord()
    {
        var (task, round) = await CreateRoundAsync();

        using var scope = _factory.Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IReviewWorkflowService>();

        var json = await TaskTools.SetReviewVerdict(
            workflow, round.Id, "looks_good", "codex",
            notes: "Approved with minor nit",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("looks_good", root.GetProperty("verdict").GetString());
        // Verbose returns full ReviewRound record
        Assert.True(root.TryGetProperty("round_number", out _));
        Assert.True(root.TryGetProperty("branch", out _));
    }

    // ─── Concise response structure invariants ───────────────────────────

    [Fact]
    public async Task ConciseResponse_AlwaysContainsSummaryAndId()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        // Test create
        var createJson = await TaskTools.CreateTask(repo, ProjectId, "Invariant check", verbose: false);
        using var createDoc = JsonDocument.Parse(createJson);
        Assert.True(createDoc.RootElement.TryGetProperty("summary", out _));
        Assert.True(createDoc.RootElement.TryGetProperty("id", out _));

        // Test update
        var detection = scope.ServiceProvider.GetRequiredService<IDispatchDetectionService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaskTools>>();
        var task = await repo.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Update target" });
        var updateJson = await TaskTools.UpdateTask(repo, detection, logger, task.Id, "codex", status: "done", verbose: false);
        using var updateDoc = JsonDocument.Parse(updateJson);
        Assert.True(updateDoc.RootElement.TryGetProperty("summary", out _));
        Assert.True(updateDoc.RootElement.TryGetProperty("id", out _));
        Assert.True(updateDoc.RootElement.TryGetProperty("changes", out _));
    }

    [Fact]
    public async Task ConciseResponse_SummaryDoesNotExceedReasonableLength()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITaskRepository>();

        var json = await TaskTools.CreateTask(
            repo, ProjectId,
            "A very long title that goes on and on with lots of detail about what the task is supposed to accomplish",
            description: "Even longer description ".PadRight(2000, 'd'),
            verbose: false);

        // Concise response should be short — well under 2000 chars even with long inputs
        Assert.True(json.Length < 500, $"Concise response too long: {json.Length} chars");
    }

    // ─── Create project ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_ConciseDefault_ReturnsSummaryWithId()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        var json = await ProjectTools.CreateProject(
            repo,
            "secondary-test-proj",
            "Secondary Test Project",
            description: "A very long project description that should not appear in concise output. ".PadRight(500, 'x'),
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("created project 'secondary-test-proj'", summary);

        Assert.Equal("secondary-test-proj", root.GetProperty("id").GetString());
        Assert.Equal("Secondary Test Project", root.GetProperty("name").GetString());

        // Must NOT contain the description (absent property)
        Assert.False(root.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task CreateProject_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        var json = await ProjectTools.CreateProject(
            repo,
            "verbose-test-proj",
            "Verbose Test Project",
            description: "Full description visible",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("verbose-test-proj", root.GetProperty("id").GetString());
        Assert.Equal("Verbose Test Project", root.GetProperty("name").GetString());
        Assert.Equal("Full description visible", root.GetProperty("description").GetString());
    }

    // ─── Space tools ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSpace_ConciseDefault_ReturnsSummaryWithIdAndKind()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        var json = await SpaceTools.CreateSpace(
            repo,
            "assistant-space-1",
            "Assistant Space 1",
            kind: "assistant",
            description: "A very long description that should not appear in concise output. ".PadRight(500, 'x'),
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("created space 'assistant-space-1'", summary);
        Assert.Contains("assistant", summary);

        Assert.Equal("assistant-space-1", root.GetProperty("id").GetString());
        Assert.Equal("Assistant Space 1", root.GetProperty("name").GetString());
        Assert.Equal("assistant", root.GetProperty("kind").GetString());

        // Must NOT contain the description (absent property)
        Assert.False(root.TryGetProperty("description", out _));
    }

    [Fact]
    public async Task CreateSpace_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        var json = await SpaceTools.CreateSpace(
            repo,
            "verbose-space-1",
            "Verbose Space",
            kind: "knowledge_base",
            description: "Full description visible",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("verbose-space-1", root.GetProperty("id").GetString());
        Assert.Equal("Verbose Space", root.GetProperty("name").GetString());
        Assert.Equal("knowledge_base", root.GetProperty("kind").GetString());
        Assert.Equal("Full description visible", root.GetProperty("description").GetString());
    }

    [Fact]
    public async Task ListSpaces_DefaultsToVisibleAllKinds()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = "proj-visible", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = "proj-hidden", Name = "Hidden Project", Visibility = "hidden" });
        await repo.CreateAsync(new Project { Id = "assistant-1", Name = "Assistant", Kind = "assistant" });

        var json = await SpaceTools.ListSpaces(repo);
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains("proj-visible", ids);
        Assert.Contains("assistant-1", ids);
        Assert.DoesNotContain("proj-hidden", ids);
    }

    [Fact]
    public async Task ListSpaces_KindFilter()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = "proj-visible", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = "assistant-1", Name = "Assistant", Kind = "assistant" });

        var json = await SpaceTools.ListSpaces(repo, kind: "assistant");
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains("assistant-1", ids);
        Assert.DoesNotContain("proj-visible", ids);
    }

    [Fact]
    public async Task ListProjects_DefaultsToProjectKindOnly()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();

        await repo.CreateAsync(new Project { Id = "proj-visible", Name = "Visible Project" });
        await repo.CreateAsync(new Project { Id = "assistant-1", Name = "Assistant", Kind = "assistant" });
        await repo.CreateAsync(new Project { Id = "personal-1", Name = "Personal", Kind = "personal" });
        await repo.CreateAsync(new Project { Id = "kb-1", Name = "Knowledge Base", Kind = "knowledge_base" });
        await repo.CreateAsync(new Project { Id = "system-1", Name = "System", Kind = "system" });

        var json = await ProjectTools.ListProjects(repo);
        using var doc = JsonDocument.Parse(json);
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToHashSet();

        Assert.Contains("proj-visible", ids);
        Assert.DoesNotContain("assistant-1", ids);
        Assert.DoesNotContain("personal-1", ids);
        Assert.DoesNotContain("kb-1", ids);
        Assert.DoesNotContain("system-1", ids);
    }

    [Fact]
    public async Task GetSpace_ReturnsStats()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        await repo.CreateAsync(new Project { Id = "space-get-test", Name = "Space Get Test", Kind = "assistant" });

        var json = await SpaceTools.GetSpace(repo, "space-get-test");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("space-get-test", root.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal("assistant", root.GetProperty("project").GetProperty("kind").GetString());
    }

    // ─── Store document ──────────────────────────────────────────────────

    [Fact]
    public async Task StoreDocument_ConciseDefault_ReturnsSummaryWithSlug()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var json = await DocumentTools.StoreDocument(
            repo,
            ProjectId,
            "test-spec",
            "Test Specification",
            content: "# Very Long Content\n\n".PadRight(2000, 'x'),
            doc_type: "spec",
            tags: """["core","api"]""",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("stored document", summary);
        Assert.Contains(ProjectId, summary);
        Assert.Contains("test-spec", summary);

        Assert.Equal(ProjectId, root.GetProperty("project_id").GetString());
        Assert.Equal("test-spec", root.GetProperty("slug").GetString());
        Assert.Equal("Test Specification", root.GetProperty("title").GetString());
        Assert.Equal("spec", root.GetProperty("doc_type").GetString());

        // Must NOT contain the content (absent property)
        Assert.False(root.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task StoreDocument_ConciseDefault_IncludesSummaryWhenProvided()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var json = await DocumentTools.StoreDocument(
            repo,
            ProjectId,
            "summary-spec",
            "Summary Spec",
            content: "Content",
            summary: "A concise summary",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("A concise summary", root.GetProperty("doc_summary").GetString());
    }

    [Fact]
    public async Task StoreDocument_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();

        var json = await DocumentTools.StoreDocument(
            repo,
            ProjectId,
            "verbose-spec",
            "Verbose Spec",
            content: "Full markdown content here",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Full markdown content here", root.GetProperty("content").GetString());
        Assert.Equal("verbose-spec", root.GetProperty("slug").GetString());
    }

    // ─── Add agent guidance entry ─────────────────────────────────────────

    [Fact]
    public async Task AddAgentGuidanceEntry_ConciseDefault_ReturnsSummaryWithId()
    {
        using var scope = _factory.Services.CreateScope();
        var docRepo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var guidanceRepo = scope.ServiceProvider.GetRequiredService<IAgentGuidanceRepository>();

        // Prerequisite: store a document for the guidance to reference
        await DocumentTools.StoreDocument(
            docRepo,
            ProjectId,
            "guidance-doc",
            "Guidance Doc",
            content: "# Guidance content",
            verbose: true);

        var json = await AgentGuidanceTools.AddAgentGuidanceEntry(
            guidanceRepo, docRepo,
            ProjectId,
            document_slug: "guidance-doc",
            importance: "required",
            audience: "pi,conductor",
            sort_order: 10,
            notes: "Detailed notes about why this guidance is important that should not appear in concise output",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("added guidance entry #", summary);
        Assert.Contains("guidance-doc", summary);
        Assert.Contains(ProjectId, summary);

        Assert.True(root.TryGetProperty("id", out var id));
        Assert.True(id.GetInt32() > 0);
        Assert.Equal(ProjectId, root.GetProperty("project_id").GetString());
        Assert.Equal("guidance-doc", root.GetProperty("document_slug").GetString());
        Assert.Equal("required", root.GetProperty("importance").GetString());

        // Must NOT contain the notes (absent property)
        Assert.False(root.TryGetProperty("notes", out _));
    }

    [Fact]
    public async Task AddAgentGuidanceEntry_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var docRepo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var guidanceRepo = scope.ServiceProvider.GetRequiredService<IAgentGuidanceRepository>();

        await DocumentTools.StoreDocument(
            docRepo,
            ProjectId,
            "verbose-guidance-doc",
            "Verbose Guidance Doc",
            content: "Content",
            verbose: true);

        var json = await AgentGuidanceTools.AddAgentGuidanceEntry(
            guidanceRepo, docRepo,
            ProjectId,
            document_slug: "verbose-guidance-doc",
            importance: "important",
            notes: "Full notes visible",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("verbose-guidance-doc", root.GetProperty("document_slug").GetString());
        Assert.Equal("Full notes visible", root.GetProperty("notes").GetString());
    }

    // ─── Store blackboard entry ───────────────────────────────────────────

    [Fact]
    public async Task StoreBlackboardEntry_ConciseDefault_ReturnsSummaryWithSlug()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBlackboardRepository>();

        var json = await BlackboardTools.StoreBlackboardEntry(
            repo,
            "test-blackboard-entry",
            "Test Entry",
            content: "# Very Long Blackboard Content\n\n".PadRight(2000, 'x'),
            tags: """["handoff","coordination"]""",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("stored blackboard entry 'test-blackboard-entry'", summary);

        Assert.Equal("test-blackboard-entry", root.GetProperty("slug").GetString());
        Assert.Equal("Test Entry", root.GetProperty("title").GetString());

        // Must NOT contain the content (absent property)
        Assert.False(root.TryGetProperty("content", out _));
    }

    [Fact]
    public async Task StoreBlackboardEntry_VerboseTrue_ReturnsFullRecord()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBlackboardRepository>();

        var json = await BlackboardTools.StoreBlackboardEntry(
            repo,
            "verbose-bb-entry",
            "Verbose Entry",
            content: "Full content here",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Full content here", root.GetProperty("content").GetString());
        Assert.Equal("verbose-bb-entry", root.GetProperty("slug").GetString());
    }

    // ─── Approve dispatch ─────────────────────────────────────────────────

    [Fact]
    public async Task ApproveDispatch_ConciseDefault_ReturnsSummaryWithId()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        var json = await DispatchTools.ApproveDispatch(
            repo, dispatch.Id, "test-user",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"approved dispatch #{dispatch.Id}", summary);
        Assert.Contains("test-agent", summary);

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("test-agent", root.GetProperty("target_agent").GetString());
        Assert.Equal("approved", root.GetProperty("status").GetString());

        // Must NOT contain context_prompt or context_json (absent properties)
        Assert.False(root.TryGetProperty("context_prompt", out _));
        Assert.False(root.TryGetProperty("context_json", out _));
    }

    [Fact]
    public async Task ApproveDispatch_VerboseTrue_ReturnsFullRecord()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        var json = await DispatchTools.ApproveDispatch(
            repo, dispatch.Id, "test-user",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("approved", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("decided_by", out _));
    }

    // ─── Reject dispatch ──────────────────────────────────────────────────

    [Fact]
    public async Task RejectDispatch_ConciseDefault_ReturnsSummaryWithId()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        var json = await DispatchTools.RejectDispatch(
            repo, dispatch.Id, "test-user",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"rejected dispatch #{dispatch.Id}", summary);

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("rejected", root.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RejectDispatch_VerboseTrue_ReturnsFullRecord()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        var json = await DispatchTools.RejectDispatch(
            repo, dispatch.Id, "test-user",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("rejected", root.GetProperty("status").GetString());
    }

    // ─── Complete dispatch ────────────────────────────────────────────────

    [Fact]
    public async Task CompleteDispatch_ConciseDefault_ReturnsSummaryWithId()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        // Must approve before completing
        await repo.ApproveAsync(dispatch.Id, "test-user");

        var json = await DispatchTools.CompleteDispatch(
            repo, dispatch.Id, "test-agent",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains($"completed dispatch #{dispatch.Id}", summary);

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("test-agent", root.GetProperty("completed_by").GetString());
    }

    [Fact]
    public async Task CompleteDispatch_VerboseTrue_ReturnsFullRecord()
    {
        var dispatch = await CreatePendingDispatchAsync();

        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();

        await repo.ApproveAsync(dispatch.Id, "test-user");

        var json = await DispatchTools.CompleteDispatch(
            repo, dispatch.Id, "test-agent",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(dispatch.Id, root.GetProperty("id").GetInt32());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.True(root.TryGetProperty("completed_by", out _));
    }

    // ─── Send agent stream message ───────────────────────────────────────

    [Fact]
    public async Task SendAgentStreamMessage_ConciseDefault_ReturnsSummaryWithId()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentStreamMessageService>();

        var json = await AgentStreamTools.SendAgentStreamMessage(
            service,
            sender: "user",
            event_type: "note",
            body: "A note body that should not appear in concise output",
            project_id: ProjectId,
            recipient_agent: "codex",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("sent agent stream message #", summary);
        Assert.Contains("note", summary);
        Assert.Contains("codex", summary);

        Assert.True(root.TryGetProperty("id", out _));
        Assert.Equal("note", root.GetProperty("event_type").GetString());
        Assert.Equal("codex", root.GetProperty("recipient_agent").GetString());

        // Must NOT contain the body (absent property)
        Assert.False(root.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task SendAgentStreamMessage_ConciseDefault_RecordOnly_OmitsWakeResolution()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentStreamMessageService>();

        var json = await AgentStreamTools.SendAgentStreamMessage(
            service,
            sender: "user",
            event_type: "note",
            body: "FYI note",
            project_id: ProjectId,
            recipient_agent: "codex",
            delivery_mode: "record_only",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Record-only messages have no wake resolution; the field is omitted when null
        Assert.False(root.TryGetProperty("wake_resolution_status", out _));
    }

    [Fact]
    public async Task SendAgentStreamMessage_ConciseDefault_WakeResolved_IncludesWakeResolutionStatus()
    {
        // Register an active binding so wake resolves to "resolved"
        using (var scope = _factory.Services.CreateScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "codex-concise-test-1",
                ProjectId = ProjectId,
                AgentIdentity = "codex",
                AgentFamily = "codex",
                Role = "implementer",
                TransportKind = "local_adapter",
                Status = AgentInstanceBindingStatus.Active
            });
        }

        using var serviceScope = _factory.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<IAgentStreamMessageService>();

        var json = await AgentStreamTools.SendAgentStreamMessage(
            service,
            sender: "user",
            event_type: "question",
            body: "Can you check this?",
            project_id: ProjectId,
            recipient_agent: "codex",
            delivery_mode: "wake",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var summary = root.GetProperty("summary").GetString()!;
        Assert.Contains("sent agent stream message #", summary);
        Assert.Contains("question", summary);

        Assert.True(root.TryGetProperty("id", out _));
        Assert.Equal("question", root.GetProperty("event_type").GetString());
        Assert.Equal("codex", root.GetProperty("recipient_agent").GetString());
        Assert.Equal("resolved", root.GetProperty("wake_resolution_status").GetString());

        // Must NOT contain the body (absent property)
        Assert.False(root.TryGetProperty("body", out _));
    }

    [Fact]
    public async Task SendAgentStreamMessage_ConciseDefault_WakeMissingBinding_IncludesWakeResolutionStatus()
    {
        using var serviceScope = _factory.Services.CreateScope();
        var service = serviceScope.ServiceProvider.GetRequiredService<IAgentStreamMessageService>();

        var json = await AgentStreamTools.SendAgentStreamMessage(
            service,
            sender: "user",
            event_type: "nudge",
            body: "Wake up",
            project_id: ProjectId,
            recipient_agent: "unknown-agent",
            delivery_mode: "wake",
            verbose: false);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("nudge", root.GetProperty("event_type").GetString());
        Assert.Equal("missing_binding", root.GetProperty("wake_resolution_status").GetString());
    }

    [Fact]
    public async Task SendAgentStreamMessage_VerboseTrue_ReturnsFullRecord()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var bindings = scope.ServiceProvider.GetRequiredService<IAgentInstanceBindingRepository>();
            await bindings.UpsertAsync(new AgentInstanceBinding
            {
                InstanceId = "codex-concise-verbose-1",
                ProjectId = ProjectId,
                AgentIdentity = "codex",
                AgentFamily = "codex",
                Role = "implementer",
                TransportKind = "local_adapter",
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
            project_id: ProjectId,
            recipient_agent: "codex",
            delivery_mode: "wake",
            verbose: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verbose returns full AgentStreamMessageCreateResult
        Assert.True(root.TryGetProperty("entry", out var entry));
        Assert.Equal("answer", entry.GetProperty("event_type").GetString());
        Assert.True(root.TryGetProperty("wake_resolution", out var wakeRes));
        Assert.Equal("resolved", wakeRes.GetProperty("status").GetString());
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

    private async Task<ReviewFinding> CreateFindingAsync(int taskId, int roundId)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IReviewFindingRepository>();

        return await repo.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = roundId,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Test finding"
        });
    }

    private int _dispatchTriggerCounter = 1000;

    private async Task<DispatchEntry> CreatePendingDispatchAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        var triggerId = Interlocked.Increment(ref _dispatchTriggerCounter);
        var (entry, _) = await repo.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = ProjectId,
            TargetAgent = "test-agent",
            TriggerType = DispatchTriggerType.TaskStatus,
            TriggerId = triggerId,
            Summary = $"Test dispatch {triggerId}",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.TaskStatus, triggerId, "test-agent"),
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        return entry;
    }

    // ─── Test app factory ────────────────────────────────────────────────

    private sealed class ConciseAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-concise-{Guid.NewGuid()}.db");

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
