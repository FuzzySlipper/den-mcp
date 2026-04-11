using DenMcp.Core.Data;
using DenMcp.Core.Models;
using DenMcp.Core.Services;

namespace DenMcp.Core.Tests.Services;

public class PromptGenerationServiceTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private PromptGenerationService _service = null!;
    private TaskRepository _tasks = null!;
    private MessageRepository _messages = null!;
    private RoutingService _routing = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _tasks = new TaskRepository(_testDb.Db);
        _messages = new MessageRepository(_testDb.Db);
        var docs = new DocumentRepository(_testDb.Db);
        _routing = new RoutingService(docs);
        _service = new PromptGenerationService(_tasks, _messages, _routing);

        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test Project" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private static RoutingConfig DefaultConfig => RoutingService.CreateDefaultConfig();

    #region Review prompt

    [Fact]
    public async Task ReviewPrompt_IncludesTaskAndBranch()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Add dispatch API" });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            FromStatus = "in_progress",
            TaskId = task.Id,
            TaskTitle = task.Title,
            Sender = "claude-code",
            Branch = "task/557-dispatch-api"
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("task/557-dispatch-api", result.ContextPrompt);
        Assert.Contains("Add dispatch API", result.ContextPrompt);
        Assert.Contains("reviewer", result.ContextPrompt);
        Assert.Contains("git diff main...HEAD", result.ContextPrompt);
        Assert.Contains($"#{task.Id}", result.Summary);
    }

    [Fact]
    public async Task ReviewPrompt_IncludesRecentMessages()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Feature X" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "claude-code",
            Content = "Ready for review — added 5 new tests."
        });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            TaskId = task.Id,
            TaskTitle = task.Title,
            Sender = "claude-code"
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("Ready for review", result.ContextPrompt);
        Assert.Contains("claude-code", result.ContextPrompt);
    }

    #endregion

    #region Feedback prompt

    [Fact]
    public async Task FeedbackPrompt_IncludesReviewContext()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Fix the bug" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "codex",
            Content = "Two issues found: missing null check and no test for edge case."
        });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "planned",
            FromStatus = "review",
            TaskId = task.Id,
            TaskTitle = task.Title,
            Sender = "codex"
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("review feedback", result.ContextPrompt);
        Assert.Contains("implementer", result.ContextPrompt);
        Assert.Contains("missing null check", result.ContextPrompt);
        Assert.Contains("Review feedback", result.Summary);
    }

    #endregion

    #region Message prompt

    [Fact]
    public async Task MessagePrompt_IncludesTaskContext()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Routing rules" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "codex",
            Content = "Please address the validation gap."
        });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            TaskId = task.Id,
            Recipient = "claude-code",
            Sender = "codex",
            MessageType = "review_feedback",
            MessageId = 1
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("Routing rules", result.ContextPrompt);
        Assert.Contains("validation gap", result.ContextPrompt);
        Assert.Contains("codex", result.Summary);
    }

    [Fact]
    public async Task MessagePrompt_WithoutTask_StillWorks()
    {
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.MessageReceived,
            ProjectId = "proj",
            Recipient = "claude-code",
            Sender = "codex",
            MessageType = "planning_summary",
            MessageId = 1
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("proj", result.ContextPrompt);
        Assert.Contains("codex", result.ContextPrompt);
        Assert.DoesNotContain("Task #", result.ContextPrompt); // No task context
    }

    #endregion

    #region Custom template

    [Fact]
    public async Task CustomTemplate_OverridesOpener()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Custom work" });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            TaskId = task.Id,
            TaskTitle = task.Title,
            Sender = "claude-code"
        };

        var config = DefaultConfig;
        var trigger = new RoutingTrigger
        {
            Event = DispatchEvent.TaskStatusChanged,
            ToStatus = "review",
            DispatchTo = "reviewer",
            PromptTemplate = "CUSTOM: Please review {task_title} in {project_id}."
        };

        var result = await _service.GenerateAsync(evt, trigger, config);

        // Custom opener used instead of default
        Assert.StartsWith("CUSTOM: Please review Custom work in proj.", result.ContextPrompt);
        // But still includes the structured context
        Assert.Contains("reviewer", result.ContextPrompt);
        Assert.Contains("git diff main...HEAD", result.ContextPrompt);
    }

    #endregion

    #region Missing context fallbacks

    [Fact]
    public async Task ReviewPrompt_MissingBranch_FallsBackToConvention()
    {
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            TaskId = 42,
            TaskTitle = "Some task",
            Branch = null // No explicit branch
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("task/42-*", result.ContextPrompt);
    }

    [Fact]
    public async Task ReviewPrompt_NoMessages_StillGenerates()
    {
        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            TaskId = 999, // No messages on this task
            TaskTitle = "Empty task"
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("Empty task", result.ContextPrompt);
        Assert.DoesNotContain("Recent messages", result.ContextPrompt);
    }

    [Fact]
    public async Task FallbackPrompt_UnknownEventKind()
    {
        var evt = new DispatchEvent
        {
            EventKind = "unknown_event",
            ProjectId = "proj"
        };

        var trigger = new RoutingTrigger
        {
            Event = "unknown_event",
            DispatchTo = "some-agent"
        };

        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("proj", result.ContextPrompt);
        Assert.Equal("Dispatch on proj", result.Summary);
    }

    #endregion

    #region Long message truncation

    [Fact]
    public async Task ReviewPrompt_TruncatesLongMessages()
    {
        var task = await _tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Long msg test" });
        await _messages.CreateAsync(new Message
        {
            ProjectId = "proj",
            TaskId = task.Id,
            Sender = "codex",
            Content = new string('x', 2000) // Very long message
        });

        var evt = new DispatchEvent
        {
            EventKind = DispatchEvent.TaskStatusChanged,
            ProjectId = "proj",
            ToStatus = "review",
            TaskId = task.Id,
            TaskTitle = task.Title
        };

        var trigger = _routing.MatchTrigger(DefaultConfig, evt)!;
        var result = await _service.GenerateAsync(evt, trigger, DefaultConfig);

        Assert.Contains("(truncated)", result.ContextPrompt);
        // Prompt should be significantly shorter than the 2000-char message
        Assert.True(result.ContextPrompt.Length < 1800);
    }

    #endregion
}
