# Design Notes

Captured from the initial design session (2026-03-28). These explain the *why* behind decisions that aren't obvious from the code alone.

## Why den-mcp exists

George runs multiple AI agents (Claude Code, Codex, Kimi Code) across several .NET projects (QuillForge, VoxelForge, RuleWeaver, and others). The existing task-master-ai MCP works for task management but has problems:

- It requires its own API key and does AI processing (complexity analysis, research, auto-expansion) that adds friction and cost when a frontier model is already doing the thinking
- It has no inter-agent messaging — George manually copy-pastes between agent windows
- It has no document storage — PRDs and specs live in files with no search
- It's JavaScript/Node.js in a .NET ecosystem

den-mcp replaces all of that with a single C# server that matches George's existing project patterns.

## Key design decisions

### No AI processing in the server

Task-master has analyze-complexity, research, scope-up/scope-down, and AI-powered task expansion. All of these require API keys and make calls to LLM providers. den-mcp intentionally drops all of this. The philosophy is: frontier models creating tasks are smart enough to create well-structured tasks directly. The server just stores and queries — it doesn't second-guess the model.

### Project ID = directory name

Rather than requiring explicit project registration in every agent config, the convention is that project ID is simply the directory name (e.g., `quillforge`, `den-mcp`). This means:
- Agents can auto-detect their project from `$PWD`
- The CLI auto-detects with `--project` omitted
- No full paths in the DB — just the slug

George avoids directory name collisions across projects, so this works for his setup.

### Single SQLite DB, central server

One server instance, one database, all projects. The server could run on George's actual server (not the desktop where he codes) with the CLI connecting over HTTP. This separates the data layer from the development machines and makes it accessible from anywhere on the local network.

### Subtasks via parent_id, not dotted IDs

Task-master uses dotted notation internally (task 5 has subtasks 5.1, 5.2). den-mcp uses a proper `parent_id` foreign key — subtasks are just tasks with a parent. The dotted notation (`5.1`) is a *presentation concern* only, computed in the CLI display. This simplifies the database model and makes queries like "all subtasks of task 5" trivial.

### Dependencies separate from parent-child

A subtask being a child of a parent does NOT imply a dependency. Dependencies are explicit edges in `task_dependencies`. A subtask of task 5 can depend on a subtask of task 8. This is important for cross-cutting work where George might have an agent expand one task into subtasks that depend on work tracked under a different parent.

### Agent messaging is polling-based

Real-time push (SSE notifications to agents) was considered but deferred. The practical workflow is:
1. Agent A finishes work, sends a message
2. George switches to Agent B's window and says "check messages from A"
3. Agent B calls `get_messages(unread_for="codex")`

This is simpler and works well enough. The `unread_for` filter and `mark_read` tracking make it efficient. Real-time notifications could be added later if agents gain the ability to be interrupted.

### Terminal.Gui for the dashboard, not Spectre.Console

George uses Spectre.Console in VoxelForge and RuleWeaver for CLI output. For den-mcp's command mode output, we use manual Console formatting (colors, padding) rather than Spectre.Console because the dependency would add weight for what's mostly simple table output.

For the dashboard (persistent, interactive, multi-panel TUI), Terminal.Gui v1 was chosen because it provides actual window management, keyboard navigation, and live updates — things Spectre.Console's Live rendering can't do. Terminal.Gui v2 is still in prerelease (develop.5203 as of March 2026), so we're on v1.19.0 stable.

### MCP tool descriptions use [Description] attribute, not McpServerTool.Description

The ModelContextProtocol C# SDK (v1.2.0) does NOT have a `Description` property on `McpServerToolAttribute`. Tool descriptions come from `[System.ComponentModel.Description]` on the method, and parameter descriptions come from `[Description]` on each parameter. This was discovered during implementation — the SDK's attribute only has: Name, Title, Destructive, Idempotent, OpenWorld, ReadOnly, UseStructuredContent, OutputSchemaType, IconSource, TaskSupport.

### JSON naming: snake_case everywhere

Both MCP tool responses and REST API responses use `snake_case` for JSON property names. This matches the MCP convention and the SQLite column names. C# models use PascalCase as normal — the `JsonNamingPolicy.SnakeCaseLower` serializer option handles conversion.

### REST API mirrors MCP tools

Every MCP tool has a corresponding REST endpoint. This means the CLI doesn't need to speak MCP protocol — it just uses HTTP/JSON. It also means a future web UI could be built without touching the MCP layer.

## Architecture boundaries

The `Architecture.Tests` project enforces at build time:
- **Core** has zero references to ASP.NET Core, Terminal.Gui, or ModelContextProtocol
- **Server** references Core but not CLI
- **CLI** references Core (for shared model types) but not Server
- `DisableTransitiveProjectReferences=true` prevents accidental leaks

This mirrors the patterns in QuillForge (Core/Providers/Storage/Web) and RuleWeaver (Core/App/Content/Engine).

## What was explicitly dropped from task-master

| task-master feature | den-mcp | Reason |
|---|---|---|
| `analyze_task_complexity` | Dropped | Requires API key, frontier model handles this |
| `complexity_report` | Dropped | Same |
| `expand_task` (AI) | Dropped | Agents expand tasks directly via `create_task` with `parent_id` |
| `scope_up_task` / `scope_down_task` | Dropped | Just update the task description |
| `perform_research` | Dropped | Agent does its own research |
| `parse_prd` (AI) | Dropped | Agent reads the PRD and creates tasks |
| Provider management (20+ LLM providers) | Dropped | No AI processing = no providers |
| `TASK_MASTER_TOOLS` env var (core/standard/all modes) | Dropped | All 19 tools always available |

## What was added beyond task-master

| Feature | Notes |
|---|---|
| Inter-agent messaging | Threaded, task-scoped, with read tracking |
| Document storage + FTS5 | PRDs, specs, ADRs searchable in the DB |
| Multi-project in one server | One DB, multiple project registrations |
| REST API | CLI communicates via HTTP, not MCP |
| TUI Dashboard | Terminal.Gui live view of all projects |
| Task audit history | Field-level change tracking with agent identity |
| Subtask hierarchy | Parent-child via `parent_id` FK |

## Future considerations

- **Agent heartbeat**: Agents could periodically call a `heartbeat` tool so the dashboard shows which agents are active/idle. Silence implies the agent is waiting on a permission prompt or has stopped.
- **Claude Code hooks**: A `PreToolUse` hook could ping den-mcp to indicate activity, but there's no hook for permission prompts specifically.
- **SSE push notifications**: Agents could subscribe to a stream to get notified of new messages without polling.
- **Database migrations**: A version tracking table for schema upgrades when the schema evolves. Not implemented in v1 since the schema is new.
- **Terminal.Gui v2**: When it hits stable, the dashboard could benefit from the improved API. Evaluate migration at that point.
