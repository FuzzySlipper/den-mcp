## Agent Expectations

- If you are the intended recipient of `review_feedback`, `merge_request`, or `planning_summary`, read the full thread or structured context before acting.
- If work is handed off between agents, keep the contract in Den tasks/messages rather than tool-specific side channels.
- Helper scripts, generic notifications, terminal wrappers, or explicitly maintained local adapters may assist workflow, but they are not the primary record. Retired bridges such as Signal/Telegram mobile bridges and Codex/Claude dispatch wrappers should be treated as legacy context only unless a task explicitly reintroduces a supported adapter.
- Before idling or declaring a slice complete, check Den again for unread task-thread messages, relevant agent-stream/attention items, and the next unblocked task so work drains promptly. Do not treat dispatches as a normal queue unless a legacy bridge/debug task explicitly says to.
