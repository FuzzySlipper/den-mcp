# Pi Den Extension Spike

Date: 2026-04-24
Task: `#757`

This spike starts the pivot from per-vendor live-session bridges toward one
project-facing Pi conductor with Den-backed state.

The first project-local extension lives at:

```text
.pi/extensions/den.ts
```

Pi auto-discovers project-local extensions from `.pi/extensions`, so starting
`pi` in this repo should load the extension.

## Goals for this slice

- Bind a running Pi session to Den as one project-level conductor.
- Keep Den as the durable source of tasks, messages, dispatches, stream entries,
  and review records.
- Expose a tiny Den command/tool surface inside Pi without replacing MCP or the
  existing server APIs.
- Keep the implementation reversible while the Pi workflow proves itself.

## Current behavior

On `session_start`, the extension:

- infers the Den project from `DEN_PI_PROJECT_ID` or the basename of Pi's
  current working directory
- calls `/api/agents/checkin`
- registers an `agent_instance_binding` with:
  - `agent_family`: `pi`
  - `agent_identity`: `pi` by default
  - `role`: `conductor` by default
  - `transport_kind`: `pi_extension`
- starts a heartbeat loop against `/api/agents/heartbeat`
- checks out on `session_shutdown`

It also updates the binding metadata on Pi agent start/end with a lightweight
`state` value of `busy` or `idle`.

## Commands

```text
/den-status
/den-inbox
/den-next [assigned_to]
/den-claim-next [assigned_to]
/den-task <task_id>
/den-note [task_id] <text>
/den-done [task_id] [note]
/den-blocked [task_id] <reason>
/den-mark-read <message_id> [message_id...]
/den-complete-dispatch <dispatch_id>
```

`/den-inbox` summarizes:

- approved dispatches for the configured Pi agent identity
- unread task-thread messages for that identity
- targeted agent-stream messages for the current instance, role, or agent
- the next unblocked task for the project

## Model-callable tools

```text
den_get_task
den_next_task
den_inbox
den_claim_next_task
den_update_task
den_send_message
den_mark_read
den_complete_dispatch
```

The write tools cover the minimum single-conductor work loop: claim a task,
update task status/assignee fields, post task-thread messages, clear read
state, and complete consumed dispatches. Review packet helpers and sub-agent run
records are still follow-up slices.

## Configuration

Environment variables:

```text
DEN_MCP_URL             default http://192.168.1.10:5199
DEN_MCP_BASE_URL        fallback if DEN_MCP_URL is unset
DEN_PI_PROJECT_ID       optional explicit project id; defaults to cwd basename
DEN_PI_AGENT            default pi
DEN_PI_ROLE             default conductor
DEN_PI_INSTANCE_ID      optional stable instance id
```

For compatibility with existing Codex-targeted dispatches during migration,
launching with `DEN_PI_AGENT=codex` lets the Pi conductor see work currently
targeted at `codex`.

## Run

Start Den:

```bash
dotnet run --project src/DenMcp.Server
```

In another terminal, start Pi from this repo:

```bash
pi
```

Then try:

```text
/den-status
/den-inbox
/den-next
/den-claim-next
```

## Conductor direction

The intended next shape is:

- one user-facing, durable Pi conductor per project
- implementation and review work run as bounded sub-agent sessions
- reviewer sessions use fresh context and a different provider/model
- Den task messages and review rounds stay the source of truth
- the conductor reads coder/reviewer communication for intent drift, not as a
  second code reviewer
- user escalation happens through Den task-thread questions and targeted stream
  entries when the conductor detects a decision outside agent authority

The important distinction is user-facing conductor versus non-user-facing
sub-agent run, not "coder terminal" versus "reviewer terminal."

## Open follow-ups

- Add a Pi sub-agent runner command or tool backed by Pi RPC/SDK.
- Persist sub-agent session IDs and run metadata, initially in stream ops
  entries and later possibly in an `agent_runs` table.
- Add conductor prompts for drift detection using task intent, acceptance
  criteria, review findings, and coder responses.
- Decide whether the long-term Den agent identity should be `pi`, `conductor`,
  or project-configurable per repo.
