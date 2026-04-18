# CLAUDE.md Snippet Template

Add this to any project's CLAUDE.md to integrate with den-mcp:

```markdown
## Project Management — den-mcp

This project uses a centralized den-mcp server for task management, agent messaging,
and document storage. The MCP server is connected as "den".

- Project ID: `<directory-name>`
- Your agent identity: `claude-code`
- When updating tasks, always include your identity as the `agent` parameter.
- Check for unread messages at the start of each session:
  `get_messages(project_id="<directory-name>", unread_for="claude-code")`
- When you complete a task, update its status:
  `update_task(task_id=N, agent="claude-code", status="done")`
- To see what to work on next:
  `next_task(project_id="<directory-name>")`
- Cross-project docs (conventions, shared specs) are under project `_global`.
```

## MCP Server Configuration

Add to your `.mcp.json` or MCP settings:

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

## Agent Identity Convention

| Agent       | Identity String |
|-------------|----------------|
| Claude Code | `claude-code`  |
| Codex CLI   | `codex`        |
| OMP         | `omp`          |
| Kimi Code   | `kimi-code`    |
| User        | `user`         |

## Available MCP Tools (19)

### Projects (3)
- `create_project` — Register a new project
- `list_projects` — List all projects
- `get_project` — Get project with stats

### Tasks (7)
- `create_task` — Create a task or subtask
- `update_task` — Update task fields (records audit history)
- `get_task` — Get full task with deps, subtasks, messages
- `list_tasks` — List tasks with filters
- `next_task` — Get next unblocked task
- `add_dependency` — Add dependency (rejects cycles)
- `remove_dependency` — Remove dependency

### Messages (4)
- `send_message` — Send a message (project/task/thread scoped)
- `get_messages` — Get messages with filters (unread, since, etc.)
- `get_thread` — Get complete message thread
- `mark_read` — Mark messages as read

### Documents (5)
- `store_document` — Create or update a document
- `get_document` — Get full document content
- `list_documents` — List document summaries
- `search_documents` — Full-text search (FTS5)
- `delete_document` — Delete a document
