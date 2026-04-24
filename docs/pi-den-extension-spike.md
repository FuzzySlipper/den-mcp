# Pi Den Extension Spike

Date: 2026-04-24
Task: `#757`

This spike starts the pivot from per-vendor live-session bridges toward one
project-facing Pi conductor with Den-backed state.

The first git-tracked Pi resources live at:

```text
pi-dev/extensions/den.ts
pi-dev/extensions/den-subagent.ts
pi-dev/skills/den-conductor/SKILL.md
```

Keep these resources outside project-local `.pi` discovery so they can be
installed into Pi once and reused across projects without double-loading when
Pi starts inside this repo.

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
/den-conductor-guidance
/den-run-subagent [--continue|--fork <session>|--session <session>] <role> <task_id|-> <prompt>
/den-run-coder [--continue|--fork <session>|--session <session>] <task_id> [extra notes]
/den-run-reviewer [--fork <session>|--session <session>] <task_id> [review target/notes]
/den-config
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
den_get_conductor_guidance
den_run_subagent
den_run_coder
den_run_reviewer
```

The write tools cover the minimum single-conductor work loop: claim a task,
update task status/assignee fields, post task-thread messages, clear read
state, and complete consumed dispatches.

`den_run_subagent` is the first sub-agent spike. It launches a fresh
`pi --mode json -p --no-session` process by default, records
`subagent_started` and `subagent_completed` ops entries, and posts the final
output back to the task thread when `task_id` is present.

Sub-agent session policy is explicit:

- `fresh`: default; best for reviewers, arbitration, and independent planning
- `continue`: reuse Pi's previous session; useful for ongoing coder work
- `fork`: fork a named session for follow-up while keeping the original intact
- `session`: resume a specific named session directly

It supports a single bounded run only; parallel fanout, worktree isolation,
review packet helpers, and richer run records are still follow-up slices.

`/den-config` opens a Pi TUI configuration menu. The initial menu supports
project-local and global sub-agent role defaults for `coder`, `reviewer`, and
`planner`. It lists models from Pi's model registry and saves provider-qualified
model IDs such as `openai-codex/gpt-5.5` or
`anthropic/claude-sonnet-4-6`, avoiding ambiguous unqualified model resolution.

`den_run_coder` and `den_run_reviewer` load prompt templates from Den documents
before launching the sub-agent:

- project document `pi-coder-subagent-prompt`, falling back to global
  `_global/pi-coder-subagent-prompt-default`
- project document `pi-reviewer-subagent-prompt`, falling back to global
  `_global/pi-reviewer-subagent-prompt-default`
- built-in fallback text if neither document exists

Templates use simple `{{placeholder}}` replacement for values such as
`{{project_id}}`, `{{task_id}}`, `{{task_title}}`, `{{task_description}}`,
`{{task_context}}`, `{{review_target}}`, and `{{extra_notes}}`.

The `den-conductor` Pi skill is the user/agent-invokable entry point for
conductor mode. It does not duplicate the policy text. It tells Pi to call
`den_get_conductor_guidance`, which resolves project document
`pi-conductor-guidance`, then `_global/pi-conductor-guidance-default`, then a
built-in fallback.

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

Sub-agent role defaults are read from JSON config with project-local values
overriding global values:

```text
.pi/den-config.json
~/.pi/agent/den-config.json
```

Example:

```json
{
  "version": 1,
  "subagents": {
    "coder": { "model": "openai-codex/gpt-5.5" },
    "reviewer": { "model": "anthropic/claude-sonnet-4-6" }
  }
}
```

Explicit `model` arguments on `den_run_subagent`, `den_run_coder`, or
`den_run_reviewer` still take precedence over config defaults. The project-local
file is gitignored because model choices are user/machine-specific.

For compatibility with existing Codex-targeted dispatches during migration,
launching with `DEN_PI_AGENT=codex` lets the Pi conductor see work currently
targeted at `codex`.

## Run

Install or link the tracked resources into Pi from this repo path:

```bash
pi install /home/patch/dev/den-mcp/pi-dev
```

If Pi is already loading that path from user settings, leave the project root
without a local `.pi` copy. The important bit is that `pi-dev` remains the
tracked source and Pi sees it through one discovery path.

Start Den:

```bash
dotnet run --project src/DenMcp.Server
```

In another terminal, start Pi from the target project directory:

```bash
pi
```

Then try:

```text
/den-status
/den-inbox
/den-next
/den-claim-next
/skill:den-conductor
/den-conductor-guidance
/den-config
/den-run-subagent planner - "Summarize the next useful Den follow-up task."
/den-run-subagent --continue coder 123 "Continue from the prior coder run."
/den-run-coder 123 "Keep the change scoped to the CLI wrapper."
/den-run-reviewer 123 "Review main...task/123-example."
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

- Persist richer sub-agent session IDs and run metadata, possibly in an
  `agent_runs` table after the stream-ops spike proves useful.
- Add parallel fanout, worktree isolation, and richer role-specific defaults
  beyond model/tools for coder/reviewer runs.
- Add conductor prompts for drift detection using task intent, acceptance
  criteria, review findings, and coder responses.
- Decide whether the long-term Den agent identity should be `pi`, `conductor`,
  or project-configurable per repo.
