using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class ReviewWorkflowServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _tasks = null!;
    private ReviewRoundRepository _rounds = null!;
    private ReviewFindingRepository _findings = null!;
    private MessageRepository _messages = null!;
    private ReviewWorkflowService _workflow = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _rounds = new ReviewRoundRepository(_testDb.Db);
        _findings = new ReviewFindingRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _workflow = new ReviewWorkflowService(_tasks, _rounds, _findings, _messages);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task RequestReviewAsync_BuildsRereviewPacketWithAddressedAndOpenFindings()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Review workflow" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/597-review-packet-ux",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var fixedFinding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Fix the review packet"
        });
        var openFinding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.AcceptanceGap,
            Summary = "Add a smoother rereview packet"
        });

        await _findings.RespondAsync(fixedFinding.Id, new RespondToReviewFindingInput
        {
            RespondedBy = "claude-code",
            ResponseNotes = "Fixed on branch",
            Status = ReviewFindingStatus.ClaimedFixed,
            StatusNotes = "Ready for rereview"
        });

        var result = await _workflow.RequestReviewAsync("proj", new RequestReviewInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/597-review-packet-ux",
            BaseBranch = "task/596-stacked-diff-metadata",
            BaseCommit = "ccc333",
            HeadCommit = "ddd444",
            CommitsSinceLastReview = 2,
            TestsRun = ["dotnet test den-mcp.slnx --filter ReviewWorkflowServiceTests"],
            PreferredDiffBaseRef = "task/596-stacked-diff-metadata",
            AlternateDiffBaseRef = "main",
            AlternateDiffBaseCommit = "aaa111",
            DeltaBaseCommit = "bbb222",
            InheritedCommitCount = 3,
            TaskLocalCommitCount = 2
        });

        Assert.Equal(2, result.ReviewRound!.RoundNumber);
        Assert.Equal(ReviewPacketKind.RereviewRequest, result.Packet.Kind);
        Assert.Contains("Ready for rereview", result.Packet.Content);
        Assert.Contains(fixedFinding.FindingKey, result.Packet.Content);
        Assert.Contains(openFinding.FindingKey, result.Packet.Content);
        Assert.Contains("Tests run by implementer:", result.Packet.Content);
        Assert.Equal(task.Id, result.Message.TaskId);
        Assert.Equal("claude-code", result.Message.Sender);
        Assert.Single(result.TestCommands);
    }

    [Fact]
    public async Task PostReviewFindingsAsync_BuildsStructuredPacketWithVerdictAndEvidence()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Findings packet" });
        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/597-review-packet-ux",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = round.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Wrong packet shape",
            Notes = "Need a stable rereview payload",
            FileReferences = ["src/DenMcp.Server/Routes/TaskRoutes.cs:300"],
            TestCommands = ["dotnet test den-mcp.slnx --filter ReviewWorkflowServiceTests"]
        });

        await _rounds.SetVerdictAsync(round.Id, ReviewVerdict.ChangesRequested, "codex", "Needs another pass");

        var result = await _workflow.PostReviewFindingsAsync("proj", new PostReviewFindingsInput
        {
            TaskId = task.Id,
            ReviewRoundId = round.Id,
            Sender = "codex",
            Notes = "Please address before merge"
        });

        Assert.Equal(ReviewPacketKind.ReviewFindings, result.Packet.Kind);
        Assert.Contains("Verdict: `changes_requested`", result.Packet.Content);
        Assert.Contains(finding.FindingKey, result.Packet.Content);
        Assert.Contains("Files: `src/DenMcp.Server/Routes/TaskRoutes.cs:300`", result.Packet.Content);
        Assert.Contains("Tests: `dotnet test den-mcp.slnx --filter ReviewWorkflowServiceTests`", result.Packet.Content);
        Assert.Contains("Please address before merge", result.Packet.Content);
        Assert.Single(result.TestCommands);
        Assert.Equal("codex", result.Message.Sender);
    }
}
