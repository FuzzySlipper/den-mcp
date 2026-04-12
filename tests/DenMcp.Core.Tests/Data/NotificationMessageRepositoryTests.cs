using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class NotificationMessageRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private NotificationMessageRepository _repo = null!;
    private DispatchRepository _dispatches = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new NotificationMessageRepository(_testDb.Db);
        _dispatches = new DispatchRepository(_testDb.Db);

        var projects = new ProjectRepository(_testDb.Db);
        await projects.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private async Task<DispatchEntry> CreateDispatchAsync(int triggerId, string agent)
    {
        var (entry, _) = await _dispatches.CreateIfAbsentAsync(new DispatchEntry
        {
            ProjectId = "proj",
            TargetAgent = agent,
            TriggerType = DispatchTriggerType.Message,
            TriggerId = triggerId,
            Summary = $"Dispatch {triggerId}",
            DedupKey = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, triggerId, agent),
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        return entry;
    }

    [Fact]
    public async Task LinkDispatchMessageAsync_StoresDispatchLookup()
    {
        var dispatch = await CreateDispatchAsync(1, "claude-code");

        await _repo.LinkDispatchMessageAsync("signal", "1712345678901", dispatch.Id, "+15551234567");

        var linkedDispatchId = await _repo.FindDispatchIdAsync("signal", "1712345678901");
        Assert.Equal(dispatch.Id, linkedDispatchId);
    }

    [Fact]
    public async Task LinkDispatchMessageAsync_ReplacesExistingMappingForSameMessage()
    {
        var first = await CreateDispatchAsync(1, "claude-code");
        var second = await CreateDispatchAsync(2, "codex");

        await _repo.LinkDispatchMessageAsync("signal", "1712345678901", first.Id);
        await _repo.LinkDispatchMessageAsync("signal", "1712345678901", second.Id);

        var linkedDispatchId = await _repo.FindDispatchIdAsync("signal", "1712345678901");
        Assert.Equal(second.Id, linkedDispatchId);
    }
}
