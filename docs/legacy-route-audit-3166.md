# Task 3166 MCP legacy route audit

Date: 2026-06-22

## Scope

Audited the den-mcp MCP adapter/facade repository for active assumptions on legacy den-channels URLs, port `18081`, direct-agent compatibility routes, channel subscription/cursor routes, Gateway-shaped `/api/gateway/*` wake/message routes, and broad Gateway catch-all behavior.

## Result

No active den-mcp runtime/tool code or docs were found requiring legacy den-channels or Gateway catch-all routes for normal operation.

The current den-mcp repo remains a thin MCP-facing facade over Den Core APIs. The searched legacy route/config strings did not appear in the working tree, so no runtime code changes were needed for this task.

## Sweep patterns

The active repository was swept for:

```text
/den-core-api
den-channels
DEN_CHANNELS
18081
/api/direct-agent-events
/api/channel-subscriptions
/api/channel-subscription-cursors
/api/gateway
Gateway catch-all
gateway catch-all
channel-subscription
channel-messages
channel-memberships
```

Sweep result before this audit note was added: `0` matches.

## Follow-ups

No follow-up implementation task is required from the den-mcp repo audit. If future MCP tool docs reintroduce channel/transcript or executable wake examples, route them explicitly to the owning successor surface:

- Core MCP tools for durable project/task messages, tasks, documents, workflow packets, guidance, librarian/search, and user notifications.
- Delivery successor `/v1/delivery/...` for executable agent wake intent lifecycle.
- Conversation successor `/v1/conversation/...` for channel/transcript rows.
- Observation/Timeline successors for progress/activity/read models.
