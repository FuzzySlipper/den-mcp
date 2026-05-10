# Space-compatible Librarian and Guidance

## Summary

Den librarian queries, document search, and agent guidance operate over any
Space ID, not just code projects. This includes `personal`, `assistant`,
`knowledge_base`, and `system` spaces, as well as the existing `project` kind.

No project-kind assumptions exist in the librarian, document, or guidance core.
The same `project_id` parameters used by existing APIs and MCP tools accept any
valid space ID.

## Librarian behavior

`query_librarian` gathers context for a space just as it does for a project:

- **Task context** — if a `task_id` is provided, the task and its
  parent/subtasks/dependencies/messages are included.
- **Documents** — FTS5 search runs against documents scoped to the requested
  space, plus optional `_global` documents when `include_global=true`.
- **Messages** — recent messages in the space are appended (lowest priority,
  truncated first if the token budget is exceeded).

Cross-space search is **not** automatic. A query scoped to `personal` does not
search `assistant` documents unless the caller explicitly queries each space.

## Document search behavior

`search_documents` and `list_documents` accept any space ID in the
`project_id` parameter. Omitting `project_id` searches/lists across all spaces.

## Guidance behavior

`get_agent_guidance` resolves guidance for any space:

1. `_global` entries are always included.
2. Space-local entries for the requested space ID are included.
3. Ordering follows the same deterministic rules as projects: `_global` first,
   then `sort_order`, then `required` before `important`.

This means a `personal` space can have its own guidance entries, and it still
inherits `_global` policy. Future work may add kind-scoped guidance (e.g.
`_global:project` vs `_global:assistant`), but that is not required for the
first space-compatible implementation.

## API and MCP compatibility

Existing routes and tools keep their `project_id` parameter names for backward
compatibility:

- `GET /api/projects/{projectId}/librarian/query`
- `GET /api/projects/{projectId}/agent-guidance`
- `GET /api/projects/{projectId}/documents/search`
- MCP `query_librarian(project_id, ...)`
- MCP `get_agent_guidance(project_id)`
- MCP `search_documents(project_id, ...)`

These parameters accept any space ID. Future aliases such as
`/api/spaces/{spaceId}/librarian/query` may be added, but the existing routes
remain fully functional.

## UI switching behavior

The Den web/operator view and Den Desktop expose a unified space/project switcher.
Selecting a space scopes task, message, document, guidance, librarian, and
collaboration views to that space ID where the existing compatibility APIs accept
`project_id` parameters.

Project-kind spaces remain the normal repo-backed workflow. Non-project spaces
are labeled with their `kind`, `visibility`, and root-path capability so they are
not presented as code projects unless they have a meaningful `root_path` or local
workspace snapshot.

In Den Desktop's left rail, clicking a project/space row selects that space. For
projects with multiple local workspaces, expansion is a separate adjacent chevron
control with an explicit expand/collapse label; selecting the row does not
implicitly expand or collapse workspace children.

Den Desktop includes hidden and archived spaces in switcher queries by default so
operators can see and distinguish their visibility labels. This is an explicit
operator policy controlled by `includeHiddenSpaces` and `includeArchivedSpaces` in
operator settings, not an authorization bypass; Den API/server authorization
remains responsible for deciding which spaces a caller may receive.

## Boundaries and non-goals

- **No automatic cross-space search.** Agents must query each space explicitly.
- **Git, terminal, review-branch, and workspace snapshot surfaces remain
  project/root-path oriented.** The UIs show this as an intentional boundary and
  avoid implying that personal, assistant, knowledge-base, or system spaces are
  repo-backed when they do not have a root path.
- **No guidance taxonomy redesign.** Space-kind-scoped guidance is deferred to
  future work.

## Testing

Core and server tests verify librarian and guidance behavior against
non-project spaces:

- `LibrarianGathererTests.Gather_NonProjectSpace_IncludesDocsTasksMessages`
- `LibrarianGathererTests.Gather_NonProjectSpace_WithGlobalDocs`
- `LibrarianServiceTests.QueryAsync_NonProjectSpace_ReturnsStructuredResponse`
- `AgentGuidanceRepositoryTests.Resolve_NonProjectSpace_CombinesGlobalAndSpaceLocalGuidance`
- `AgentGuidanceRepositoryTests.List_NonProjectSpace_ReturnsSpaceLocalEntries`
- `LibrarianServerTests.QueryRoute_NonProjectSpace_ReturnsSnakeCaseResponse`
- `AgentGuidanceApiTests.AgentGuidance_NonProjectSpace_ResolveAndList`
