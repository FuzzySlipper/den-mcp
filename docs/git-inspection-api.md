# Git Inspection API

Date: 2026-04-27

This backend surface supports [doc: den-mcp/git-working-tree-observability-plan]. It exposes read-only git status and diff inspection for Den-registered project roots and declared agent workspace paths.

## Scope

- Operates only on a project's registered `root_path` or an `agent_workspaces.worktree_path` resolved through a project-scoped workspace record.
- Does not merge, push, checkout, clean, delete, or mutate git state.
- Uses `git` through `ProcessStartInfo.ArgumentList` rather than shell snippets.
- Returns structured warnings/errors for non-git roots, missing refs, detached heads, no upstream, invalid paths, timeouts, and truncated output.

Workspace routes include stored workspace metadata and branch/head alignment warnings when live git state diverges from the workspace record.

## Endpoints

```http
GET /api/projects/{projectId}/git/status
GET /api/projects/{projectId}/git/files?baseRef=<ref>&headRef=<ref>&includeUntracked=true
GET /api/projects/{projectId}/git/diff?path=<relative-path>&baseRef=<ref>&headRef=<ref>&maxBytes=<n>&staged=false

GET /api/projects/{projectId}/agent-workspaces/{workspaceId}/git/status
GET /api/projects/{projectId}/agent-workspaces/{workspaceId}/git/files?baseRef=<ref>&headRef=<ref>&includeUntracked=true
GET /api/projects/{projectId}/agent-workspaces/{workspaceId}/git/diff?path=<relative-path>&baseRef=<ref>&headRef=<ref>&maxBytes=<n>&staged=false
```

`status` returns branch/head/upstream/ahead-behind metadata plus a parsed file list from `git status --porcelain=v2 --branch --untracked-files=all`.

`files` returns the status file list when no range is supplied. When `baseRef` and `headRef` are both supplied it uses `git diff --name-status --find-renames <baseRef>...<headRef>` and can append untracked files from the working tree.

`diff` returns a bounded unified diff. With both `baseRef` and `headRef`, it inspects that review range. Without refs, it inspects the current unstaged working tree, or the staged/index diff when `staged=true`. `path` is optional but, when present, must resolve under the inspected root.

Workspace routes reuse the same DTOs and include nullable `workspace_id`, `task_id`, `workspace_branch`, `workspace_base_branch`, `workspace_base_commit`, and `workspace_head_commit` fields. Workspace lookup is scoped to `{projectId}`; a workspace from another project returns `404`. Status/files/diff responses add warnings when the stored workspace branch or head commit differs from the live git checkout.

## Guardrails

- `path` must be relative and may not escape the project or workspace root.
- Refs reject obviously unsafe shell/ref syntax before invoking git.
- Output is bounded; responses include `truncated: true` and a warning when output exceeds the cap.
- Git command timeouts are returned as response warnings/errors rather than server crashes.
