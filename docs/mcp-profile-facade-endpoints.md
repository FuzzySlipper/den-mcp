# Den MCP profile facade endpoints

`den-mcp` adapter mode preserves the full-compatible MCP endpoint:

```text
/mcp
```

For Den-controlled Hermes profiles, the adapter also exposes config-friendly profile URLs that proxy to Den Core's authoritative MCP endpoint with a request-scoped `tool_profile` selector:

```text
/mcp/profiles/planner
/mcp/profiles/runner
/mcp/profiles/worker-coder
/mcp/profiles/worker-reviewer
/mcp/profiles/admin-current
/mcp/profiles/legacy-full
```

These facade URLs are conveniences only. Den Core owns profile/bundle metadata, `tools/list` filtering, and `tools/call` enforcement.

Forwarding behavior:

- `/mcp` is forwarded unchanged and remains full-compatible for migration and archive/debug clients.
- `/mcp?tool_profile=planner` and `/mcp?tool_bundles=core-read,review` are forwarded unchanged.
- `/mcp/profiles/<profile>` is rewritten to Core `/mcp?tool_profile=<profile>`.
- `/mcp/profiles/<profile>/<subpath>` is rewritten to Core `/mcp/<subpath>?tool_profile=<profile>`.
- Existing query parameters are preserved, except a conflicting `tool_profile` query on a profile URL is replaced by the profile URL selector.

Normal daily Hermes profiles should use `planner`, `runner`, worker, or `admin-current` URLs. `legacy-full` is explicit break-glass/archive access and should not be used as a normal profile endpoint.
