using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DenMcp.Core.Data;

public sealed class DatabaseInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(string databasePath, ILogger<DatabaseInitializer> logger)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={databasePath}";
        _logger = logger;
    }

    public string ConnectionString => _connectionString;

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        await using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
        await pragmaCmd.ExecuteNonQueryAsync();

        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = Schema;
        await schemaCmd.ExecuteNonQueryAsync();

        // Migrations for existing databases
        await RunMigrationsAsync(connection);

        _logger.LogInformation("Database initialized at {ConnectionString}", _connectionString);
    }

    internal const string Schema = """
        ------------------------------------------------------------
        -- PROJECTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS projects (
            id          TEXT PRIMARY KEY,
            name        TEXT NOT NULL,
            root_path   TEXT,
            description TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        INSERT OR IGNORE INTO projects (id, name, description)
        VALUES ('_global', 'Global', 'Cross-project documents and discussions');

        ------------------------------------------------------------
        -- TASKS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS tasks (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            parent_id   INTEGER REFERENCES tasks(id) ON DELETE CASCADE,
            title       TEXT NOT NULL,
            description TEXT,
            status      TEXT NOT NULL DEFAULT 'planned'
                        CHECK (status IN (
                            'planned',
                            'in_progress',
                            'review',
                            'blocked',
                            'done',
                            'cancelled'
                        )),
            priority    INTEGER NOT NULL DEFAULT 3
                        CHECK (priority BETWEEN 1 AND 5),
            assigned_to TEXT,
            tags        TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_tasks_project_status ON tasks(project_id, status);
        CREATE INDEX IF NOT EXISTS idx_tasks_assigned ON tasks(assigned_to);
        CREATE INDEX IF NOT EXISTS idx_tasks_parent ON tasks(parent_id);

        ------------------------------------------------------------
        -- TASK DEPENDENCIES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS task_dependencies (
            task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            depends_on  INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            PRIMARY KEY (task_id, depends_on),
            CHECK (task_id != depends_on)
        );

        ------------------------------------------------------------
        -- TASK HISTORY
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS task_history (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            field       TEXT NOT NULL,
            old_value   TEXT,
            new_value   TEXT,
            changed_by  TEXT,
            changed_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_task_history_task ON task_history(task_id);

        ------------------------------------------------------------
        -- MESSAGES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS messages (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            task_id     INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            thread_id   INTEGER REFERENCES messages(id) ON DELETE SET NULL,
            sender      TEXT NOT NULL,
            content     TEXT NOT NULL,
            metadata    TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now'))
        );

        CREATE INDEX IF NOT EXISTS idx_messages_project_task ON messages(project_id, task_id);
        CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);

        ------------------------------------------------------------
        -- MESSAGE READ STATE
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS message_reads (
            message_id  INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            agent       TEXT NOT NULL,
            read_at     TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (message_id, agent)
        );

        ------------------------------------------------------------
        -- DOCUMENTS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS documents (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            slug        TEXT NOT NULL,
            title       TEXT NOT NULL,
            content     TEXT NOT NULL,
            doc_type    TEXT NOT NULL DEFAULT 'spec'
                        CHECK (doc_type IN (
                            'prd',
                            'spec',
                            'adr',
                            'convention',
                            'reference',
                            'note'
                        )),
            tags        TEXT,
            created_at  TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(project_id, slug)
        );

        CREATE INDEX IF NOT EXISTS idx_documents_project_type ON documents(project_id, doc_type);

        ------------------------------------------------------------
        -- FTS5 for full-text search across documents
        ------------------------------------------------------------
        CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
            title,
            content,
            tags,
            content=documents,
            content_rowid=id,
            tokenize='porter unicode61'
        );

        -- Triggers to keep FTS in sync
        CREATE TRIGGER IF NOT EXISTS documents_ai AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, title, content, tags)
            VALUES (new.id, new.title, new.content, new.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS documents_ad AFTER DELETE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
            VALUES ('delete', old.id, old.title, old.content, old.tags);
        END;

        CREATE TRIGGER IF NOT EXISTS documents_au AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
            VALUES ('delete', old.id, old.title, old.content, old.tags);
            INSERT INTO documents_fts(rowid, title, content, tags)
            VALUES (new.id, new.title, new.content, new.tags);
        END;

        ------------------------------------------------------------
        -- AGENT SESSIONS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS agent_sessions (
            agent           TEXT NOT NULL,
            project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            session_id      TEXT,
            status          TEXT NOT NULL DEFAULT 'active'
                            CHECK (status IN ('active', 'inactive')),
            checked_in_at   TEXT NOT NULL DEFAULT (datetime('now')),
            last_heartbeat  TEXT NOT NULL DEFAULT (datetime('now')),
            metadata        TEXT,
            PRIMARY KEY (agent, project_id)
        );

        CREATE INDEX IF NOT EXISTS idx_agent_sessions_project_status
            ON agent_sessions(project_id, status);

        ------------------------------------------------------------
        -- REVIEW ROUNDS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS review_rounds (
            id                          INTEGER PRIMARY KEY AUTOINCREMENT,
            task_id                     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            round_number                INTEGER NOT NULL,
            requested_by                TEXT NOT NULL,
            branch                      TEXT NOT NULL,
            base_branch                 TEXT NOT NULL,
            base_commit                 TEXT NOT NULL,
            head_commit                 TEXT NOT NULL,
            last_reviewed_head_commit   TEXT,
            commits_since_last_review   INTEGER,
            tests_run                   TEXT,
            notes                       TEXT,
            preferred_diff_base_ref     TEXT,
            preferred_diff_base_commit  TEXT,
            preferred_diff_head_ref     TEXT,
            preferred_diff_head_commit  TEXT,
            alternate_diff_base_ref     TEXT,
            alternate_diff_base_commit  TEXT,
            alternate_diff_head_ref     TEXT,
            alternate_diff_head_commit  TEXT,
            delta_base_commit           TEXT,
            inherited_commit_count      INTEGER
                                        CHECK (inherited_commit_count IS NULL OR inherited_commit_count >= 0),
            task_local_commit_count     INTEGER
                                        CHECK (task_local_commit_count IS NULL OR task_local_commit_count >= 0),
            verdict                     TEXT
                                        CHECK (verdict IS NULL OR verdict IN (
                                            'changes_requested',
                                            'looks_good',
                                            'follow_up_needed',
                                            'blocked_by_dependency'
                                        )),
            verdict_by                  TEXT,
            verdict_notes               TEXT,
            requested_at                TEXT NOT NULL DEFAULT (datetime('now')),
            verdict_at                  TEXT,
            UNIQUE(task_id, round_number)
        );

        CREATE INDEX IF NOT EXISTS idx_review_rounds_task
            ON review_rounds(task_id, round_number);

        ------------------------------------------------------------
        -- REVIEW FINDINGS
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS review_findings (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            finding_key         TEXT NOT NULL UNIQUE,
            task_id             INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            review_round_id     INTEGER NOT NULL REFERENCES review_rounds(id) ON DELETE CASCADE,
            finding_number      INTEGER NOT NULL,
            created_by          TEXT NOT NULL,
            category            TEXT NOT NULL
                                CHECK (category IN (
                                    'blocking_bug',
                                    'acceptance_gap',
                                    'test_weakness',
                                    'follow_up_candidate'
                                )),
            summary             TEXT NOT NULL,
            notes               TEXT,
            file_references     TEXT,
            test_commands       TEXT,
            status              TEXT NOT NULL DEFAULT 'open'
                                CHECK (status IN (
                                    'open',
                                    'claimed_fixed',
                                    'verified_fixed',
                                    'not_fixed',
                                    'superseded',
                                    'split_to_follow_up'
                                )),
            status_updated_by   TEXT,
            status_notes        TEXT,
            status_updated_at   TEXT,
            response_by         TEXT,
            response_notes      TEXT,
            response_at         TEXT,
            follow_up_task_id   INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            created_at          TEXT NOT NULL DEFAULT (datetime('now')),
            updated_at          TEXT NOT NULL DEFAULT (datetime('now')),
            UNIQUE(task_id, finding_number)
        );

        CREATE INDEX IF NOT EXISTS idx_review_findings_task_status
            ON review_findings(task_id, status, finding_number);
        CREATE INDEX IF NOT EXISTS idx_review_findings_round
            ON review_findings(review_round_id, finding_number);

        ------------------------------------------------------------
        -- DISPATCH ENTRIES
        ------------------------------------------------------------
        CREATE TABLE IF NOT EXISTS dispatch_entries (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id      TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            target_agent    TEXT NOT NULL,
            status          TEXT NOT NULL DEFAULT 'pending'
                            CHECK (status IN (
                                'pending',
                                'approved',
                                'rejected',
                                'completed',
                                'expired'
                            )),
            trigger_type    TEXT NOT NULL
                            CHECK (trigger_type IN ('message', 'task_status')),
            trigger_id      INTEGER NOT NULL,
            task_id         INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
            summary         TEXT,
            context_prompt  TEXT,
            dedup_key       TEXT NOT NULL,
            created_at      TEXT NOT NULL DEFAULT (datetime('now')),
            expires_at      TEXT NOT NULL,
            decided_at      TEXT,
            completed_at    TEXT,
            decided_by      TEXT,
            completed_by    TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_dispatch_status
            ON dispatch_entries(status);
        CREATE INDEX IF NOT EXISTS idx_dispatch_project_status
            ON dispatch_entries(project_id, status);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_dispatch_dedup
            ON dispatch_entries(dedup_key) WHERE status = 'pending';
        """;

    private static async Task RunMigrationsAsync(SqliteConnection connection)
    {
        // Add session_id column to agent_sessions if it doesn't exist.
        // SQLite has no ALTER TABLE ... ADD COLUMN IF NOT EXISTS,
        // so we check via PRAGMA table_info.
        await TryAddColumnAsync(connection, "agent_sessions", "session_id", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_base_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_head_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "preferred_diff_head_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_base_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_head_ref", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "alternate_diff_head_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "delta_base_commit", "TEXT");
        await TryAddColumnAsync(connection, "review_rounds", "inherited_commit_count",
            "INTEGER CHECK (inherited_commit_count IS NULL OR inherited_commit_count >= 0)");
        await TryAddColumnAsync(connection, "review_rounds", "task_local_commit_count",
            "INTEGER CHECK (task_local_commit_count IS NULL OR task_local_commit_count >= 0)");
    }

    private static async Task TryAddColumnAsync(SqliteConnection connection, string table, string column, string columnDefinition)
    {
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await checkCmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == column)
                return; // column already exists
        }
        await reader.CloseAsync();

        await using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDefinition}";
        await alterCmd.ExecuteNonQueryAsync();
    }
}
