# Project den-mcp — Schema & MCP Tool Contracts

*Unified project management, agent communication, and document storage for multi-agent CLI workflows.*

---

## SQLite Schema

```sql
-- Enable WAL mode for concurrent reads during single-writer operations.
-- Important for SSE server where one agent may write while another reads.
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

------------------------------------------------------------
-- PROJECTS
------------------------------------------------------------
CREATE TABLE projects (
    id          TEXT PRIMARY KEY,            -- slug derived from directory name or user-defined key
    name        TEXT NOT NULL,               -- human-readable display name
    root_path   TEXT,                        -- optional: absolute path to project root on disk
    description TEXT,
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

-- A reserved "global" project for cross-cutting docs and messages.
INSERT INTO projects (id, name, description)
VALUES ('_global', 'Global', 'Cross-project documents and discussions');

------------------------------------------------------------
-- TASKS
------------------------------------------------------------
CREATE TABLE tasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    title       TEXT NOT NULL,
    description TEXT,                        -- detailed spec / acceptance criteria (markdown)
    status      TEXT NOT NULL DEFAULT 'planned'
                CHECK (status IN (
                    'planned',               -- defined but not started
                    'in_progress',           -- actively being worked
                    'review',                -- awaiting review / sanity check
                    'blocked',               -- waiting on dependency or external input
                    'done',                  -- completed
                    'cancelled'              -- abandoned
                )),
    priority    INTEGER NOT NULL DEFAULT 3   -- 1 (critical) to 5 (backlog)
                CHECK (priority BETWEEN 1 AND 5),
    assigned_to TEXT,                        -- agent identity or null
    tags        TEXT,                        -- JSON array of string tags, e.g. ["combat","core"]
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_tasks_project_status ON tasks(project_id, status);
CREATE INDEX idx_tasks_assigned ON tasks(assigned_to);

------------------------------------------------------------
-- TASK DEPENDENCIES
------------------------------------------------------------
CREATE TABLE task_dependencies (
    task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    depends_on  INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    PRIMARY KEY (task_id, depends_on),
    CHECK (task_id != depends_on)
);

------------------------------------------------------------
-- TASK HISTORY (audit log for status changes)
------------------------------------------------------------
CREATE TABLE task_history (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id     INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    field       TEXT NOT NULL,               -- which field changed
    old_value   TEXT,
    new_value   TEXT,
    changed_by  TEXT,                        -- agent identity
    changed_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_task_history_task ON task_history(task_id);

------------------------------------------------------------
-- MESSAGES
------------------------------------------------------------
CREATE TABLE messages (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    task_id     INTEGER REFERENCES tasks(id) ON DELETE SET NULL,  -- null = project-level channel
    thread_id   INTEGER REFERENCES messages(id) ON DELETE SET NULL, -- null = thread root
    sender      TEXT NOT NULL,               -- agent identity, e.g. "pi", "user", or a manual agent identity
    content     TEXT NOT NULL,               -- message body (markdown)
    metadata    TEXT,                        -- optional JSON blob (e.g. { "type": "plan_review" })
    created_at  TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_messages_project_task ON messages(project_id, task_id);
CREATE INDEX idx_messages_thread ON messages(thread_id);

------------------------------------------------------------
-- MESSAGE READ STATE
------------------------------------------------------------
CREATE TABLE message_reads (
    message_id  INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    agent       TEXT NOT NULL,               -- agent identity
    read_at     TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (message_id, agent)
);

------------------------------------------------------------
-- DOCUMENTS (PRDs, specs, conventions, ADRs, etc.)
------------------------------------------------------------
CREATE TABLE documents (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id  TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,  -- "_global" for cross-project
    slug        TEXT NOT NULL,               -- unique within project, e.g. "damage-multiplier-spec"
    title       TEXT NOT NULL,
    content     TEXT NOT NULL,               -- markdown body
    doc_type    TEXT NOT NULL DEFAULT 'spec'
                CHECK (doc_type IN (
                    'prd',                   -- product requirements document
                    'spec',                  -- technical specification
                    'adr',                   -- architecture decision record
                    'convention',            -- coding/project conventions
                    'reference',             -- general reference material
                    'note'                   -- freeform notes
                )),
    tags        TEXT,                        -- JSON array of string tags
    created_at  TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(project_id, slug)
);

CREATE INDEX idx_documents_project_type ON documents(project_id, doc_type);

------------------------------------------------------------
-- FTS5 for full-text search across documents
------------------------------------------------------------
CREATE VIRTUAL TABLE documents_fts USING fts5(
    title,
    content,
    tags,
    content=documents,
    content_rowid=id,
    tokenize='porter unicode61'
);

-- Triggers to keep FTS in sync
CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN
    INSERT INTO documents_fts(rowid, title, content, tags)
    VALUES (new.id, new.title, new.content, new.tags);
END;

CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN
    INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
    VALUES ('delete', old.id, old.title, old.content, old.tags);
END;

CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN
    INSERT INTO documents_fts(documents_fts, rowid, title, content, tags)
    VALUES ('delete', old.id, old.title, old.content, old.tags);
    INSERT INTO documents_fts(rowid, title, content, tags)
    VALUES (new.id, new.title, new.content, new.tags);
END;
```

