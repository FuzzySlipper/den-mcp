## Project Overview

den-mcp is a centralized MCP server plus CLI for task management, agent messaging, and document storage across multiple projects. It replaces task-master-ai with a custom solution.

Tasks for this repo are tracked in Den under project ID `den-mcp`.

## Architecture

```
src/DenMcp.Core/       — Models, SQLite DB, repositories (no framework deps)
src/DenMcp.Server/     — ASP.NET Core MCP + REST server
src/DenMcp.Cli/        — Terminal.Gui dashboard + CLI commands
tests/DenMcp.Core.Tests/   — Integration tests
tests/Architecture.Tests/  — Dependency boundary enforcement
```

## Build & Test

```bash
dotnet build den-mcp.slnx
dotnet test den-mcp.slnx
```

## Run the Server

```bash
dotnet run --project src/DenMcp.Server
# Override port: dotnet run --project src/DenMcp.Server -- --port 5200
# Override DB:   dotnet run --project src/DenMcp.Server -- --db-path /tmp/test.db
```

Default: `http://localhost:5199`, DB at `~/.den-mcp/den.db`

## Run the CLI

```bash
dotnet run --project src/DenMcp.Cli -- <command> [options]
# Examples:
dotnet run --project src/DenMcp.Cli -- tasks --project den-mcp
dotnet run --project src/DenMcp.Cli -- next --project den-mcp
dotnet run --project src/DenMcp.Cli -- dashboard
```

## Conventions

- No AI processing in the server — agents create well-formed tasks directly.
- All SQL is parameterized (no string interpolation).
- Explicit DI registration (no auto-scanning).
- Core has no dependency on ASP.NET or Terminal.Gui.
- JSON uses snake_case naming.
