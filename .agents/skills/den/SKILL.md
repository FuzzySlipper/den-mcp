---
name: den
description: Check Den for queued work, or fetch the structured handoff context for a specific dispatch ID and continue from Den as the source of truth.
---

# Den

Use this skill as a manual inbox refresh when the agent should check Den for queued work, or as a targeted wake-up when a wrapper pastes `den <dispatch_id>` into an active session.

## Invocation forms

- `den`
  - refresh the inbox for the current project and agent
- `den <dispatch_id>`
  - fetch the structured handoff context for that dispatch with `get_dispatch_context`
  - use that payload, not pasted prose, as the source of truth

## Workflow

1. Identify the current project and agent identity from the working repo and session context.
2. If the user message is `den <dispatch_id>`, call `get_dispatch_context` for that dispatch ID first.
3. When a targeted dispatch context is available, use it as the primary handoff record.
4. Otherwise check Den for pending or approved dispatches targeted at the current agent in that project.
5. Check for unread task-thread messages for the current agent.
6. If a dispatch or unread message points at a task thread, read the relevant task or thread before acting so the handoff record, not terminal side channels, is the source of truth.
7. Prefer the most specific queued work first:
- `review_feedback`
- `merge_request`
- `review_request`
- `planning_summary`
- other explicit task-thread messages
8. If there is no queued handoff, check for the next unblocked task only when it makes sense for the current workflow.
9. After acting, update Den clearly:
- mark messages read
- complete any dispatch handled manually
- update task status only when the work state really changed

## Output

Report:
- what queued work was found
- what dispatch context, thread, or task you used as source of truth
- what action you took next

If nothing is queued, say that explicitly instead of inventing work.
