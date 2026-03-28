using DenMcp.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DenMcp.Core.Tests.Data;

public class DatabaseInitializerTests : IDisposable
{
    private readonly string _dbPath;

    public DatabaseInitializerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"den-mcp-test-{Guid.NewGuid()}.db");
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public async Task InitializeAsync_CreatesAllTables()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        var tables = new List<string>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));

        Assert.Contains("projects", tables);
        Assert.Contains("tasks", tables);
        Assert.Contains("task_dependencies", tables);
        Assert.Contains("task_history", tables);
        Assert.Contains("messages", tables);
        Assert.Contains("message_reads", tables);
        Assert.Contains("documents", tables);
        Assert.Contains("documents_fts", tables);
    }

    [Fact]
    public async Task InitializeAsync_SeedsGlobalProject()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM projects WHERE id = '_global'";
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal("Global", result);
    }

    [Fact]
    public async Task InitializeAsync_EnablesWalMode()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal("wal", result?.ToString());
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();
        await initializer.InitializeAsync(); // second call should not throw

        await using var conn = new SqliteConnection(initializer.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM projects WHERE id = '_global'";
        var count = (long)(await cmd.ExecuteScalarAsync())!;

        Assert.Equal(1, count);
    }
}
