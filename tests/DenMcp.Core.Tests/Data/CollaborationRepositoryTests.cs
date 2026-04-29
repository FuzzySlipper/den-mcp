using System.Text.Json;
using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public sealed class CollaborationRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private CollaborationRepository _repo = null!;
    private ProjectTask _task = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new CollaborationRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask { ProjectId = "proj", Title = "Collaboration task" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateSession_PersistsImmutableSourceSnapshotAndDeterministicSegments()
    {
        using var context = JsonDocument.Parse("""{"thread_id":2614,"source":"task_thread"}""");
        var rawMarkdown = """
            # Plan

            This is a paragraph.

            - first
            - second

            > quoted
            > reply

            ```csharp
            Console.WriteLine("hi");
            ```
            """;

        var session = await _repo.CreateSessionAsync(new CreateCollaborationSessionRequestModel
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            PiRunId = "run-1",
            PiSessionId = "pi-session-1",
            DesktopOperatorSessionId = "operator-1",
            Title = "Annotate plan",
            CreatedBy = "tester",
            InitialTurn = new CreateCollaborationTurnRequestModel
            {
                Role = "assistant",
                SourceKind = "den_message",
                SourceRef = "2614",
                SourceLabel = "agent response",
                SourceUri = "den://messages/2614",
                SourceContext = context.RootElement.Clone(),
                RawMarkdown = rawMarkdown
            }
        });

        Assert.Equal("proj", session.ProjectId);
        Assert.Equal(_task.Id, session.TaskId);
        Assert.Equal("run-1", session.PiRunId);
        Assert.Equal("pi-session-1", session.PiSessionId);
        Assert.Equal("operator-1", session.DesktopOperatorSessionId);
        var turn = Assert.Single(session.Turns);
        Assert.Equal(rawMarkdown, turn.RawMarkdown);
        Assert.Equal(CollaborationRepository.SegmenterVersion, turn.SegmenterVersion);
        Assert.Equal(64, turn.SourceContentHash.Length);
        Assert.Equal("task_thread", turn.SourceContext!.Value.GetProperty("source").GetString());

        Assert.Collection(turn.Segments,
            heading =>
            {
                Assert.Equal(1, heading.SequenceNumber);
                Assert.Equal(CollaborationSegmentType.Heading, heading.SegmentType);
                Assert.Equal(1, heading.HeadingLevel);
                Assert.Equal("Plan", heading.Text);
            },
            paragraph => Assert.Equal(CollaborationSegmentType.Paragraph, paragraph.SegmentType),
            list => Assert.Equal(CollaborationSegmentType.List, list.SegmentType),
            quote => Assert.Equal(CollaborationSegmentType.BlockQuote, quote.SegmentType),
            code =>
            {
                Assert.Equal(CollaborationSegmentType.CodeBlock, code.SegmentType);
                Assert.Equal("csharp", code.CodeLanguage);
            });

        Assert.All(turn.Segments, segment => Assert.Equal(64, segment.SegmentHash.Length));
        Assert.Equal(turn.Segments.Count, turn.Segments.Select(s => s.SegmentHash).Distinct().Count());

        var same = await _repo.AddTurnAsync("proj", session.Id, new CreateCollaborationTurnRequestModel
        {
            Role = "assistant",
            RawMarkdown = rawMarkdown
        });
        Assert.Equal(2, same.TurnOrder);
        Assert.Equal(turn.Segments.Select(s => s.SegmentHash), same.Segments.Select(s => s.SegmentHash));
    }

    [Fact]
    public async Task AnnotationAndDraftUpdates_UseExpectedRevisionConflicts()
    {
        var session = await NewSessionAsync();
        var turn = Assert.Single(session.Turns);
        var segment = turn.Segments[0];

        var annotation = await _repo.CreateAnnotationAsync(
            "proj",
            session.Id,
            turn.Id,
            segment.Id,
            CollaborationAnnotationType.Note,
            "please clarify",
            "operator");

        Assert.Equal(1, annotation.Revision);
        Assert.Equal(segment.SegmentHash, annotation.SegmentHash);

        var updated = await _repo.UpdateAnnotationAsync(
            "proj",
            session.Id,
            annotation.Id,
            expectedRevision: 1,
            CollaborationAnnotationType.Flag,
            "discuss before proceeding",
            "operator");
        Assert.Equal(2, updated.Revision);
        Assert.Equal(CollaborationAnnotationType.Flag, updated.AnnotationType);

        await Assert.ThrowsAsync<CollaborationConflictException>(() => _repo.UpdateAnnotationAsync(
            "proj",
            session.Id,
            annotation.Id,
            expectedRevision: 1,
            CollaborationAnnotationType.Done,
            "stale update",
            "pi"));

        var draft = await _repo.CreateDraftAsync("proj", session.Id, turn.Id, "> segment\n  [note]: please clarify", "operator");
        Assert.Equal(1, draft.Revision);

        var draftUpdated = await _repo.UpdateDraftAsync("proj", session.Id, draft.Id, 1, "compiled response v2", "operator");
        Assert.Equal(2, draftUpdated.Revision);

        await Assert.ThrowsAsync<CollaborationConflictException>(() => _repo.UpdateDraftAsync(
            "proj",
            session.Id,
            draft.Id,
            expectedRevision: 1,
            content: "stale draft",
            updatedBy: "pi"));

        var reloaded = await _repo.GetSessionAsync("proj", session.Id);
        Assert.NotNull(reloaded);
        Assert.Equal("## Reply\n\nParagraph to annotate.", Assert.Single(reloaded!.Turns).RawMarkdown);
        Assert.Equal(Assert.Single(reloaded.Turns).Segments[0].SegmentHash, Assert.Single(reloaded.Annotations).SegmentHash);
        Assert.Equal("compiled response v2", Assert.Single(reloaded.Drafts).Content);
    }

    private Task<CollaborationSession> NewSessionAsync() => _repo.CreateSessionAsync(new CreateCollaborationSessionRequestModel
    {
        ProjectId = "proj",
        TaskId = _task.Id,
        Title = "Annotate reply",
        CreatedBy = "tester",
        InitialTurn = new CreateCollaborationTurnRequestModel
        {
            Role = "assistant",
            SourceKind = "markdown",
            RawMarkdown = "## Reply\n\nParagraph to annotate."
        }
    });
}
