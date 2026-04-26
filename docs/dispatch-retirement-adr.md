# ADR: Retire dispatches from the canonical conductor workflow

Status: Accepted  
Date: 2026-04-26

## Context

Dispatches were introduced as a durable wake/delivery queue for bridges such as Signal, Telegram, and early agent-wrapper experiments. They worked as a compatibility layer, but they also created a second apparent work lane next to task-thread messages, review workflow records, agent-stream ops, and sub-agent run state.

That ambiguity confused both operators and agents: pending dispatches looked like normal work even when the task thread already contained the real handoff, and routine targeted messages could create approval/reject prompts that competed with the current conductor workflow.

## Decision

Dispatches are retired from the canonical Den conductor path.

The normal workflow is now:

1. Tasks and task-thread messages hold the durable work contract.
2. Review workflow tables/messages hold review state.
3. Agent-stream ops and run records provide attention, lifecycle, and observability.
4. Future first-class attention items should extend that path rather than revive dispatches as a normal queue.

Dispatch rows and APIs remain for historical data and legacy bridges, but automatic dispatch creation is disabled unless a project explicitly opts into legacy dispatch routing with `defaults.legacy_dispatch_enabled: true` in its `dispatch-routing` document.

## Consequences

- Agents should not check pending dispatches during normal startup or drain loops.
- Operator UI should not present dispatch approval/reject as a default main-path control.
- Existing dispatch rows are preserved and can still be inspected for debugging or bridge compatibility.
- Legacy bridge deployments that still consume dispatches must opt in deliberately through routing config and should be treated as compatibility mode, not the default Den workflow.
