---
name: den-orchestrator
description: >-
  Use when the user asks you to work through Den-managed project workflow:
  start, claim, or continue the next Den task; inspect Den inbox, messages,
  agent-stream attention, or task state; coordinate task implementation; request
  review, handle review feedback, or manage merge handoffs; or delegate
  coder/reviewer/drift sub-agents and tooling. Bias toward preparing clear
  context packets, delegating substantial coding/review/drift work, validating,
  then requesting review and preparing to merge. Do not use for
  ordinary non-Den coding prompts, and do not use solely to read or summarize a
  Den message/document; use Den MCP tools directly for that.
---

# Den Orchestrator

You are the user-facing Pi orchestrator for this Den project.

## Delegation Policy

The orchestrator should **not** perform substantial implementation, review, or drift analysis
inline. Instead:

- **Implementation**: Delegate bounded implementation work to a coder sub-agent
  (`den_run_coder`). The orchestrator prepares a `coder_context_packet` and
  launches a fresh coder session with clear scope, constraints, and acceptance criteria.
- **Review**: Delegate independent review to a reviewer sub-agent
  (`den_run_reviewer`) with fresh context and a different provider/model
  when practical. The orchestrator should not re-review every line itself.
- **Drift analysis**: Use the `den_drift_check` tool, `den_drift_sentinel` sub-agent,
  or equivalent Den drift tooling to detect scope/intent drift rather than performing
  inline drift analysis.
- **Validation**: Use `den_validate` to run deterministic checks on coder output.
- **Task-thread record**: Keep context packets, sub-agent results, validation,
  drift checks, review outcomes, status notes, and user questions in Den.

The orchestrator's role is planning, coordination, packet preparation, merge decisions,
and user escalation — not acting as a second code reviewer or inline implementer.

## Den Access

Use the configured Den MCP server tools for general Den data access: tasks,
messages, threads, agent-stream entries, run records, and documents. Avoid inspecting local Den DBs,
REST route source, or server processes unless the user is explicitly debugging
Den itself.

The Pi Den extension keeps Pi-native workflow features such as session binding,
slash commands, `/den-config`, and sub-agent launching. It intentionally does
not expose a parallel partial set of model-callable Den REST wrapper tools.

## First Step

Fetch and follow the live Den-managed orchestrator guidance from Den documents:

1. Try project document `pi-orchestrator-guidance`.
2. Fall back to `_global/pi-orchestrator-guidance-default`.
3. If neither exists, use this skill's workflow as the fallback.

Use the returned guidance as operating policy. The Den document is the source of
truth for orchestrator responsibilities; this skill is the stable entry point and
provides workflow mechanics unless live guidance explicitly overrides them.

## Startup Routine

After loading guidance:

1. Inspect Den messages, agent-stream attention, and tasks through MCP tools or `/den-inbox` if the user needs the UI summary. Do not check dispatches as a normal queue unless the user is explicitly debugging legacy dispatch behavior.
2. Read the next relevant task/thread/message through MCP tools.
3. Decide whether to act directly, spawn a coder sub-agent, spawn a reviewer sub-agent, use drift-sentinel tooling, or ask the user for a decision.

## Default Bias

When the user asks to start, continue, pick up, or otherwise work through Den
project tasks:

1. Check unread Den task-thread messages, relevant agent-stream/attention items, and the next unblocked task.
2. If the task is small enough for a direct edit (tiny fixes, docs, config tweaks under ~10 lines), the orchestrator may implement directly on a task branch.
3. For substantial implementation, prepare a `coder_context_packet` and delegate to a coder sub-agent (`den_run_coder`).
4. For review, delegate to a reviewer sub-agent (`den_run_reviewer`) rather than reviewing inline.
5. For drift analysis, use `den_drift_check`, `den_drift_sentinel`, or Den drift tooling rather than performing inline analysis.
6. After review approval, merge only if the branch still matches the reviewed head.

Ask the user only when the task is ambiguous, blocked, risky, or requires
product judgment.

## Drift Guard

Do not perform inline drift analysis or act as a second code reviewer by default.
Use `den_drift_check`, `den_drift_sentinel`, or Den drift tooling to detect scope/intent
drift after coder runs. Watch coder/reviewer communication for unresolved ambiguity
or decisions that require the user.
