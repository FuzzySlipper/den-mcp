# den-mcp

Den MCP is the shared control plane for multi-agent software projects. It provides a centralized MCP/REST server, CLI/dashboard, Den web/operator workflow support, and APIs for tasks, messages, review loops, agent-stream observability, project documents, and desktop/operator clients.

Tasks for this repository are tracked in Den under project ID `den-mcp`.

## What It Provides

- **Task management** — hierarchical tasks, dependencies, priority/status workflow, audit history, and smart next-task selection.
- **Task-thread messages** — durable project/task/thread communication with per-agent read state.
- **Review workflow** — review rounds, structured findings, verdicts, merge handoffs, and follow-up splitting.
- **Agent stream / run records** — operational visibility for orchestrators, sub-agents, drift checks, validation, and targeted nudges.
- **Document storage** — Markdown docs (PRDs, specs, ADRs, conventions, references) with SQLite FTS5 search.
- **Shared blackboard** — cross-project scratch/handoff Markdown entries with optional idle TTL.
- **MCP + REST APIs** — MCP tools for agents and REST endpoints for CLI/web/desktop clients.
- **CLI + TUI dashboard** — terminal commands and a Terminal.Gui dashboard.
- **Desktop/operator APIs** — REST/MCP surfaces consumed by the standalone `den-desktop` app.

## Repository Layout

```text
src/DenMcp.Core/                  Models, SQLite repositories, domain services
src/DenMcp.Server/                ASP.NET Core MCP + REST server
src/DenMcp.Cli/                   CLI commands + Terminal.Gui dashboard

tests/DenMcp.Core.Tests/          Core integration tests
tests/DenMcp.Server.Tests/        Server/API tests
tests/Architecture.Tests/         Dependency boundary tests
```

## Quick Start

```bash
dotnet build den-mcp.slnx
dotnet test den-mcp.slnx
```

Run the server:

```bash
dotnet run --project src/DenMcp.Server
```

Default server URL: `http://localhost:5199`
Default database: `~/.den-mcp/den.db`

Override defaults:

```bash
dotnet run --project src/DenMcp.Server -- --port 5200
dotnet run --project src/DenMcp.Server -- --db-path /tmp/den-mcp/dev.db
```

## CLI

```bash
# Alias for convenience
alias den='dotnet run --project /path/to/den-mcp/src/DenMcp.Cli --'

den projects
den tasks --project den-mcp
den task 1154 --project den-mcp
den next --project den-mcp
den create-task --title "Build X" --project den-mcp
den status 1154 done --project den-mcp
den messages --project den-mcp
den send --content "Ready for review" --project den-mcp
den docs --project den-mcp
den doc project-bootstrap-guide --project den-mcp
den search "bridge submodule"
den blackboard list
den dashboard
```

The `--project` flag auto-detects from the current directory name when omitted.

### Dashboard

```bash
den dashboard
```

The dashboard shows projects, tasks, docs, and messages with keyboard-driven navigation. Dispatches are legacy/debug artifacts, not the normal work queue. Use `den dashboard --legacy-dispatches` only when explicitly inspecting legacy dispatch rows.

## Den Desktop

Den Desktop has moved to the standalone `den-desktop` repository. Do not add desktop UI, sidecar runtime, Electron packaging, or desktop-only fixtures back to this repo. Den MCP keeps the server-side REST/MCP API surface that desktop clients consume.

## Validation Commands

General validation:

```bash
dotnet build den-mcp.slnx
dotnet test den-mcp.slnx
```

Desktop UI/sidecar and bridge validation now happen in the standalone `den-desktop` repository.

## Architecture And Boundary Rules

- `DenMcp.Core` has no dependency on ASP.NET, Terminal.Gui, or desktop UI packages.
- `DenMcp.Server` references Core and hosts MCP + REST endpoints.
- `DenMcp.Cli` communicates through the API/CLI layer and does not become server infrastructure.
- Desktop UI and sidecar code live in the standalone `den-desktop` repository; keep this repo focused on Den core/server/CLI surfaces.
- SQL must be parameterized; do not interpolate SQL strings.
- Use explicit DI registration; do not add auto-scanning.
- Den APIs use snake_case JSON. Bridge protocol DTOs may intentionally use their protocol contract naming.

## MCP Usage

MCP endpoint:

```text
http://localhost:5199/mcp
```

Example MCP config:

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

For manually launched agents, prefer user/global MCP configuration over ad-hoc project `.mcp.json` files so local config does not shadow the working global endpoint.

## Deployment

A systemd service file is provided at `deploy/den-mcp.service`.

```bash
dotnet publish src/DenMcp.Server -c Release -o /opt/den-mcp
sudo cp deploy/den-mcp.service /etc/systemd/system/
sudo systemctl enable --now den-mcp
```

Signal/Telegram mobile bridge integrations and legacy kitty helpers are retired. Current operator workflows use Den web, Den Desktop, Pi/orchestrator runs, task-thread messages, review records, agent-stream ops, and AgentRun state.

## License

MIT


