using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class ProjectRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private ProjectRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new ProjectRepository(_testDb.Db);
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task CreateAndGet_RoundTrips()
    {
        var project = await _repo.CreateAsync(new Project { Id = "test", Name = "Test Project", Description = "A test" });
        Assert.Equal("test", project.Id);
        Assert.Equal("Test Project", project.Name);

        var fetched = await _repo.GetByIdAsync("test");
        Assert.NotNull(fetched);
        Assert.Equal("Test Project", fetched.Name);
    }

    [Fact]
    public async Task GetAll_IncludesGlobalProject()
    {
        var all = await _repo.GetAllAsync();
        Assert.Contains(all, p => p.Id == "_global");
    }

    [Fact]
    public async Task GetWithStats_ReturnsTaskCounts()
    {
        await _repo.CreateAsync(new Project { Id = "stats-test", Name = "Stats" });
        var taskRepo = new TaskRepository(_testDb.Db);
        await taskRepo.CreateAsync(new ProjectTask { ProjectId = "stats-test", Title = "T1" });
        await taskRepo.CreateAsync(new ProjectTask { ProjectId = "stats-test", Title = "T2" });

        var stats = await _repo.GetWithStatsAsync("stats-test");
        Assert.Equal(2, stats.TaskCountsByStatus[Models.TaskStatus.Planned]);
    }

    [Fact]
    public async Task GetWithStats_CountsUnreadMessages()
    {
        await _repo.CreateAsync(new Project { Id = "msg-test", Name = "Msg" });
        var msgRepo = new MessageRepository(_testDb.Db);
        await msgRepo.CreateAsync(new Message { ProjectId = "msg-test", Sender = "codex", Content = "Hello" });
        await msgRepo.CreateAsync(new Message { ProjectId = "msg-test", Sender = "claude-code", Content = "Hi" });

        var stats = await _repo.GetWithStatsAsync("msg-test", agent: "claude-code");
        Assert.Equal(1, stats.UnreadMessageCount); // only codex's message is unread
    }
}