---

## MCP Tool Contracts

All tools accept and return JSON. Every tool that operates on project-scoped data
takes `project_id` as a required parameter (string). Agents identify themselves
via a `sender` / `agent` parameter where relevant.

### Project Management (3 tools)

#### `create_project`
Create or register a new project.

| Parameter     | Type   | Required | Notes                                      |
|---------------|--------|----------|--------------------------------------------|
| `id`          | string | yes      | Slug key, e.g. `"rpg-system"`              |
| `name`        | string | yes      | Display name                               |
| `root_path`   | string | no       | Absolute path to project root on disk      |
| `description` | string | no       |                                            |

Returns: the created project record.

#### `list_projects`
List all registered projects.

| Parameter | Type | Required | Notes |
|-----------|------|----------|-------|
| *(none)*  |      |          |       |

Returns: array of project records.

#### `get_project`
Get a single project by ID, including summary stats (task counts by status, unread message count).

| Parameter    | Type   | Required | Notes |
|--------------|--------|----------|-------|
| `project_id` | string | yes      |       |

Returns: project record + summary stats.

---

### Task Management (6 tools)

#### `create_task`

| Parameter     | Type     | Required | Notes                                     |
|---------------|----------|----------|-------------------------------------------|
| `project_id`  | string   | yes      |                                           |
| `title`       | string   | yes      |                                           |
| `description` | string   | no       | Markdown body — spec, acceptance criteria |
| `priority`    | int      | no       | 1-5, default 3                            |
| `tags`        | string[] | no       |                                           |
| `assigned_to` | string   | no       | Agent identity                            |
| `depends_on`  | int[]    | no       | Task IDs this task depends on             |

Returns: the created task record with ID.

#### `update_task`

| Parameter     | Type     | Required | Notes                                          |
|---------------|----------|----------|-------------------------------------------------|
| `project_id`  | string   | yes      |                                                 |
| `task_id`     | int      | yes      |                                                 |
| `title`       | string   | no       |                                                 |
| `description` | string   | no       |                                                 |
| `status`      | string   | no       | Must be valid status enum                       |
| `priority`    | int      | no       |                                                 |
| `tags`        | string[] | no       |                                                 |
| `assigned_to` | string   | no       |                                                 |
| `agent`       | string   | yes      | Identity of agent making the change (for audit) |

Returns: the updated task record.
Side effect: writes to `task_history` for every changed field.

#### `get_task`

| Parameter    | Type   | Required | Notes |
|--------------|--------|----------|-------|
| `project_id` | string | yes      |       |
| `task_id`    | int    | yes      |       |

Returns: full task record including dependencies (as task IDs + titles) and
recent messages on that task (last 10).

#### `list_tasks`

