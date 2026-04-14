using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

public class ReviewWorkflowServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private TaskRepository _tasks = null!;
    private ReviewRoundRepository _rounds = null!;
    private ReviewFindingRepository _findings = null!;
    private MessageRepository _messages = null!;
    private DispatchRepository _dispatches = null!;
    private ReviewWorkflowService _workflow = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _rounds = new ReviewRoundRepository(_testDb.Db);
        _findings = new ReviewFindingRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _dispatches = new DispatchRepository(_testDb.Db);
        var docs = new DocumentRepository(_testDb.Db);
        var routing = new RoutingService(docs);
        var prompts = new PromptGenerationService(_tasks, _messages, routing);
        var detection = new DispatchDetectionService(
            routing,
            _dispatches,
            prompts,
            NoOpNotifications.Instance,
            NullLogger<DispatchDetectionService>.Instance);
        _workflow = new ReviewWorkflowService(
            _tasks,
            _rounds,
            _findings,
            _messages,
            _dispatches,
            detection,
            NullLogger<ReviewWorkflowService>.Instance);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private sealed class NoOpNotifications : INotificationChannel
    {
        public static NoOpNotifications Instance { get; } = new();

        public Task SendDispatchNotificationAsync(
            DispatchEntry dispatch,
            string summary,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendAgentStatusAsync(
            string projectId,
            string agent,
            string status,
            int? taskId = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StartListeningAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

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

    [Fact]
    public async Task SetReviewVerdictAsync_ChangesRequested_PostsFeedbackHandoffAndCompletesReviewerDispatch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Verdict automation",
            AssignedTo = "claude-code"
        });
        var reviewRequest = await _workflow.RequestReviewAsync("proj", new RequestReviewInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/658-post-review-automation",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var finding = await _findings.CreateAsync(new CreateReviewFindingInput
        {
            ReviewRoundId = reviewRequest.ReviewRound!.Id,
            CreatedBy = "codex",
            Category = ReviewFindingCategory.BlockingBug,
            Summary = "Need a post-verdict handoff"
        });

        var (reviewDispatch, _) = await _dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = "proj",
            TargetAgent = "codex",
            TriggerType = DispatchTriggerType.Message,
            TriggerId = reviewRequest.Message.Id,
            TaskId = task.Id,
            Summary = "Review request pending",
            ContextPrompt = "Review this task",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, reviewRequest.Message.Id, "codex"),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await _dispatches.ApproveAsync(reviewDispatch.Id, "signal-user");

        var result = await _workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ReviewRoundId = reviewRequest.ReviewRound.Id,
            Verdict = ReviewVerdict.ChangesRequested,
            DecidedBy = "codex",
            Notes = "Please add the automatic handoff"
        });

        Assert.Equal(ReviewVerdict.ChangesRequested, result.ReviewRound.Verdict);
        Assert.NotNull(result.HandoffMessage);
        Assert.Equal(reviewRequest.Message.Id, result.HandoffMessage!.ThreadId);
        Assert.Contains(finding.FindingKey, result.HandoffMessage.Content);
        Assert.Contains("request review again", result.HandoffMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Stop and ask for guidance", result.HandoffMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code TODOs", result.HandoffMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.HandoffMessage.Metadata.HasValue);
        Assert.Equal("review_feedback", result.HandoffMessage.Metadata!.Value.GetProperty("type").GetString());
        Assert.Equal("claude-code", result.HandoffMessage.Metadata!.Value.GetProperty("recipient").GetString());
        Assert.Single(result.CompletedDispatches);
        Assert.Equal(reviewDispatch.Id, result.CompletedDispatches[0].Id);

        var implementerDispatches = await _dispatches.ListAsync("proj", "claude-code", [DispatchStatus.Pending]);
        Assert.Single(implementerDispatches);
        Assert.Contains("review feedback", implementerDispatches[0].Summary!, StringComparison.OrdinalIgnoreCase);

        var completedReviewerDispatch = await _dispatches.GetByIdAsync(reviewDispatch.Id);
        Assert.Equal(DispatchStatus.Completed, completedReviewerDispatch!.Status);
    }

    [Fact]
    public async Task SetReviewVerdictAsync_LooksGood_PostsMergeHandoffUsingRequesterFallback()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Merge handoff" });
        var reviewRequest = await _workflow.RequestReviewAsync("proj", new RequestReviewInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/658-post-review-automation",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222",
            TestsRun = ["dotnet test den-mcp.slnx --filter ReviewWorkflowServiceTests"]
        });

        var result = await _workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ReviewRoundId = reviewRequest.ReviewRound!.Id,
            Verdict = ReviewVerdict.LooksGood,
            DecidedBy = "codex",
            Notes = "Approved for merge"
        });

        Assert.NotNull(result.HandoffMessage);
        Assert.Contains("Reviewed head SHA: `bbb222`", result.HandoffMessage!.Content);
        Assert.Contains("pick up your next task", result.HandoffMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request review again with the new head SHA and tests run", result.HandoffMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.HandoffMessage.Metadata.HasValue);
        Assert.Equal("merge_request", result.HandoffMessage.Metadata!.Value.GetProperty("type").GetString());
        Assert.Equal("claude-code", result.HandoffMessage.Metadata!.Value.GetProperty("recipient").GetString());

        var implementerDispatches = await _dispatches.ListAsync("proj", "claude-code", [DispatchStatus.Pending]);
        Assert.Single(implementerDispatches);
        Assert.Contains("Merge handoff", implementerDispatches[0].Summary!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mark the task done", implementerDispatches[0].ContextPrompt!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("request review again with the new head SHA and tests run", implementerDispatches[0].ContextPrompt!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetReviewVerdictAsync_WithoutMatchingRoundMessage_CreatesRootTaskHandoff()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Root handoff fallback",
            AssignedTo = "claude-code"
        });
        var unrelatedThread = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "codex",
            Content = "Unrelated task chatter"
        });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ThreadId = unrelatedThread.Id,
            Sender = "claude-code",
            Content = "Reply in the unrelated thread"
        });

        var round = await _rounds.CreateAsync(new CreateReviewRoundInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/658-post-review-automation",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var result = await _workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ReviewRoundId = round.Id,
            Verdict = ReviewVerdict.ChangesRequested,
            DecidedBy = "codex",
            Notes = "Needs one more pass"
        });

        Assert.NotNull(result.HandoffMessage);
        Assert.Null(result.HandoffMessage!.ThreadId);
    }

    [Fact]
    public async Task SetReviewVerdictAsync_SameVerdictTwice_ReusesExistingHandoff()
    {
        var task = await _tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Idempotent verdict",
            AssignedTo = "claude-code"
        });
        var reviewRequest = await _workflow.RequestReviewAsync("proj", new RequestReviewInput
        {
            TaskId = task.Id,
            RequestedBy = "claude-code",
            Branch = "task/658-post-review-automation",
            BaseBranch = "main",
            BaseCommit = "aaa111",
            HeadCommit = "bbb222"
        });

        var firstResult = await _workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ReviewRoundId = reviewRequest.ReviewRound!.Id,
            Verdict = ReviewVerdict.LooksGood,
            DecidedBy = "codex",
            Notes = "Approved for merge"
        });

        var secondResult = await _workflow.SetReviewVerdictAsync(new SetReviewVerdictInput
        {
            ProjectId = "proj",
            TaskId = task.Id,
            ReviewRoundId = reviewRequest.ReviewRound!.Id,
            Verdict = ReviewVerdict.LooksGood,
            DecidedBy = "codex",
            Notes = "Approved for merge"
        });

        Assert.NotNull(firstResult.HandoffMessage);
        Assert.NotNull(secondResult.HandoffMessage);
        Assert.Equal(firstResult.HandoffMessage!.Id, secondResult.HandoffMessage!.Id);

        var taskMessages = await _messages.GetMessagesAsync("proj", taskId: task.Id, limit: 20);
        Assert.Single(taskMessages, message =>
            message.Metadata.HasValue &&
            message.Metadata.Value.TryGetProperty("type", out var type) &&
            type.GetString() == "merge_request");

        var implementerDispatches = await _dispatches.ListAsync("proj", "claude-code", [DispatchStatus.Pending]);
        Assert.Single(implementerDispatches);
    }
}
