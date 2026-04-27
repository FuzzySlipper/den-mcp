## Project Bootstrap

Project ID: `den-mcp`

Live project guidance lives in Den at `[doc: den-mcp/project-bootstrap-guide]`
and is included by `get_agent_guidance(project_id="den-mcp")`.

This repository hosts the Den MCP server. If Den is unavailable, the task is to
restore Den or ask the user for direction, not to work around Den state from
local files.

Useful local commands for restoring or validating Den:

```bash
dotnet build den-mcp.slnx
dotnet test den-mcp.slnx
dotnet run --project src/DenMcp.Server
```
