# Den Pi Worker Orchestrator State Machine

Task: #1244 — validator/drift worker roles and explicit orchestrator decisions.

## Worker roles

The Pi worker lifecycle accepts these role profiles through Den MCP worker tools:

- `coder` — implementation worker that posts an `implementation_packet`.
- `reviewer` — independent review worker that posts a `review_findings_packet` and must be backed by Den review-round/finding/verdict state.
- `validator` — deterministic build/test/lint worker that posts a `validation_packet` with exact commands/results.
- `drift_checker` — scope-drift worker that posts a `drift_check_packet` comparing task intent, packet claims, diff/repo metadata, and review state.
- `packet_auditor` — claim-audit worker that posts a `packet_audit_packet` checking packet claims against Den state and repo branch/head metadata.

`drift_sentinel` remains accepted as a compatibility alias and normalizes to `drift_checker`.

## Packet and completion contract

Launch tools prepare bounded context packets as Den task-thread messages and pass only packet references into the worker runtime. Completion is not inferred from process exit: the orchestrator reads structured Den completion packets and Den review records.

New context-packet tools:

- `prepare_validator_context_packet`
- `prepare_drift_checker_context_packet`
- `prepare_packet_auditor_context_packet`

New role launch tools:

- `launch_validator_worker`
- `launch_drift_checker_worker`
- `launch_packet_auditor_worker`

Accepted completion packet types now include:

- `validation_packet`
- `drift_check_packet`
- `packet_audit_packet`

## State transition order

`determine_orchestrator_next_action` evaluates Den state in this order and fails closed when required evidence is missing or inconsistent:

1. If task is `blocked`, `done`, or `cancelled` → hold.
2. If no completed implementation packet exists → launch coder.
3. If implementation packet is missing branch/head commit → escalate; repo identity is required.
4. If implementation has no test report or explicit skip/recovery rationale → launch validator.
5. If no validation packet for the implementation head → launch validator.
6. If validation failed or mismatches implementation head → return to coder or escalate after retry cap.
7. If no drift check for the implementation head → launch drift checker.
8. If drift check failed or mismatches implementation head → return to coder or escalate after retry cap.
9. If no packet audit for the implementation head → launch packet auditor.
10. If packet audit failed or mismatches implementation head → escalate.
11. If no Den review round exists → request review.
12. If latest Den review round head mismatches implementation head → request a new review.
13. If no reviewer completion packet exists for the round/head → launch reviewer.
14. If reviewer completion does not reference the latest Den review round/head → escalate.
15. If reviewer packet exists but the Den review verdict is missing → escalate; freeform packet text is insufficient.
16. If verdict is `changes_requested` or unresolved findings remain → launch coder or escalate after retry cap.
17. If verdict is `blocked_by_dependency` → ask user/planner.
18. If verdict is `follow_up_needed` → triage follow-ups.
19. If verdict is `looks_good` and validation/drift/audit all match the implementation head → ready for done/merge decision.

## Fail-closed rules

- Process exit alone is never success.
- Review completion must correlate with a real Den review round id and head commit.
- Den review verdict is required; `review_findings_packet` text alone does not advance the loop.
- Validation, drift, and packet audit packets must match the implementation head commit.
- Missing branch/head/test/review metadata escalates or launches deterministic validation instead of silently proceeding.
- Retry caps prevent infinite loops and force human/planner escalation after repeated failures.
