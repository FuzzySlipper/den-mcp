## Task Management

Use Den tasks and task-thread messages as the shared record. Agent-stream ops and run records are the normal attention/observability layer. Dispatches are legacy bridge artifacts, not a normal work queue or second source of truth.

Before substantial work:
- Check unread task-thread messages and relevant agent-stream/attention items.
- Check the current task status if you are working from a task.
- Ignore pending dispatches during normal startup/drain loops unless a user explicitly asks you to debug a legacy bridge or dispatch row.

For Den state, prefer Den tools over shell. Shell, `curl`, database searches, and source-code inspection are fallback paths only after the relevant Den read tool is missing or fails.

When using Den task-thread handoffs:
- Prefer task-thread messages over project-wide chat.
- Use `metadata.type` for structured handoffs such as `planning_summary`, `review_request`, `review_feedback`, and `merge_request`.
- Do not invent new `intent` values when an existing canonical family already fits; keep `intent` aligned with `handoff`, `review_request`, `review_feedback`, and similar built-in families.
- When a message is intended to wake or redirect a specific agent, make the recipient explicit in metadata and keep the real context in the task/thread instead of relying on generated dispatch prompt text.

Suggested targeted metadata examples:
- `{"type":"review_request","recipient":"<agent>"}`
- `{"type":"review_feedback","recipient":"<agent>"}`
- `{"type":"merge_request","recipient":"<agent>"}`
- `{"type":"planning_summary","recipient":"<agent>"}`
