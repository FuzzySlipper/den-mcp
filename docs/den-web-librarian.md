# Den Web Librarian

Task #855 adds a `Librarian` tab to the Den web main view. The tab is for human operator context queries without adding another always-visible panel to the dense dashboard.

## Query Controls

The Librarian tab provides:

- Query text for natural-language document/context questions.
- Scope selector:
  - `Current project (<project_id>)` queries the selected project.
  - `_global` queries the global document/context project directly.
- Optional task id. When provided, the librarian includes that task's detail, dependencies, subtasks, and recent task messages in gathered context. The task must belong to the selected query project.
- `Include _global docs` toggle. This maps to the existing librarian `include_global` option and is disabled when `_global` itself is the query scope.

The tab calls the existing REST endpoint:

```http
POST /api/projects/{projectId}/librarian/query
```

with snake_case body fields:

```json
{
  "query": "reasoning capture policy",
  "task_id": 854,
  "include_global": true
}
```

## Result Groups

Results are grouped by librarian item type:

- `Documents`
- `Tasks`
- `Messages`
- `Other`

Each result preserves the source id returned by the librarian, plus a normalized stable reference where Den can infer one.

## Stable References

Document references use the gathered-context syntax:

```text
[doc: project_id/slug]
```

Examples:

```text
[doc: den-mcp/pi-subagent-infrastructure]
[doc: _global/pi-conductor-guidance-default]
```

Task references render as `#<task_id>`, for example `#855`.

Message references render as `msg#<message_id>` and, when available from the source id, `thread#<thread_id>`. The web view can open `msg#` references through the message detail overlay; when `thread#` is present it can also open the thread directly.

## Navigation

Where practical, result cards expose existing Den web detail surfaces:

- Document results open `DocumentDetail` by project/slug.
- Task results open `TaskDetail` by task id and project.
- Message results open `MessageDetail` by project/message id, and can open a thread directly when the source id includes a thread id.

The Librarian tab does not create a new durable queue or modify Den state; it is a read/query surface over the existing librarian endpoint.
