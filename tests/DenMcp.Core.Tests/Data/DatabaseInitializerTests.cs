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
        Assert.DoesNotContain("notification_message_links", tables);
        Assert.Contains("documents", tables);
        Assert.Contains("documents_fts", tables);
        Assert.Contains("agent_stream_entries", tables);
        Assert.Contains("agent_runs", tables);
        Assert.Contains("agent_workspaces", tables);
        Assert.Contains("agent_instance_bindings", tables);
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

    [Fact]
    public async Task InitializeAsync_AddsCompletedByColumnToExistingDispatchEntriesTable()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );

                CREATE TABLE dispatch_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    target_agent TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'pending',
                    trigger_type TEXT NOT NULL,
                    trigger_id INTEGER NOT NULL,
                    task_id INTEGER,
                    summary TEXT,
                    context_prompt TEXT,
                    dedup_key TEXT NOT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now')),
                    expires_at TEXT NOT NULL,
                    decided_at TEXT,
                    completed_at TEXT,
                    decided_by TEXT
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
        checkCmd.CommandText = "PRAGMA table_info(dispatch_entries)";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(1));

        Assert.Contains("completed_by", columns);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentStreamTableToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'agent_stream_entries'";
        var result = await checkCmd.ExecuteScalarAsync();

        Assert.Equal("agent_stream_entries", result);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentInstanceBindingsTableToExistingDatabase()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'agent_instance_bindings'";
        var result = await checkCmd.ExecuteScalarAsync();

        Assert.Equal("agent_instance_bindings", result);
    }

    [Fact]
    public async Task InitializeAsync_AddsAgentStreamIndexesAndDeduplicatesExistingRows()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE projects (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    description TEXT
                );

                INSERT INTO projects (id, name) VALUES ('proj', 'Project');

                CREATE TABLE agent_stream_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stream_kind TEXT NOT NULL CHECK (stream_kind IN ('ops', 'message')),
                    event_type TEXT NOT NULL,
                    project_id TEXT,
                    task_id INTEGER,
                    thread_id INTEGER,
                    dispatch_id INTEGER,
                    sender TEXT NOT NULL,
                    sender_instance_id TEXT,
                    recipient_agent TEXT,
                    recipient_role TEXT,
                    recipient_instance_id TEXT,
                    delivery_mode TEXT NOT NULL CHECK (delivery_mode IN ('record_only', 'notify', 'wake')),
                    body TEXT,
                    metadata TEXT,
                    dedup_key TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT INTO agent_stream_entries (
                    stream_kind, event_type, project_id, sender, recipient_agent, delivery_mode, dedup_key, body
                ) VALUES
                    ('ops', 'review_requested', 'proj', 'codex', 'claude-code', 'wake', 'dup-key', 'first'),
                    ('ops', 'review_requested', 'proj', 'codex', 'claude-code', 'wake', 'dup-key', 'second');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var countCmd = verify.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM agent_stream_entries WHERE dedup_key = 'dup-key'";
        var duplicateCount = (long)(await countCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, duplicateCount);

        var indexes = new List<string>();
        await using var indexCmd = verify.CreateCommand();
        indexCmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'agent_stream_entries'";
        await using var reader = await indexCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indexes.Add(reader.GetString(0));

        Assert.Contains("idx_agent_stream_sender_created", indexes);
        Assert.Contains("idx_agent_stream_sender_instance_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_agent_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_role_created", indexes);
        Assert.Contains("idx_agent_stream_recipient_instance_created", indexes);
        Assert.Contains("idx_agent_stream_dedup", indexes);
    }

    [Fact]
    public async Task InitializeAsync_AddsIntentColumnAndBackfillsLegacyMessageTypes()
    {
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id TEXT NOT NULL,
                    task_id INTEGER,
                    thread_id INTEGER,
                    sender TEXT NOT NULL,
                    content TEXT NOT NULL,
                    metadata TEXT,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                INSERT INTO messages (project_id, sender, content, metadata) VALUES
                    ('proj', 'codex', 'Request review', '{"type":"review_request_packet","recipient":"claude-code"}'),
                    ('proj', 'codex', 'Planning handoff', '{"type":"planning_summary","recipient":"claude-code"}'),
                    ('proj', 'codex', 'Unknown legacy type', '{"type":"something_else"}'),
                    ('proj', 'codex', 'Malformed json', '{not-json');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var rows = new List<(string Content, string Intent)>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT content, intent FROM messages ORDER BY id";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("Request review", row.Content);
                Assert.Equal("review_request", row.Intent);
            },
            row =>
            {
                Assert.Equal("Planning handoff", row.Content);
                Assert.Equal("handoff", row.Intent);
            },
            row =>
            {
                Assert.Equal("Unknown legacy type", row.Content);
                Assert.Equal("general", row.Intent);
            },
            row =>
            {
                Assert.Equal("Malformed json", row.Content);
                Assert.Equal("general", row.Intent);
            });
    }

    [Fact]
    public async Task InitializeAsync_MessageIntentConstraintRejectsUnknownValues()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        await using var insert = verify.CreateCommand();
        insert.CommandText = """
            INSERT INTO messages (project_id, sender, content, intent)
            VALUES ('_global', 'codex', 'Bad intent', 'not_real')
            """;

        await Assert.ThrowsAsync<SqliteException>(() => insert.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task InitializeAsync_BackfillsHistoricalDispatchCleanup()
    {
        var initializer = new DatabaseInitializer(_dbPath, NullLogger<DatabaseInitializer>.Instance);
        await initializer.InitializeAsync();

        await using (var seed = new SqliteConnection(initializer.ConnectionString))
        {
            await seed.OpenAsync();

            await using var cmd = seed.CreateCommand();
            cmd.CommandText = """
                INSERT INTO projects (id, name) VALUES ('proj', 'Test Project');

                INSERT INTO tasks (id, project_id, title, status) VALUES
                    (1, 'proj', 'Done task', 'done'),
                    (2, 'proj', 'Cancelled task', 'cancelled'),
                    (3, 'proj', 'Active review task', 'review'),
                    (4, 'proj', 'Other active task', 'review');

                INSERT INTO dispatch_entries (
                    id, project_id, target_agent, status, trigger_type, trigger_id, task_id, dedup_key, expires_at
                ) VALUES
                    (1, 'proj', 'claude-code', 'pending', 'message', 1001, 1, 'done-task-pending', datetime('now', '+1 day')),
                    (2, 'proj', 'claude-code', 'approved', 'message', 1002, 2, 'cancelled-task-approved', datetime('now', '+1 day')),
                    (3, 'proj', 'claude-code', 'pending', 'message', 1003, 3, 'older-open-review-request', datetime('now', '+1 day')),
                    (4, 'proj', 'claude-code', 'pending', 'message', 1004, 3, 'newer-open-review-request', datetime('now', '+1 day')),
                    (5, 'proj', 'codex', 'pending', 'message', 1005, 3, 'different-target-stays-open', datetime('now', '+1 day')),
                    (6, 'proj', 'claude-code', 'pending', 'message', 1006, 4, 'different-task-stays-open', datetime('now', '+1 day')),
                    (7, 'proj', 'claude-code', 'pending', 'message', 1007, NULL, 'project-level-dispatch-stays-open', datetime('now', '+1 day'));
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await initializer.InitializeAsync();

        await using var verify = new SqliteConnection(initializer.ConnectionString);
        await verify.OpenAsync();

        var statuses = new List<(int Id, string Status)>();
        await using var checkCmd = verify.CreateCommand();
        checkCmd.CommandText = "SELECT id, status FROM dispatch_entries ORDER BY id";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            statuses.Add((reader.GetInt32(0), reader.GetString(1)));

        Assert.Collection(
            statuses,
            row => Assert.Equal((1, "expired"), row),
            row => Assert.Equal((2, "expired"), row),
            row => Assert.Equal((3, "expired"), row),
            row => Assert.Equal((4, "pending"), row),
            row => Assert.Equal((5, "pending"), row),
            row => Assert.Equal((6, "pending"), row),
            row => Assert.Equal((7, "pending"), row));
    }
}
