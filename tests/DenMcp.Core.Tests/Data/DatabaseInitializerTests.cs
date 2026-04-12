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
        Assert.Contains("review_rounds", tables);
        Assert.Contains("review_findings", tables);
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

    [Fact]
    public async Task InitializeAsync_AddsReviewRoundDiffMetadataColumnsToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE review_rounds (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    task_id INTEGER NOT NULL REFERENCES tasks(id),
                    round_number INTEGER NOT NULL,
                    requested_by TEXT NOT NULL,
                    branch TEXT NOT NULL,
                    base_branch TEXT NOT NULL,
                    base_commit TEXT NOT NULL,
                    head_commit TEXT NOT NULL,
                    last_reviewed_head_commit TEXT,
                    commits_since_last_review INTEGER,
                    tests_run TEXT,
                    notes TEXT,
                    verdict TEXT,
                    verdict_by TEXT,
                    verdict_notes TEXT,
                    requested_at TEXT NOT NULL DEFAULT (datetime('now')),
                    verdict_at TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var columns = new List<string>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "PRAGMA table_info(review_rounds)";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("preferred_diff_base_ref", columns);
        Assert.Contains("alternate_diff_base_ref", columns);
        Assert.Contains("delta_base_commit", columns);
        Assert.Contains("inherited_commit_count", columns);
        Assert.Contains("task_local_commit_count", columns);

        await using var seedProject = verify.CreateCommand();
        seedProject.CommandText = "INSERT INTO projects (id, name) VALUES ('proj', 'Test')";
        await seedProject.ExecuteNonQueryAsync();

        await using var seedTask = verify.CreateCommand();
        seedTask.CommandText = "INSERT INTO tasks (project_id, title) VALUES ('proj', 'Review target')";
        await seedTask.ExecuteNonQueryAsync();

        await using var invalidInsert = verify.CreateCommand();
        invalidInsert.CommandText = """
            INSERT INTO review_rounds (
                task_id, round_number, requested_by, branch, base_branch, base_commit, head_commit,
                inherited_commit_count
            )
            VALUES (1, 1, 'codex', 'task/596', 'main', 'abc123', 'def456', -1)
            """;

        await Assert.ThrowsAsync<SqliteException>(() => invalidInsert.ExecuteNonQueryAsync());
    }
}
