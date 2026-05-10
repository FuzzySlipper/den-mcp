# Den Pi Worker Observability and Cleanup Runbook

Task: #1245 — operator status and cleanup lifecycle for Hermes-launched Den Pi workers.

## Launch bootstrap

Worker launch now projects the bounded startup contract into the Pi Docker runtime environment instead of embedding large prompt bodies in argv. The Docker compose args remain limited to compose/run controls; worker context is available inside the container via environment keys:

- `DEN_WORKER_PROJECT_ID`
- `DEN_WORKER_TASK_ID`
- `DEN_WORKER_SESSION_ID`
- `DEN_WORKER_RUN_ID`
- `DEN_WORKER_ROLE`
- `DEN_WORKER_PROMPT_PACKET_MESSAGE_ID`
- `DEN_WORKER_STATE_FILE_REF`
- `DEN_WORKER_STARTUP_PROMPT`
- `DEN_WORKER_TIMEOUT_SECONDS`

`DEN_WORKER_STARTUP_PROMPT` is bounded to 4000 characters and should only reference the Den packet/state file. Full task context belongs in Den task-thread packets or state files, not process args.

## Status projection

Use `get_worker_run_status` for operator/orchestrator status. It returns:

- role, run id, session id, task id;
- runtime state and completion-packet state separately;
- tmux session/container/compose handles;
- state file and prompt packet references;
- latest completion packet summary, including final branch/head, tests reported, review round, failure category, and recovery guidance;
- safe output tail and attention state;
- cleanup state and diagnostics.

Important reconciliation rule: process/container exit is not success. A terminal runtime with no structured completion packet must fail closed in orchestration.

## Cleanup lifecycle

Use `cleanup_worker_run` for terminal sessions. It is idempotent:

- active sessions return `blocked / not_eligible_active` and should be terminated before cleanup;
- already cleaned sessions return `noop / cleaned_up`;
- terminal uncleaned sessions invoke Pi session cleanup and return the resulting cleanup state.

Cleanup state values exposed in projections:

- `not_eligible_active`
- `eligible`
- `cleanup_pending`
- `cleaned_up`

## Break-glass tmux attach

Automation should not drive tmux as the control API, but humans can inspect a session with the projection's attach info:

```bash
tmux attach-session -t <tmux_session_name>
```

Use this only for observation or break-glass intervention. Keep durable outcome in Den completion/review/status packets.

## Safe output previews

Status projections expose bounded `output_tail`, capture timestamp, truncation flag, and output hash where available. Do not expose raw reasoning files or credentials by default; completion packets and status summaries should redact secrets as `[REDACTED]`.
