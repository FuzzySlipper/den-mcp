using DenMcp.Core.Data;
using DenMcp.Core.Models;
using Microsoft.Data.Sqlite;

namespace DenMcp.Core.Tests.Data;

public class DispatchRepositoryTests : IAsyncLifetime
{
    private readonly TestDb _testDb = new();
    private DispatchRepository _repo = null!;

    public async Task InitializeAsync()
    {
        await _testDb.InitializeAsync();
        _repo = new DispatchRepository(_testDb.Db);
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "proj", Name = "Test" });
    }

    public Task DisposeAsync() => _testDb.DisposeAsync();

    private DispatchEntry MakeEntry(int triggerId = 1, string agent = "claude-code",
        DispatchTriggerType triggerType = DispatchTriggerType.TaskStatus) => new()
    {
        ProjectId = "proj",
        TargetAgent = agent,
        TriggerType = triggerType,
        TriggerId = triggerId,
        TaskId = null,
        Summary = "test dispatch",
        DedupKey = DispatchEntry.BuildDedupKey(triggerType, triggerId, agent),
        ExpiresAt = DateTime.UtcNow.AddHours(24)
    };

    [Fact]
    public async Task CreateIfAbsent_NewEntry_ReturnsCreatedTrue()
    {
        var (entry, created) = await _repo.CreateIfAbsentAsync(MakeEntry());
        Assert.True(created);
        Assert.True(entry.Id > 0);
        Assert.Equal(DispatchStatus.Pending, entry.Status);
        Assert.Equal("claude-code", entry.TargetAgent);
        Assert.Equal("proj", entry.ProjectId);
    }

    [Fact]
    public async Task CreateIfAbsent_DuplicateDedupKey_ReturnsFalse()
    {
        var (first, created1) = await _repo.CreateIfAbsentAsync(MakeEntry());
        Assert.True(created1);

        var (second, created2) = await _repo.CreateIfAbsentAsync(MakeEntry());
        Assert.False(created2);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task CreateIfAbsent_DedupKeyAllowedAfterResolution()
    {
        var (first, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        await _repo.RejectAsync(first.Id, "user");

        // Same dedup key should now succeed — the old one is no longer pending
        var (second, created) = await _repo.CreateIfAbsentAsync(MakeEntry());
        Assert.True(created);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task GetById_ReturnsEntry()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        var fetched = await _repo.GetByIdAsync(entry.Id);
        Assert.NotNull(fetched);
        Assert.Equal(entry.Id, fetched.Id);
        Assert.Equal("test dispatch", fetched.Summary);
    }

    [Fact]
    public async Task GetById_NotFound_ReturnsNull()
    {
        var result = await _repo.GetByIdAsync(9999);
        Assert.Null(result);
    }

    [Fact]
    public async Task List_FiltersByProject()
    {
        // Create a second project
        var projRepo = new ProjectRepository(_testDb.Db);
        await projRepo.CreateAsync(new Project { Id = "other", Name = "Other" });

        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 1));
        var otherEntry = MakeEntry(triggerId: 2);
        otherEntry.ProjectId = "other";
        otherEntry.DedupKey = DispatchEntry.BuildDedupKey(otherEntry.TriggerType, 2, otherEntry.TargetAgent);
        await _repo.CreateIfAbsentAsync(otherEntry);

        var projList = await _repo.ListAsync(projectId: "proj");
        Assert.Single(projList);

        var allList = await _repo.ListAsync();
        Assert.Equal(2, allList.Count);
    }

    [Fact]
    public async Task List_FiltersByStatus()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 1));
        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 2));
        await _repo.ApproveAsync(entry.Id, "user");

        var pending = await _repo.ListAsync(statuses: [DispatchStatus.Pending]);
        Assert.Single(pending);

        var approved = await _repo.ListAsync(statuses: [DispatchStatus.Approved]);
        Assert.Single(approved);
    }

    [Fact]
    public async Task List_FiltersByTargetAgent()
    {
        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 1, agent: "claude-code"));
        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 2, agent: "codex"));

        var claudeList = await _repo.ListAsync(targetAgent: "claude-code");
        Assert.Single(claudeList);
        Assert.Equal("claude-code", claudeList[0].TargetAgent);
    }

    [Fact]
    public async Task Approve_TransitionsPendingToApproved()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        var approved = await _repo.ApproveAsync(entry.Id, "user");

        Assert.Equal(DispatchStatus.Approved, approved.Status);
        Assert.Equal("user", approved.DecidedBy);
        Assert.NotNull(approved.DecidedAt);
    }

    [Fact]
    public async Task Approve_NonPending_Throws()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        await _repo.ApproveAsync(entry.Id, "user");

        // Already approved — can't approve again
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.ApproveAsync(entry.Id, "user"));
    }

    [Fact]
    public async Task Reject_TransitionsPendingToRejected()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        var rejected = await _repo.RejectAsync(entry.Id, "user");

        Assert.Equal(DispatchStatus.Rejected, rejected.Status);
        Assert.Equal("user", rejected.DecidedBy);
        Assert.NotNull(rejected.DecidedAt);
    }

    [Fact]
    public async Task Complete_TransitionsApprovedToCompleted()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        await _repo.ApproveAsync(entry.Id, "user");
        var completed = await _repo.CompleteAsync(entry.Id, "claude-code");

        Assert.Equal(DispatchStatus.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
    }

    [Fact]
    public async Task Complete_PendingEntry_Throws()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());

        // Can't complete a pending entry — must be approved first
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repo.CompleteAsync(entry.Id));
    }

    [Fact]
    public async Task ExpireStale_ExpiresOldPendingEntries()
    {
        var entry = MakeEntry();
        entry.ExpiresAt = DateTime.UtcNow.AddMinutes(-5); // already expired
        await _repo.CreateIfAbsentAsync(entry);

        var expiredCount = await _repo.ExpireStaleAsync(DateTime.UtcNow);
        Assert.Equal(1, expiredCount);

        var fetched = await _repo.GetByIdAsync(1);
        Assert.NotNull(fetched);
        Assert.Equal(DispatchStatus.Expired, fetched.Status);
    }

    [Fact]
    public async Task ExpireStale_DoesNotExpireApproved()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        await _repo.ApproveAsync(entry.Id, "user");

        // Even if expires_at is in the past, approved entries are not expired
        var expiredCount = await _repo.ExpireStaleAsync(DateTime.UtcNow.AddDays(30));
        Assert.Equal(0, expiredCount);
    }

    [Fact]
    public async Task GetPendingCount_ReturnsCorrectCount()
    {
        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 1));
        await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 2));
        var (third, _) = await _repo.CreateIfAbsentAsync(MakeEntry(triggerId: 3));
        await _repo.ApproveAsync(third.Id, "user");

        Assert.Equal(2, await _repo.GetPendingCountAsync());
        Assert.Equal(2, await _repo.GetPendingCountAsync("proj"));
        Assert.Equal(0, await _repo.GetPendingCountAsync("nonexistent"));
    }

    [Fact]
    public async Task CreateIfAbsent_NonDedupConstraintViolation_Throws()
    {
        // A bad project_id FK should propagate as SqliteException, not be swallowed as dedup
        var entry = MakeEntry();
        entry.ProjectId = "nonexistent-project";
        entry.DedupKey = DispatchEntry.BuildDedupKey(entry.TriggerType, entry.TriggerId, entry.TargetAgent);

        await Assert.ThrowsAsync<SqliteException>(
            () => _repo.CreateIfAbsentAsync(entry));
    }

    [Fact]
    public async Task Complete_PreservesApproverAndCompleter()
    {
        var (entry, _) = await _repo.CreateIfAbsentAsync(MakeEntry());
        await _repo.ApproveAsync(entry.Id, "george");
        var completed = await _repo.CompleteAsync(entry.Id, "claude-code");

        Assert.Equal("george", completed.DecidedBy);
        Assert.Equal("claude-code", completed.CompletedBy);
    }

    [Fact]
    public async Task BuildDedupKey_IsDeterministic()
    {
        var key1 = DispatchEntry.BuildDedupKey(DispatchTriggerType.TaskStatus, 42, "claude-code");
        var key2 = DispatchEntry.BuildDedupKey(DispatchTriggerType.TaskStatus, 42, "claude-code");
        Assert.Equal(key1, key2);

        var key3 = DispatchEntry.BuildDedupKey(DispatchTriggerType.Message, 42, "claude-code");
        Assert.NotEqual(key1, key3);
    }
}