| Parameter    | Type     | Required | Notes                                             |
|--------------|----------|----------|----------------------------------------------------|
| `project_id` | string   | yes      |                                                    |
| `status`     | string[] | no       | Filter by status(es), e.g. `["planned","review"]` |
| `assigned_to`| string   | no       | Filter by assigned agent                           |
| `tags`       | string[] | no       | Filter: task must have ALL specified tags           |
| `priority`   | int      | no       | Filter: tasks at this priority or higher (lower #) |

Returns: array of task summaries (id, title, status, priority, assigned_to, tags, dependency count).

#### `add_dependency`

| Parameter    | Type   | Required | Notes                       |
|--------------|--------|----------|-----------------------------|
| `project_id` | string | yes      |                             |
| `task_id`    | int    | yes      | The task that is blocked    |
| `depends_on` | int    | yes      | The task it depends on      |

Returns: confirmation. Rejects if it would create a cycle.

#### `remove_dependency`

| Parameter    | Type   | Required | Notes |
|--------------|--------|----------|-------|
| `project_id` | string | yes      |       |
| `task_id`    | int    | yes      |       |
| `depends_on` | int    | yes      |       |

Returns: confirmation.

---

### Agent Messaging (4 tools)

#### `send_message`

| Parameter    | Type   | Required | Notes                                           |
|--------------|--------|----------|-------------------------------------------------|
| `project_id` | string | yes      |                                                 |
| `sender`     | string | yes      | Agent identity, e.g. `"pi"`                    |
| `content`    | string | yes      | Markdown body                                   |
| `task_id`    | int    | no       | Attach to a task. Null = project-level channel  |
| `thread_id`  | int    | no       | Reply to an existing message (forms thread)     |
| `metadata`   | object | no       | Freeform JSON, e.g. `{"type":"plan_review"}`. Use `recipient` for a concrete agent identity or `target_role` for a project role like `reviewer` / `implementer`; if both are present, `recipient` wins. |

Returns: the created message record with ID.

#### `get_messages`

| Parameter    | Type   | Required | Notes                                           |
|--------------|--------|----------|-------------------------------------------------|
| `project_id` | string | yes      |                                                 |
| `task_id`    | int    | no       | Filter to messages on a specific task           |
| `since`      | string | no       | ISO datetime — only messages after this time    |
| `unread_for` | string | no       | Agent identity — only unread messages for them  |
| `limit`      | int    | no       | Default 20, max 100                             |

Returns: array of message records, newest first.

#### `get_thread`

| Parameter    | Type   | Required | Notes                                  |
|--------------|--------|----------|----------------------------------------|
| `project_id` | string | yes      |                                        |
| `thread_id`  | int    | yes      | ID of the root message                 |

Returns: the root message + all replies in chronological order.

#### `mark_read`

| Parameter     | Type  | Required | Notes                              |
|---------------|-------|----------|------------------------------------|
| `agent`       | string| yes      | Agent identity                     |
| `message_ids` | int[] | yes      | Messages to mark as read           |

Returns: confirmation with count marked.

---

### Document Storage (5 tools)

#### `store_document`
Create or update a document. If a document with the same `project_id` + `slug`
exists, it is overwritten (with `updated_at` bumped).

| Parameter    | Type     | Required | Notes                                         |
|--------------|----------|----------|-----------------------------------------------|
| `project_id` | string   | yes      | Use `"_global"` for cross-project docs        |
| `slug`       | string   | yes      | Unique within project, e.g. `"damage-system"` |
| `title`      | string   | yes      |                                               |
| `content`    | string   | yes      | Markdown body                                 |
| `doc_type`   | string   | no       | Default `"spec"`. See schema enum.            |
| `tags`       | string[] | no       |                                               |

Returns: the document record with ID.

#### `get_document`

| Parameter    | Type   | Required | Notes                                  |
|--------------|--------|----------|----------------------------------------|
| `project_id` | string | yes      |                                        |
| `slug`       | string | yes      |                                        |

Returns: full document record including content.

#### `list_documents`

| Parameter    | Type     | Required | Notes                                    |
|--------------|----------|----------|------------------------------------------|
| `project_id` | string   | no       | Omit to list across all projects         |
| `doc_type`   | string   | no       | Filter by type                           |
| `tags`       | string[] | no       | Filter: doc must have ALL specified tags |

Returns: array of document summaries (id, project_id, slug, title, doc_type, tags, updated_at).
Does NOT return content — use `get_document` for full content.

#### `search_documents`

| Parameter    | Type   | Required | Notes                                       |
|--------------|--------|----------|---------------------------------------------|
| `query`      | string | yes      | FTS5 search query (supports AND, OR, NOT, "phrases") |
| `project_id` | string | no       | Scope search to one project                 |

Returns: array of search results with `slug`, `title`, `project_id`, `doc_type`,
a `snippet` of matching content (via FTS5 SNIPPET), and a relevance `rank`.

#### `delete_document`

| Parameter    | Type   | Required | Notes |
|--------------|--------|----------|-------|
| `project_id` | string | yes      |       |
| `slug`       | string | yes      |       |

Returns: confirmation.

---

## Agent Identity Convention

Agents identify themselves with a simple string. Current Den-managed conductor
work usually uses `pi`; manually launched agents may use identities such as
`claude-code`, `codex`, `kimi-code`, or `user` for audit/read-state purposes.
Those manual identities are not dispatch-routing instructions.

This is used in `sender`, `assigned_to`, `agent`, and `unread_for` fields.
Not authenticated — it's a trust-based convention for a single-user system.

---

## Deployment Notes

- **Transport**: Streamable HTTP MCP endpoint plus REST API, single long-lived process
- **Hosting**: systemd service or Docker container on local machine
- **Database**: single SQLite file, e.g. `~/.den-mcp/den-mcp.db` or configurable path
- **Networking**: listen on localhost only (or Tailscale for cross-machine access)
- **Config**: database path and listen port via environment variables or appsettings.json
- **Stack**: ASP.NET Core (Kestrel) + ModelContextProtocol + Microsoft.Data.Sqlite

## Manual Agent Snippet

For each project that uses den-mcp, manually launched agents should be told how
to identify themselves and what's available. Prefer global MCP configuration and
avoid project-local files that shadow it. Example guidance:

```markdown
## Project Management — den-mcp

This project uses a centralized den-mcp server for task management, agent messaging,
and document storage. The MCP server is connected as "den".

- Project ID: `rpg-system`
- Use Den as the durable record for task/thread/review updates.
- When updating task status, always include your identity as the `agent` parameter.
- Check unread task-thread messages at the start of each session.
- When you want a sanity check from another agent, send a message with
  `metadata: {"type": "review_request"}` on the relevant task.
- If a review comes back approved, the implementer merges the reviewed head
  and marks the task done; the reviewer approves or requests changes but does
  not merge.
- If the path is straightforward and still fits the current plan, keep working
  until the current slice is complete.
- Stop and ask for guidance when reality materially conflicts with the plan,
  the plan is too vague to implement confidently, scope needs to expand in a
  non-obvious way, repeated failed attempts suggest the assumptions are wrong,
  or you are inventing a complex workaround mainly to cope with local mess.
- Creating or updating Den tasks is cheap; prefer a follow-up task over
  landing thin interfaces, deceptive scaffolding, or code TODOs that leave the
  real behavior unwired.
- Cross-project docs (conventions, shared specs) are under project `_global`.
```
