using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class DesktopSnapshotRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DateTime _now = new(2026, 4, 27, 12, 0, 0, DateTimeKind.Utc);
    private DesktopSnapshotRepository _snapshots = null!;
    private ProjectTask _task = null!;
    private AgentWorkspace _workspace = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _snapshots = new DesktopSnapshotRepository(_testDb.Db, () => _now);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Project", RootPath = "/not/local" });

        var tasks = new TaskRepository(_testDb.Db);
        _task = await tasks.CreateAsync(new ProjectTask
        {
            ProjectId = "proj",
            Title = "Desktop snapshot host"
        });

        var workspaces = new AgentWorkspaceRepository(_testDb.Db);
        _workspace = await workspaces.UpsertAsync(new AgentWorkspace
        {
            Id = "ws-1",
            ProjectId = "proj",
            TaskId = _task.Id,
            Branch = "task/desktop",
            WorktreePath = "/home/patch/dev/proj-worktree",
            BaseBranch = "main"
        });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task UpsertGitSnapshot_CreatesAndUpdatesLatestSnapshotForSameSourceScope()
    {
        var observed = _now.AddSeconds(-15);
        var created = await _snapshots.UpsertGitSnapshotAsync(NewGitSnapshot(observed));

        Assert.Equal("proj", created.ProjectId);
        Assert.Equal(_task.Id, created.TaskId);
        Assert.Equal("ws-1", created.WorkspaceId);
        Assert.Equal("task/desktop", created.Branch);
        Assert.Equal("abcdef123456", created.HeadSha);
        Assert.Equal("origin/task/desktop", created.Upstream);
        Assert.Equal(2, created.Ahead);
        Assert.Equal(1, created.Behind);
        Assert.Equal(2, created.DirtyCounts.Total);
        Assert.Equal("src/Foo.cs", Assert.Single(created.ChangedFiles).Path);
        Assert.Equal("No upstream freshness issue", Assert.Single(created.Warnings));
        Assert.False(created.IsStale);
        Assert.Equal(15, created.FreshnessSeconds);

        var updatedInput = NewGitSnapshot(_now.AddSeconds(-5));
        updatedInput.Branch = "task/desktop-updated";
        updatedInput.HeadSha = "ffffeeee";
        updatedInput.DirtyCounts = new GitDirtyCounts { Total = 0 };
        updatedInput.ChangedFiles = [];
        var updated = await _snapshots.UpsertGitSnapshotAsync(updatedInput);

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("task/desktop-updated", updated.Branch);
        Assert.Equal("ffffeeee", updated.HeadSha);
        Assert.Equal(0, updated.DirtyCounts.Total);
        Assert.Empty(updated.ChangedFiles);

        var listed = await _snapshots.ListGitSnapshotsAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-1",
            SourceInstanceId = "desktop-a",
            Limit = 10
        });
        var only = Assert.Single(listed);
        Assert.Equal(updated.Id, only.Id);
    }

    [Fact]
    public async Task GetLatestGitSnapshot_ReturnsMissingAndStaleStatesWithoutErrors()
    {
        var missing = await _snapshots.GetLatestGitSnapshotAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-missing",
            SourceInstanceId = "desktop-a",
            StaleAfter = TimeSpan.FromSeconds(30)
        });

        Assert.Equal(DesktopSnapshotState.Missing, missing.State);
        Assert.Equal("missing", missing.FreshnessStatus);
        Assert.Null(missing.Snapshot);

        await _snapshots.UpsertGitSnapshotAsync(NewGitSnapshot(_now.AddMinutes(-5)));
        var stale = await _snapshots.GetLatestGitSnapshotAsync(new DesktopGitSnapshotListOptions
        {
            ProjectId = "proj",
            WorkspaceId = "ws-1",
            SourceInstanceId = "desktop-a",
            StaleAfter = TimeSpan.FromSeconds(30)
        });

        Assert.Equal(DesktopSnapshotState.SourceOffline, stale.State);
        Assert.True(stale.IsStale);
        Assert.Equal("stale", stale.FreshnessStatus);
        Assert.NotNull(stale.Snapshot);
        Assert.True(stale.Snapshot!.IsStale);
    }

    [Fact]
    public async Task UpsertGitSnapshot_AllowsPathNotVisibleAsStatusData()
    {
        var saved = await _snapshots.UpsertGitSnapshotAsync(new DesktopGitSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = "/remote/path/not/visible",
            State = DesktopSnapshotState.PathNotVisible,
            SourceInstanceId = "desktop-a",
            SourceDisplayName = "Desktop A",
            ObservedAt = _now,
            Warnings = ["Path is not visible on this desktop instance."]
        });

        Assert.Equal(DesktopSnapshotState.PathNotVisible, saved.State);
        Assert.Equal("Path is not visible on this desktop instance.", Assert.Single(saved.Warnings));
        Assert.Equal(0, saved.DirtyCounts.Total);
        Assert.Empty(saved.ChangedFiles);
    }

    [Fact]
    public async Task UpsertDiffSnapshot_StoresBoundedDiffForLaterLookup()
    {
        var saved = await _snapshots.UpsertDiffSnapshotAsync(new DesktopDiffSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = _workspace.WorktreePath,
            Path = "src/Foo.cs",
            BaseRef = "main",
            HeadRef = "task/desktop",
            MaxBytes = 4096,
            Diff = "diff --git a/src/Foo.cs b/src/Foo.cs",
            SourceInstanceId = "desktop-a",
            SourceDisplayName = "Desktop A",
            ObservedAt = _now.AddSeconds(-4)
        });

        var loaded = await _snapshots.GetLatestDiffSnapshotAsync(new DesktopDiffSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            RootPath = _workspace.WorktreePath,
            Path = "src/Foo.cs",
            BaseRef = "main",
            HeadRef = "task/desktop",
            SourceInstanceId = "desktop-a",
            MaxBytes = 1,
            ObservedAt = _now
        }, TimeSpan.FromSeconds(30));

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded!.Id);
        Assert.Equal("Desktop A", loaded.SourceDisplayName);
        Assert.False(loaded.IsStale);
        Assert.Contains("diff --git", loaded.Diff);
    }

    [Fact]
    public async Task UpsertSessionSnapshot_StoresControlCapabilitiesWithoutConflatingObservation()
    {
        var saved = await _snapshots.UpsertSessionSnapshotAsync(new DesktopSessionSnapshot
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            WorkspaceId = _workspace.Id,
            SessionId = "pty-1",
            AgentIdentity = "pi",
            Role = "conductor",
            CurrentCommand = "pi",
            CurrentPhase = "working",
            ControlCapabilities = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>("""
                {"focus":true,"terminate":false,"launch_reviewer":true}
                """),
            SourceInstanceId = "desktop-a",
            ObservedAt = _now.AddSeconds(-2)
        });

        Assert.False(saved.IsStale);
        Assert.NotNull(saved.ControlCapabilities);
        Assert.True(saved.ControlCapabilities!.Value.GetProperty("focus").GetBoolean());
        Assert.False(saved.ControlCapabilities!.Value.GetProperty("terminate").GetBoolean());

        var listed = await _snapshots.ListSessionSnapshotsAsync(new DesktopSessionSnapshotListOptions
        {
            ProjectId = "proj",
            TaskId = _task.Id,
            SourceInstanceId = "desktop-a",
            Limit = 10
        });
        Assert.Single(listed);
    }

    private DesktopGitSnapshot NewGitSnapshot(DateTime observedAt) => new()
    {
        ProjectId = "proj",
        TaskId = _task.Id,
        WorkspaceId = _workspace.Id,
        RootPath = _workspace.WorktreePath,
        State = DesktopSnapshotState.Ok,
        Branch = "task/desktop",
        HeadSha = "abcdef123456",
        Upstream = "origin/task/desktop",
        Ahead = 2,
        Behind = 1,
        DirtyCounts = new GitDirtyCounts { Total = 2, Modified = 1, Untracked = 1 },
        ChangedFiles = [new GitFileStatus
        {
            Path = "src/Foo.cs",
            WorktreeStatus = "M",
            Category = "modified"
        }],
        Warnings = ["No upstream freshness issue"],
        SourceInstanceId = "desktop-a",
        SourceDisplayName = "Desktop A",
        ObservedAt = observedAt
    };
}
