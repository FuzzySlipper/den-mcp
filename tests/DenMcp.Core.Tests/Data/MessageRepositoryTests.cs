using DenMcp.Core.Data;
using DenMcp.Core.Models;

namespace DenMcp.Core.Tests.Data;

public class MessageRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private MessageRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new MessageRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    [Fact]
    public async Task SendAndGet_RoundTrips()
    {
        var msg = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "claude-code", Content = "Hello!" });
        Assert.True(msg.Id > 0);

        var messages = await _repo.GetMessagesAsync("proj");
        Assert.Single(messages);
        Assert.Equal("Hello!", messages[0].Content);
    }

    [Fact]
    public async Task GetMessages_FiltersByUnread()
    {
        var m1 = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Msg 1" });
        var m2 = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Msg 2" });
        await _repo.MarkReadAsync("claude-code", [m1.Id]);

        var unread = await _repo.GetMessagesAsync("proj", unreadFor: "claude-code");
        Assert.Single(unread);
        Assert.Equal("Msg 2", unread[0].Content);
    }

    [Fact]
    public async Task GetMessages_ExcludesOwnMessages()
    {
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "claude-code", Content = "My own msg" });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "From codex" });

        var unread = await _repo.GetMessagesAsync("proj", unreadFor: "claude-code");
        Assert.Single(unread);
        Assert.Equal("From codex", unread[0].Content);
    }

    [Fact]
    public async Task Threading_Works()
    {
        var root = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "alice", Content = "Thread root" });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "bob", Content = "Reply 1", ThreadId = root.Id });
        await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "alice", Content = "Reply 2", ThreadId = root.Id });

        var thread = await _repo.GetThreadAsync(root.Id);
        Assert.Equal("Thread root", thread.Root.Content);
        Assert.Equal(2, thread.Replies.Count);
        Assert.Equal("Reply 1", thread.Replies[0].Content);
        Assert.Equal("Reply 2", thread.Replies[1].Content);
    }

    [Fact]
    public async Task MarkRead_IsIdempotent()
    {
        var msg = await _repo.CreateAsync(new Message { ProjectId = "proj", Sender = "codex", Content = "Test" });
        var count1 = await _repo.MarkReadAsync("claude-code", [msg.Id]);
        var count2 = await _repo.MarkReadAsync("claude-code", [msg.Id]);

        Assert.Equal(1, count1);
        Assert.Equal(0, count2); // already read
    }
}
