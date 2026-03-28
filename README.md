# den-mcp

Centralized MCP server and CLI for task management, agent messaging, and document storage across multiple software projects.

Built for workflows where multiple AI coding agents (Claude Code, Codex, Kimi Code) operate across several projects simultaneously and need shared state, communication, and a single source of truth for tasks and documentation.

## Features

- **Task Management** — Hierarchical tasks with subtasks, dependencies (cycle-safe), priority, status workflow, audit history, and a smart next-task algorithm
- **Agent Messaging** — Threaded messages scoped to projects and tasks, with per-agent read state tracking
- **Document Storage** — Markdown documents (PRDs, specs, ADRs, conventions) with full-text search via SQLite FTS5
- **Multi-Project** — Single server instance manages all projects from one SQLite database
- **19 MCP Tools** — Full tool suite exposed via HTTP+SSE for any MCP-compatible agent
- **REST API** — Matching HTTP endpoints for the CLI and potential future UIs
- **CLI** — Rich terminal commands for all operations plus a live Terminal.Gui dashboard

## Quick Start

### Run the Server

```bash
dotnet run --project src/DenMcp.Server
```

Starts on `http://localhost:5199` with database at `~/.den-mcp/den.db`.

Override defaults:
```bash
dotnet run --project src/DenMcp.Server -- --port 5200
dotnet run --project src/DenMcp.Server -- --db-path /tmp/dev.db
```

### Connect an Agent

Add to your project's MCP configuration (`.mcp.json` or equivalent):

```json
{
  "mcpServers": {
    "den": {
      "type": "sse",
      "url": "http://localhost:5199/sse"
    }
  }
}
```

Then add to the project's `CLAUDE.md`:

```markdown
## Project Management — den-mcp

- Project ID: `my-project`
- Your agent identity: `claude-code`
- Check for unread messages at the start of each session:
  `get_messages(project_id="my-project", unread_for="claude-code")`
- To see what to work on next: `next_task(project_id="my-project")`
- Cross-project docs are under project `_global`.
```

### Use the CLI

```bash
# Alias for convenience (or publish and add to PATH)
alias den='dotnet run --project /path/to/den-mcp/src/DenMcp.Cli --'

den projects                          # list all projects
den tasks --project my-project        # list tasks
den task 5 --project my-project       # task detail view
den next --project my-project         # next unblocked task
den create-task --title "Build X" --project my-project
den status 5 done --project my-project
den messages --project my-project     # recent messages
den send --content "Ready for review" --project my-project
den docs --project my-project         # list documents
den doc prd --project my-project      # view a document
den search "authentication"           # full-text search
den dashboard                         # live TUI dashboard
```

The `--project` flag auto-detects from the current directory name when omitted.

### Live Dashboard

```bash
den dashboard
```

Full-screen Terminal.Gui interface with:
- Project selector sidebar
- Task list with status/priority indicators
- Message feed
- Keyboard shortcuts: `Tab` switch panels, `S` change status, `N` next task, `R` refresh, `^Q` quit
- Auto-refreshes every 5 seconds

## Architecture

```
src/
  DenMcp.Core/        — Models, SQLite DB access, repositories
  DenMcp.Server/      — ASP.NET Core MCP + REST server
  DenMcp.Cli/         — Terminal.Gui dashboard + CLI commands
tests/
  DenMcp.Core.Tests/  — Integration tests (35 tests)
  Architecture.Tests/ — Dependency boundary enforcement (8 tests)
```

**Strict dependency boundaries** enforced by tests:
- Core depends only on `Microsoft.Data.Sqlite` and `Microsoft.Extensions.Logging.Abstractions`
- Server references Core (not CLI)
- CLI references Core (not Server) — communicates via HTTP

## MCP Tools

### Projects (3)
| Tool | Description |
|------|-------------|
| `create_project` | Register a new project |
| `list_projects` | List all projects |
| `get_project` | Get project with task/message stats |

### Tasks (7)
| Tool | Description |
|------|-------------|
| `create_task` | Create a task or subtask |
| `update_task` | Update fields with audit history |
| `get_task` | Full detail with deps, subtasks, messages |
| `list_tasks` | List with filters (status, assigned, tags, priority) |
| `next_task` | Smart next unblocked task selection |
| `add_dependency` | Add dependency with cycle detection |
| `remove_dependency` | Remove a dependency |

### Messages (4)
| Tool | Description |
|------|-------------|
| `send_message` | Send to project, task, or thread |
| `get_messages` | Get with filters (unread, since, task) |
| `get_thread` | Complete thread with replies |
| `mark_read` | Mark messages as read for an agent |

### Documents (5)
| Tool | Description |
|------|-------------|
| `store_document` | Create or upsert (by project+slug) |
| `get_document` | Get full content |
| `list_documents` | List summaries (no content) |
| `search_documents` | Full-text search with snippets |
| `delete_document` | Delete a document |

## Next-Task Algorithm

The `next_task` tool returns the highest-priority unblocked task using a two-tier approach:

1. **Subtasks of in-progress parents** — If a parent task is `in_progress`, its `planned` subtasks with all dependencies met are checked first
2. **Top-level planned tasks** — Then top-level tasks with all dependencies met

Within each tier, ranked by: priority (1=critical beats 5=backlog) → fewer dependencies → lower ID.

## Task Statuses

`planned` → `in_progress` → `review` → `done`

Also: `blocked`, `cancelled`

## Tech Stack

- .NET 10, C# latest
- ASP.NET Core (Kestrel) + ModelContextProtocol SDK
- SQLite with WAL mode + FTS5
- Terminal.Gui 1.x
- xUnit

## Deployment

A systemd service file is provided at `deploy/den-mcp.service`. Publish and install:

```bash
dotnet publish src/DenMcp.Server -c Release -o /opt/den-mcp
sudo cp deploy/den-mcp.service /etc/systemd/system/
sudo systemctl enable --now den-mcp
```

## License

MIT
