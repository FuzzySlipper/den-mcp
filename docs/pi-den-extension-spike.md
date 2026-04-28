# Pi Den Extension Spike

Date: 2026-04-24
Task: `#757`

This spike starts the pivot from per-vendor live-session bridges toward one
project-facing Pi conductor with Den-backed state.

For the stabilized sub-agent runner, observability, control, and smoke-test
contract after task `#785`, see `docs/pi-subagent-infrastructure.md`.

Status update, 2026-04-26: dispatches are retired from the normal Pi conductor path; see `docs/dispatch-retirement-adr.md`. Older dispatch command notes below are legacy/debug context.

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
- Keep Den as the durable source of tasks, messages, stream entries, run records,
  and review records.
- Expose a tiny Den command/tool surface inside Pi without replacing MCP or the
  existing server APIs.
- Keep the implementation reversible while the Pi workflow proves itself.

## Current behavior

On `session_start`, the extension:

- binds to the explicit `DEN_PI_PROJECT_ID` when set; otherwise it lists Den
  projects and chooses the registered project whose `root_path` contains Pi's
  current working directory
- enters a quiet unbound state (`Den: no project bound`) when no registered
  project root matches; it does not infer a project from the directory basename
  and does not check in or start project-specific polling while unbound
- calls `/api/agents/checkin` only after a project binding is resolved
- registers an `agent_instance_binding` with:
  - `agent_family`: `pi`
  - `agent_identity`: `pi` by default
  - `role`: `conductor` by default
  - `transport_kind`: `pi_extension`
- resolves Den-native agent guidance from `/api/projects/{projectId}/agent-guidance`
  and appends the packet to Pi's system prompt when guidance sources exist
- starts a heartbeat loop against `/api/agents/heartbeat` only while bound
- checks out on `session_shutdown`

It also updates the binding metadata on Pi agent start/end with a lightweight
`state` value of `busy` or `idle`. In unbound mode, project-specific commands
fail with an actionable message; `/den-status` explains how to bind, and
`/den-conductor-guidance` can still load global conductor guidance.

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
/den-agent-guidance
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

General Den data access should come from the configured Den MCP server. That
keeps task/message/thread/document tools consistent with other agents and avoids
a confusing partial set of Pi-local REST wrappers.

The Pi Den extension still exposes Pi-native sub-agent tools:

```text
den_run_subagent
den_run_coder
den_run_reviewer
```

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

`/den-config` opens a Pi TUI configuration menu. The menu supports
project-local and global sub-agent role defaults for `coder`, `reviewer`, and
`planner`, a shared fallback model for failed sub-agent runs, and reasoning
capture controls. It lists models from Pi's model registry and saves
provider-qualified model IDs such as `openai-codex/gpt-5.5` or
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
`{{task_context}}`, `{{review_target}}`, and `{{extra_notes}}`. The generated
`{{task_context}}` includes current task status, dependencies/subtasks, recent
thread messages, and the latest structured workflow packets (`coder_context_packet`,
`implementation_packet`, `validation_packet`, `drift_check_packet`, review
request, and review feedback) when present in recent task-thread context.

The default coder prompt is intentionally bounded: it treats the latest
`coder_context_packet` as authoritative, forbids merges, unrequested scope or
architecture expansion, unrequested test/scoring harness or dependency/project
configuration changes, and silent test skipping, then asks for an
`implementation_packet` with branch, commit, files, tests, acceptance checklist,
known gaps, and risk notes. The default reviewer prompt checks acceptance
criteria, packet-vs-diff accuracy, scope drift against the context packet, and
suspicious harness/CI/package/dependency changes while preserving the existing
Den review-loop thread metadata and finding severities.

The `den-conductor` Pi skill is the user/agent-invokable entry point for
conductor mode. It does not duplicate the policy text. It tells Pi to use Den
MCP document tools to resolve project document `pi-conductor-guidance`, then
`_global/pi-conductor-guidance-default`, then this skill's built-in fallback.

Den-native guidance is the broader project guidance path. Operators mark Den
documents as required or important with first-class guidance entries, then Pi
loads the resolved `_global` + project packet on startup and `/reload`. Use
`/den-agent-guidance` to refresh/display the packet without restarting the
session. See `docs/agent-guidance.md` for API, MCP, CLI, and bootstrap
`AGENTS.md` guidance.

## Configuration

Environment variables:

```text
DEN_MCP_URL             default http://192.168.1.10:5199
DEN_MCP_BASE_URL        fallback if DEN_MCP_URL is unset
DEN_PI_PROJECT_ID       optional explicit project id; when unset, bind by registered project root_path
DEN_PI_AGENT            default pi
DEN_PI_ROLE             default conductor
DEN_PI_INSTANCE_ID      optional stable instance id
```

Sub-agent role defaults and reasoning capture controls are read from JSON config
with project-local values overriding global values:

```text
.pi/den-config.json
~/.pi/agent/den-config.json
```

Example:

```json
{
  "version": 1,
  "fallback_model": "zai/glm-5.1",
  "reasoning": {
    "capture_provider_summaries": true,
    "capture_raw_local_previews": false,
    "preview_chars": 240
  },
  "subagents": {
    "coder": { "model": "openai-codex/gpt-5.5" },
    "reviewer": { "model": "anthropic/claude-sonnet-4-6" }
  }
}
```

Explicit `model` arguments on `den_run_subagent`, `den_run_coder`, or
`den_run_reviewer` still take precedence over config defaults and suppress
automatic fallback retry. If a configured/default model run exits non-zero and a
`fallback_model` is configured, the extension records `subagent_fallback_started`
and retries once with that provider-qualified fallback model.

Reasoning capture config keeps provider-visible summaries and raw local previews
separate:

- `capture_provider_summaries` defaults to `true` and allows bounded
  provider/CLI-visible reasoning summaries to appear as `reasoning_summary_*`
  operator breadcrumbs.
- `capture_raw_local_previews` defaults to `false`; when enabled in a trusted
  local setup, bounded raw reasoning previews may appear in `text_preview` with
  `reasoning_redacted: false`.
- `preview_chars` defaults to `240` and is clamped to a bounded local preview
  range.
- `DEN_PI_SUBAGENT_RAW_REASONING=1|true|yes|on` and
  `DEN_PI_SUBAGENT_RAW_REASONING=0|false|no|off` remain temporary process-level
  compatibility overrides for raw local previews. When set to one of those
  recognized values, the env var overrides `capture_raw_local_previews`; unknown
  values are ignored.

The project-local file is gitignored because model choices and reasoning capture
policy are user/machine-specific.

Historical note: during the dispatch migration, Pi could be launched with
`DEN_PI_AGENT=codex` to drain old Codex-targeted dispatch rows. New workflow
should leave Pi identified as `pi` (or another explicit Pi instance identity)
and should not create Codex-targeted dispatches by default.

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

Smoke unbound startup from a temp directory (requires Den and Pi/model access):

```bash
scripts/smoke-pi-den-unbound-startup.sh
```

Then try:

```text
/den-status
/den-inbox
/den-next
/den-claim-next
/skill:den-conductor
/den-agent-guidance
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
