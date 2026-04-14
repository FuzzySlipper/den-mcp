# Agent Workflow Guidance

This note captures the workflow guidance we want coder and reviewer prompts to
reinforce during normal den-mcp task loops.

## Default posture

- If the current plan still fits reality and the path is straightforward, keep
  working until the current slice is complete.
- Needing to create or update Den tasks is normal. Tasks are cheap and do not
  require special approval.

## Stop And Ask

Stop and ask for human guidance before continuing when any of these are true:

- reality materially conflicts with the plan
- the plan is too vague to implement confidently without inventing major
  details
- scope needs to expand in a non-obvious way
- repeated failed attempts suggest the assumptions or plan are wrong
- you are inventing a complex workaround or compatibility shim mainly to cope
  with local mess instead of solving the task cleanly

## Task Hygiene

- Do not land thin interfaces or deceptive scaffolding that make the task look
  complete while the real behavior is still unwired.
- If proper implementation does not fit the current scope, create a follow-up
  Den task with clearer planning instead of landing fake completeness.
- Do not leave workflow TODOs in the codebase. If work matters, track it as a
  Den task so it cannot get lost.

## Reviewer Expectations

- Prioritize correctness, regressions, scope drift, and workflow hygiene.
- Call out thin interfaces, unwired scaffolding, and code TODOs when they are
  being used as placeholders for missing tracked work.
- Say so explicitly when an implementation drifted into complex local
  workarounds or other signs that the coder should have stopped and asked for
  guidance.

## Shipped Surfaces

- `PromptGenerationService` includes these guardrails in planning and
  review-feedback prompts for implementers, plus review prompts for reviewers.
- `ReviewWorkflowService` includes the same stop-and-ask and task-hygiene rules
  in the structured changes-requested handoff packet.
- The reusable agent-instructions snippet in `docs/project-specs.md` carries a
  concise version of the same rules into project-local `CLAUDE.md` or
  equivalent files.
