## Agent Guidance Source of Truth

Den-native agent guidance is the preferred live source for global and project policy.

When Den guidance tooling is available:
- Resolve current guidance for the project with the Den MCP `get_agent_guidance` tool or `den guidance` CLI command.
- Treat the resolved Den guidance packet and its referenced Den documents as the source of truth.
- Do not edit generated or bootstrap `AGENTS.md` snapshots for policy changes; update the Den guidance documents or first-class guidance entries instead.
- Keep generated `AGENTS.md` snapshots as compatibility/export artifacts for tools that cannot yet load Den-resolved guidance directly.
