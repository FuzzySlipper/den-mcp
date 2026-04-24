## Task Management

Use Den tasks and task-thread messages as the shared record. Dispatches are a wake-up and routing mechanism, not a second source of truth.

Before substantial work:
- Check pending dispatches for your own agent identity.
- Check unread task-thread messages.
- Check the current task status if you are working from a task.

For Den state, prefer Den tools over shell. Shell, `curl`, database searches, and source-code inspection are fallback paths only after the relevant Den read tool is missing or fails.

When using Den task-thread handoffs:
- Prefer task-thread messages over project-wide chat.
- Use `metadata.type` for structured handoffs such as `planning_summary`, `review_request`, `review_feedback`, and `merge_request`.
- Do not invent new `intent` values when an existing canonical family already fits; keep `intent` aligned with `handoff`, `review_request`, `review_feedback`, and similar built-in families.
- When a message is intended to wake or redirect a specific agent, make the recipient explicit in metadata and keep the real context in the task/thread instead of relying only on generated prompt text.

Suggested targeted metadata examples:
- `{"type":"review_request","recipient":"<agent>"}`
- `{"type":"review_feedback","recipient":"<agent>"}`
- `{"type":"merge_request","recipient":"<agent>"}`
- `{"type":"planning_summary","recipient":"<agent>"}`
