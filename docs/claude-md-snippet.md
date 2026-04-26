# Manual Den MCP Usage For Claude/Codex Agents

Status: current manual MCP reference. This is **not** a bridge/wake workflow and
should not be used to configure dispatch-driven Codex/Claude launchers.

Den's supported operator path is Den web plus Pi/conductor runs, task-thread
messages, review workflow records, agent-stream ops, and AgentRun state. Claude
Code, Codex CLI, and similar agents may still connect to Den MCP manually when a
human launches them, but they should not be treated as the default conductor or
as dispatch-injection targets.

## MCP Server Configuration

Prefer the user's/global MCP configuration for the agent runtime. Avoid random
per-project `.mcp.json` files unless the user explicitly wants a project-local
override, because project-local MCP files can shadow the working global config.

Current Den MCP endpoint:

```json
{
  "mcpServers": {
    "den": {
      "type": "http",
      "url": "http://localhost:5199/mcp"
    }
  }
}
```

## Suggested Manual Agent Guidance

If a manually launched Claude/Codex session uses Den MCP, include guidance like:

```markdown
## Project Management — den-mcp

This project uses a centralized den-mcp server for task management, agent
messaging, review workflow records, and document storage. The MCP server is
connected as "den".

- Project ID: `<directory-name>`
- Use Den as the durable record for task/thread/review updates.
- When updating tasks, always include your agent identity as the `agent`
  parameter.
- Check unread task-thread messages at the start of a work session.
- Use task-thread messages and structured review packets for handoffs; do not
  rely on dispatches unless a task explicitly says it is debugging legacy bridge
  behavior.
- Cross-project docs and global conventions are under project `_global`.
```

Agent identity strings such as `claude-code`, `codex`, `omp`, or `kimi-code` are
still acceptable as audit identities for manually launched agents. They are not
routing instructions for a supported Codex/Claude bridge.
