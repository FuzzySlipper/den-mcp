using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

public class ReviewFindingTriageServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _tasks = null!;
    private ReviewRoundRepository _rounds = null!;
    private ReviewFindingRepository _findings = null!;
    private ReviewFindingTriageService _triage = null!;
    private ProjectRepository _projects = null!;

    private const string ProjectId = "proj";

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _rounds = new ReviewRoundRepository(_testDb.Db);
        _findings = new ReviewFindingRepository(_testDb.Db);
        _projects = new ProjectRepository(_testDb.Db);
        _triage = new ReviewFindingTriageService(_tasks, _findings, NullLogger<ReviewFindingTriageService>.Instance);

        await _projects.CreateAsync(new Project { Id = ProjectId, Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task SplitFindings_CreatesFollowUpTaskWithDescription()
    {
        var (task, round) = await CreateRoundAsync();
        var f1 = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Missing edge case",
            notes: "Handle null input", fileRefs: ["src/Foo.cs:42"]);
        var f2 = await CreateFindingAsync(round.Id, ReviewFindingCategory.TestWeakness, "Flaky test",
            testCommands: ["dotnet test --filter F1"]);

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [f1.Id, f2.Id],
            SplitBy = "codex"
        });

        Assert.NotNull(result.FollowUpTask);
        Assert.Equal(2, result.UpdatedFindings.Count);
        Assert.Empty(result.SkippedFindingIds);

        // Verify follow-up task
        var followUp = result.FollowUpTask;
        Assert.Contains("Follow up", followUp.Title);
        Assert.Contains($"#{task.Id}", followUp.Title);
        Assert.NotNull(followUp.Description);
        Assert.Contains(f1.FindingKey, followUp.Description);
        Assert.Contains(f2.FindingKey, followUp.Description);
        Assert.Contains("Missing edge case", followUp.Description);
        Assert.Contains("Flaky test", followUp.Description);
        Assert.Contains("src/Foo.cs:42", followUp.Description);
        Assert.Contains("dotnet test --filter F1", followUp.Description);
        Assert.Contains("Acceptance criteria", followUp.Description);
    }

    [Fact]
    public async Task SplitFindings_UpdatesEachFindingStatusWithFollowUpTaskId()
    {
        var (task, round) = await CreateRoundAsync();
        var f1 = await CreateFindingAsync(round.Id, ReviewFindingCategory.FollowUpCandidate, "Minor cleanup");
        var f2 = await CreateFindingAsync(round.Id, ReviewFindingCategory.TestWeakness, "Missing coverage");

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [f1.Id, f2.Id],
            SplitBy = "codex"
        });

        Assert.All(result.UpdatedFindings, f =>
        {
            Assert.Equal(ReviewFindingStatus.SplitToFollowUp, f.Status);
            Assert.Equal(result.FollowUpTask.Id, f.FollowUpTaskId);
            Assert.Equal("codex", f.StatusUpdatedBy);
            Assert.Contains($"follow-up task #{result.FollowUpTask.Id}", f.StatusNotes);
        });
    }

    [Fact]
    public async Task SplitFindings_SkipsBlockingFindingsByDefault()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Critical issue");
        var nonBlocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Minor gap");

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [blocking.Id, nonBlocking.Id],
            SplitBy = "codex"
        });

        Assert.Single(result.UpdatedFindings);
        Assert.Equal(nonBlocking.Id, result.UpdatedFindings[0].Id);
        Assert.Single(result.SkippedFindingIds);
        Assert.Equal(blocking.Id, result.SkippedFindingIds[0]);
    }

    [Fact]
    public async Task SplitFindings_IncludesBlockingWhenOverrideIsSet()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Critical issue");
        var nonBlocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Minor gap");

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [blocking.Id, nonBlocking.Id],
            SplitBy = "codex",
            OverrideBlocking = true
        });

        Assert.Equal(2, result.UpdatedFindings.Count);
        Assert.Empty(result.SkippedFindingIds);
    }

    [Fact]
    public async Task SplitFindings_ThrowsWhenNoFindingsCanBeSplit()
    {
        var (task, round) = await CreateRoundAsync();
        var blocking = await CreateFindingAsync(round.Id, ReviewFindingCategory.BlockingBug, "Critical issue");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
            {
                TaskId = task.Id,
                ProjectId = ProjectId,
                FindingIds = [blocking.Id],
                SplitBy = "codex"
            }));

        Assert.Contains("blocking", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public async Task SplitFindings_ThrowsWhenFindingBelongsToDifferentTask()
    {
        var (task, round) = await CreateRoundAsync();
        var (otherTask, otherRound) = await CreateRoundAsync();
        var finding = await CreateFindingAsync(otherRound.Id, ReviewFindingCategory.AcceptanceGap, "Wrong task");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
            {
                TaskId = task.Id,
                ProjectId = ProjectId,
                FindingIds = [finding.Id],
                SplitBy = "codex"
            }));

        Assert.Contains("does not belong to task", ex.Message);
    }

    [Fact]
    public async Task SplitFindings_ThrowsWhenTaskNotFound()
    {
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
            {
                TaskId = 99999,
                ProjectId = ProjectId,
                FindingIds = [1],
                SplitBy = "codex"
            }));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task SplitFindings_ThrowsWhenProjectMismatch()
    {
        await _projects.CreateAsync(new Project { Id = "other", Name = "Other" });
        var (task, round) = await CreateRoundAsync();

        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
            {
                TaskId = task.Id,
                ProjectId = "other",
                FindingIds = [1],
                SplitBy = "codex"
            }));

        Assert.Contains("not found in project", ex.Message);
    }

    [Fact]
    public async Task SplitFindings_RespectsCustomTitlePriorityAndParent()
    {
        var (task, round) = await CreateRoundAsync();
        var parentTask = await _tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Epic parent" });
        var finding = await CreateFindingAsync(round.Id, ReviewFindingCategory.FollowUpCandidate, "Minor issue");

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [finding.Id],
            SplitBy = "codex",
            FollowUpTitle = "Custom follow-up title",
            FollowUpPriority = 1,
            FollowUpParentTaskId = parentTask.Id,
            FollowUpAssignedTo = "agent-1",
            FollowUpTags = ["follow-up", "review"]
        });

        Assert.Equal("Custom follow-up title", result.FollowUpTask.Title);
        Assert.Equal(1, result.FollowUpTask.Priority);
        Assert.Equal(parentTask.Id, result.FollowUpTask.ParentId);
        Assert.Equal("agent-1", result.FollowUpTask.AssignedTo);
        Assert.Equal(["follow-up", "review"], result.FollowUpTask.Tags);
    }

    [Fact]
    public async Task SplitFindings_DescriptionContainsFindingIdsAndCategory()
    {
        var (task, round) = await CreateRoundAsync();
        var f = await CreateFindingAsync(round.Id, ReviewFindingCategory.AcceptanceGap, "Need X");

        var result = await _triage.SplitFindingsToFollowUpAsync(new SplitFindingsToFollowUpInput
        {
            TaskId = task.Id,
            ProjectId = ProjectId,
            FindingIds = [f.Id],
            SplitBy = "codex"
        });

        var desc = result.FollowUpTask.Description!;
        Assert.Contains($"**Finding ID**: {f.Id}", desc);
        Assert.Contains("acceptance_gap", desc);
        Assert.Contains($"**Review round**: {f.ReviewRoundNumber}", desc);
    }

    private async Task<(ProjectTask Task, ReviewRound Round)> CreateRoundAsync()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = ProjectId, Title = "Review target" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
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
        string summary,
        string? notes = null,
        List<string>? fileRefs = null,
        List<string>? testCommands = null)
    {
        return await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = roundId,
            CreatedBy = "codex",
            Category = category,
            Summary = summary,
            Notes = notes,
            FileReferences = fileRefs,
            TestCommands = testCommands
        });
    }
}
