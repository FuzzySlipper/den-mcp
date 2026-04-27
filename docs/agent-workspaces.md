# Agent Workspaces

Agent workspaces are the minimal durable backend record for conductor work that spans a task branch, worktree, run, diff, preview, and review lifecycle.

This slice intentionally adds only the backend record, repository, REST surface, tests, and invariants. It does **not** create git worktrees, spawn terminals, proxy previews, or manage desktop/terminal UI.

## Model

`AgentWorkspace` records live in `agent_workspaces` and use snake_case JSON over REST.

Required fields:

- `id` — stable workspace id. REST creation may generate `ws_<guid>` when omitted.
- `project_id` — Den project scope.
- `task_id` — task the workspace is for.
- `branch` — task branch or other branch under conductor control.
- `worktree_path` — local path expected to contain the working tree.
- `base_branch` — intended diff/merge base branch.
- `state` — one of `planned`, `active`, `review`, `complete`, `failed`, `archived`.
- `cleanup_policy` — one of `keep`, `delete_worktree`, `archive`.
- `created_at`, `updated_at`.

Optional fields:

- `base_commit` — known base commit for review/diff anchoring.
- `head_commit` — current known branch head.
- `created_by_run_id` — directional link to `agent_runs.run_id` when a sub-agent/run created the workspace.
- `dev_server_url` — future adapter/server URL.
- `preview_url` — future browser preview URL.
- `changed_file_summary` — compact JSON summary for later UI/runtime projections.

## REST API

Project-scoped endpoints:

- `GET /api/projects/{projectId}/agent-workspaces?taskId=<id>&state=<state>&limit=<n>`
- `GET /api/projects/{projectId}/agent-workspaces/{workspaceId}`
- `POST /api/projects/{projectId}/agent-workspaces`
- `PUT /api/projects/{projectId}/agent-workspaces/{workspaceId}`

Global/read-only convenience endpoints:

- `GET /api/agent-workspaces?projectId=<project>&taskId=<id>&state=<state>&limit=<n>`
- `GET /api/agent-workspaces/{workspaceId}?projectId=<project>`

Create/upsert request body:

```json
{
  "id": "ws_809",
  "task_id": 809,
  "branch": "task/809-agent-workspace-backend",
  "worktree_path": "/home/patch/dev/den-mcp",
  "base_branch": "main",
  "base_commit": "d502319ce49c964e82c639f4913972442b191e2c",
  "head_commit": "...",
  "state": "active",
  "created_by_run_id": "optional-run-id",
  "dev_server_url": "http://localhost:5199",
  "preview_url": "http://localhost:5199",
  "cleanup_policy": "keep",
  "changed_file_summary": {
    "files": [
      { "path": "src/DenMcp.Core/Data/AgentWorkspaceRepository.cs", "status": "modified" }
    ],
    "counts": { "modified": 1 }
  }
}
```

## Initial invariants

- One workspace is canonical for a `(project_id, task_id, branch)` tuple.
- The repository/API reject workspaces whose `task_id` belongs to a different project than `project_id`.
- Upserting the same tuple updates the existing row and preserves its id.
- `id` remains the direct lookup key for later adapters and web views.
- `created_by_run_id` is optional and points toward `agent_runs`; workspaces are mergeable without AgentRun lifecycle automation.
- `changed_file_summary` must remain compact (currently <= 12,000 serialized characters at the REST boundary).
- The backend does not infer git state; callers provide `base_commit`, `head_commit`, and changed-file summaries when they have them.
- Cleanup policy is declarative only in this slice. No worktree deletion, archiving, or process cleanup happens here.

## Non-goals in this slice

- Terminal view registry.
- Desktop companion integration.
- Preview proxy.
- Automatic branch/worktree creation.
- Git diff scanning or changed-file computation.
