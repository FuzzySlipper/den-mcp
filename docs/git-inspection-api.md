# Git Inspection API

Date: 2026-04-27

This is the first backend slice for [doc: den-mcp/git-working-tree-observability-plan]. It exposes read-only git status and diff inspection for Den-registered project roots.

## Scope

- Operates only on a project's registered `root_path`.
- Does not merge, push, checkout, clean, delete, or mutate git state.
- Uses `git` through `ProcessStartInfo.ArgumentList` rather than shell snippets.
- Returns structured warnings/errors for non-git roots, missing refs, detached heads, no upstream, invalid paths, timeouts, and truncated output.

Agent workspace-specific routes are intentionally left for the next slice.

## Endpoints

```http
GET /api/projects/{projectId}/git/status
GET /api/projects/{projectId}/git/files?baseRef=<ref>&headRef=<ref>&includeUntracked=true
GET /api/projects/{projectId}/git/diff?path=<relative-path>&baseRef=<ref>&headRef=<ref>&maxBytes=<n>&staged=false
```

`status` returns branch/head/upstream/ahead-behind metadata plus a parsed file list from `git status --porcelain=v2 --branch --untracked-files=all`.

`files` returns the status file list when no range is supplied. When `baseRef` and `headRef` are both supplied it uses `git diff --name-status --find-renames <baseRef>...<headRef>` and can append untracked files from the working tree.

`diff` returns a bounded unified diff. With both `baseRef` and `headRef`, it inspects that review range. Without refs, it inspects the current unstaged working tree, or the staged/index diff when `staged=true`. `path` is optional but, when present, must resolve under the registered project root.

## Guardrails

- `path` must be relative and may not escape the project root.
- Refs reject obviously unsafe shell/ref syntax before invoking git.
- Output is bounded; responses include `truncated: true` and a warning when output exceeds the cap.
- Git command timeouts are returned as response warnings/errors rather than server crashes.
