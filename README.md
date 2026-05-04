# den-mcp

Den MCP is the shared control plane for multi-agent software projects. It provides a centralized MCP/REST server, CLI/dashboard, Den web/operator workflow support, and the Den Desktop operator app for tasks, messages, review loops, agent-stream observability, and project documents.

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
- **Den Desktop** — TypeScript/React/Electron operator UI backed by a .NET sidecar.

## Repository Layout

```text
src/DenMcp.Core/                  Models, SQLite repositories, domain services
src/DenMcp.Server/                ASP.NET Core MCP + REST server
src/DenMcp.Cli/                   CLI commands + Terminal.Gui dashboard
src/DenMcp.Desktop/               Den Desktop TypeScript/React/Electron UI
src/DenMcp.Desktop.Sidecar/       Den-specific .NET desktop sidecar/app-core
external/den-bridge/              Git submodule: reusable generic bridge foundation

tests/DenMcp.Core.Tests/          Core integration tests
tests/DenMcp.Server.Tests/        Server/API tests
tests/DenMcp.Desktop.Sidecar.Tests/ Den Desktop sidecar tests
tests/Architecture.Tests/         Dependency boundary tests
external/den-bridge/tests/Den.Bridge.Tests/ Generic bridge tests
```

## Den.Bridge Submodule

This repo consumes `Den.Bridge` from a git submodule:

```text
external/den-bridge -> git@github.com:FuzzySlipper/den-bridge.git
```

Initialize it after cloning:

```bash
git submodule update --init --recursive
```

Boundary split:

- **`Den.Bridge`** is generic bridge infrastructure: .NET abstractions, protocol frames, JSON/schema/registry, transports, host integration, and test harnesses.
- **Generic TS/web bridge code** should live in the same `den-bridge` repo under a package boundary such as `packages/den-bridge` when extracted.
- **`Den.Bridge.Electron`** should be an Electron-specific package boundary such as `packages/den-bridge-electron` in the same repo, unless it later needs an independent repo/release lifecycle.
- **`DenMcp.Desktop.Sidecar`** stays Den-specific: Den DTOs, Den API clients, task/message/document/session/terminal/app-agent handlers, settings, and runtime composition.
- **`DenMcp.Desktop`** stays the Den Desktop product UI; reusable TS helpers can be extracted later, but Den-specific protocol/API surfaces stay here.

See `docs/bridge-submodule-boundary.md` for the detailed boundary, test matrix, and submodule update workflow.

## Quick Start

```bash
git submodule update --init --recursive
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

Den Desktop is the local operator UI in `src/DenMcp.Desktop`.

Common commands:

```bash
npm --prefix src/DenMcp.Desktop install
npm --prefix src/DenMcp.Desktop run test:helpers
npm --prefix src/DenMcp.Desktop run ui:build
npm --prefix src/DenMcp.Desktop run electron:dev
```

The Electron shell launches `src/DenMcp.Desktop.Sidecar`, connects over the typed bridge, and exposes a constrained preload API to the renderer. The renderer should not receive raw Den tokens, endpoint internals, Node APIs, or shell access.

## Validation Commands

General validation:

```bash
git submodule status
dotnet build den-mcp.slnx
dotnet test den-mcp.slnx
```

Bridge / desktop validation:

```bash
dotnet test external/den-bridge/tests/Den.Bridge.Tests/Den.Bridge.Tests.csproj
dotnet test tests/DenMcp.Desktop.Sidecar.Tests/DenMcp.Desktop.Sidecar.Tests.csproj
npm --prefix src/DenMcp.Desktop run test:helpers
npm --prefix src/DenMcp.Desktop run ui:build
```

## Architecture And Boundary Rules

- `DenMcp.Core` has no dependency on ASP.NET, Terminal.Gui, or desktop UI packages.
- `DenMcp.Server` references Core and hosts MCP + REST endpoints.
- `DenMcp.Cli` communicates through the API/CLI layer and does not become server infrastructure.
- `DenMcp.Desktop.Sidecar` references `DenMcp.Core` and `Den.Bridge`, but must not reference Electron/Tauri/WebView packages.
- `external/den-bridge` must stay product-neutral and must not reference `DenMcp.*` assemblies or Den-specific fixtures/DTOs/command names.
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

Signal/Telegram mobile bridge integrations are retired. Current operator workflows use Den web, Den Desktop, Pi/orchestrator runs, task-thread messages, review records, agent-stream ops, and AgentRun state.

## License

MIT
