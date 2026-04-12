using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Services;

public class DispatchDetectionServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DispatchDetectionService _detection = null!;
    private DispatchRepository _dispatches = null!;
    private TaskRepository _tasks = null!;
    private MessageRepository _messages = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        _dispatches = new DispatchRepository(_testDb.Db);
        var docs = new DocumentRepository(_testDb.Db);
        var routing = new RoutingService(docs);
        var prompts = new PromptGenerationService(_tasks, _messages, routing);
        _detection = new DispatchDetectionService(routing, _dispatches, prompts,
            NullLogger<DispatchDetectionService>.Instance);

        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    #region Task status change dispatch

    [Fact]
    public async Task TaskMovedToReview_CreatesDispatchForReviewer()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Feature X" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("codex", pending[0].TargetAgent); // Default reviewer
        Assert.Equal(task.Id, pending[0].TaskId);
        Assert.Contains("Feature X", pending[0].Summary!);
        Assert.Contains("Feature X", pending[0].ContextPrompt!);
    }

    [Fact]
    public async Task TaskMovedFromReviewToPlanned_CreatesDispatchForImplementer()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Bug fix" });

        await _detection.OnTaskStatusChangedAsync(task, "review", "planned", "codex");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("claude-code", pending[0].TargetAgent); // Default implementer
        Assert.Contains("feedback", pending[0].Summary!);
    }

    [Fact]
    public async Task TaskMovedToDone_NoDispatch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Done task" });

        await _detection.OnTaskStatusChangedAsync(task, "review", "done", "codex");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // No trigger for review→done in default config
    }

    [Fact]
    public async Task SameAgentAsSender_NoDispatch()
    {
        // If codex sets a task to review and the reviewer is also codex,
        // don't dispatch to yourself
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Self-review" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "codex");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Codex is both sender and default reviewer
    }

    #endregion

    #region Message dispatch

    [Fact]
    public async Task MessageWithRecipient_CreatesDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Please review the routing config.",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Equal("claude-code", pending[0].TargetAgent);
        Assert.Contains("codex", pending[0].Summary!);
    }

    [Fact]
    public async Task MessageWithoutRecipient_NoDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "General comment, no recipient.",
            Metadata = JsonSerializer.Deserialize<JsonElement>("""{"type":"comment"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Default config requires has_recipient=true
    }

    [Fact]
    public async Task MessageToSelf_NoDispatch()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "claude-code",
            Content = "Note to self",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"note","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Empty(pending); // Don't dispatch to yourself
    }

    #endregion

    #region Dedup

    [Fact]
    public async Task DuplicateTaskEvent_SuppressedAtomically()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Dup test" });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");
        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending); // Second call deduped
    }

    [Fact]
    public async Task DuplicateMessageEvent_SuppressedAtomically()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Review this",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"review_feedback","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);
        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
    }

    #endregion

    #region Prompt content

    [Fact]
    public async Task DispatchPrompt_IncludesTaskMessages()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Prompt test" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "claude-code",
            Content = "Implementation complete, 5 new tests added."
        });

        await _detection.OnTaskStatusChangedAsync(task, "in_progress", "review", "claude-code");

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Contains("5 new tests", pending[0].ContextPrompt!);
    }

    [Fact]
    public async Task MessageDispatch_IncludesMessageContent()
    {
        var msg = await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            Sender = "codex",
            Content = "Here is the detailed plan for phase 2.",
            Metadata = JsonSerializer.Deserialize<JsonElement>(
                """{"type":"planning_summary","recipient":"claude-code"}""")
        });

        await _detection.OnMessageCreatedAsync(msg);

        var pending = await _dispatches.ListAsync("proj", statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);
        Assert.Contains("detailed plan for phase 2", pending[0].ContextPrompt!);
    }

    #endregion
}
