# Pi Den Extension Spike

Date: 2026-04-24
Task: `#757`

This spike starts the pivot from per-vendor live-session bridges toward one
project-facing Pi orchestrator with Den-backed state.

For the stabilized sub-agent runner, observability, control, and smoke-test
contract after task `#785`, see `docs/pi-subagent-infrastructure.md`.

Status update, 2026-04-26: dispatches are retired from the normal Pi orchestrator path; see `docs/dispatch-retirement-adr.md`. Older dispatch command notes below are legacy/debug context.

The first git-tracked Pi resources live at:

```text
pi-dev/extensions/den.ts
pi-dev/extensions/den-subagent.ts
pi-dev/skills/den-orchestrator/SKILL.md
```

Keep these resources outside project-local `.pi` discovery so they can be
installed into Pi once and reused across projects without double-loading when
Pi starts inside this repo.

## Goals for this slice

- Bind a running Pi session to Den as one project-level orchestrator.
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
  - `role`: `orchestrator` by default
  - `transport_kind`: `pi_extension`
- resolves Den-native agent guidance from `/api/projects/{projectId}/agent-guidance`
  and appends the packet to Pi's system prompt when guidance sources exist
- starts a heartbeat loop against `/api/agents/heartbeat` only while bound
- checks out on `session_shutdown`

It also updates the binding metadata on Pi agent start/end with a lightweight
`state` value of `busy` or `idle`. In unbound mode, project-specific commands
fail with an actionable message; `/den-status` explains how to bind, and
`/den-orchestrator-guidance` can still load global orchestrator guidance.

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
/den-orchestrator-guidance
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

The `den-orchestrator` Pi skill is the user/agent-invokable entry point for
orchestrator mode. It does not duplicate the policy
text. It tells Pi to use Den MCP document tools to resolve project document
`pi-orchestrator-guidance`, then `_global/pi-orchestrator-guidance-default`, then
this skill's built-in fallback.

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
DEN_PI_ROLE             default orchestrator
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

### Worktree config inheritance

When Pi runs in an isolated git worktree (created via `git worktree add`), the
project-local `.pi/den-config.json` may not exist in the worktree directory.
Without inheritance, `loadMergedDenExtensionConfig` would fall back to global
config only, potentially using an unintended model for delegated sub-agents.

The config loader now detects linked worktrees by inspecting
`git rev-parse --path-format=absolute --git-common-dir`:

- **Primary checkout**: the git common dir's parent matches `cwd`. Only the
  local `cwd/.pi/den-config.json` is tried.
- **Linked worktree**: the git common dir's parent is the primary checkout's
  root. If `cwd/.pi/den-config.json` is absent, the loader falls back to
  `<primary-checkout>/.pi/den-config.json`.
- **Non-git directory**: only the local path is tried; no worktree detection.

This ensures delegated coder/reviewer runs in isolated worktrees inherit the
project's model routing and cost-control settings from the primary checkout
without manual copying or symlinking. If the worktree has its own
`.pi/den-config.json`, it takes precedence over the inherited config.

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
/skill:den-orchestrator
/den-agent-guidance
/den-orchestrator-guidance
/den-config
/den-run-subagent planner - "Summarize the next useful Den follow-up task."
/den-run-subagent --continue coder 123 "Continue from the prior coder run."
/den-run-coder 123 "Keep the change scoped to the CLI wrapper."
/den-run-reviewer 123 "Review main...task/123-example."
```

## Orchestrator direction

The intended next shape is:

- one user-facing, durable Pi orchestrator per project
- implementation and review work run as bounded sub-agent sessions
- reviewer sessions use fresh context and a different provider/model
- drift analysis uses `den_drift_check`, `den_drift_sentinel`, or equivalent
  Den drift tooling rather than inline orchestrator analysis
- Den task messages and review rounds stay the source of truth
- the orchestrator reads coder/reviewer communication for intent drift, not as a
  second code reviewer
- user escalation happens through Den task-thread questions and targeted stream
  entries when the orchestrator detects a decision outside agent authority

The important distinction is user-facing orchestrator versus non-user-facing
sub-agent run, not "coder terminal" versus "reviewer terminal."

## Collaboration Sessions

Pi-facing collaboration session tooling lets humans and agents create, read,
annotate, and compile Den-backed collaboration sessions without requiring the
Den Desktop UI.

The feature is built on the Den collaboration REST API (`Task #916` API/model
and `Task #921` segmenter/compiler) and exposed through Pi extension tools and
commands.

### Tools (model-callable)

```text
den_collab_create_session        Create a collaboration session from markdown content
den_collab_list_sessions         List sessions for the current project
den_collab_get_session           Get full session detail with segments/annotations/drafts
den_collab_add_annotation        Add a note/skip/done/flag annotation to a segment
den_collab_update_annotation     Update an existing annotation (optimistic concurrency)
den_collab_delete_annotation     Delete an annotation (optimistic concurrency)
den_collab_compile_response      Compile annotations into a structured response draft
den_collab_add_turn              Add a new annotatable turn to a session
den_collab_update_session_status Change session status (active/resolved/archived)
```

### Commands (human Pi TUI)

```text
/den-collab-create [--task <id>] [--title <text>] <markdown or ->
/den-collab-list [--task <id>] [--status active|resolved|archived]
/den-collab-open <session_id>
/den-collab-annotate <session_id> <segment_id> <note|skip|done|flag> [body]
/den-collab-delete-annotation <session_id> <annotation_id> <expected_revision>
/den-collab-compile <session_id> [turn_id]
/den-collab-add-turn <session_id> <markdown or ->
/den-collab-status <session_id> <expected_status> <new_status>
```

### Workflow examples

**Human starts a collaboration session from the last assistant response:**

```text
/den-collab-create --title "Annotate architecture plan" -

Session #42 created.
  Session #42: Annotate architecture plan [active]
  Task #918
  Pi run: pi-den-mcp-abc123
  Created by: pi
  Turns: 1
  Segments: 7
```

**Agent creates a session programmatically:**

Via `den_collab_create_session` tool with `raw_markdown`, `task_id`, `source_kind:"den_message"`, `source_ref:"2614"`, etc.

**List pending sessions for the current task:**

```text
/den-collab-list --task 918

2 collaboration session(s):

  Session #42: Annotate architecture plan [active]
    Task #918
    Created by: pi
    Turns: 1
    Segments: 7

  Session #15: Review PR comments [active]
    Created by: user
    Turns: 2
    Annotations: 4
```

**Open a session to see segments and annotations:**

```text
/den-collab-open 42

Session #42: Annotate architecture plan [active]
  Task #918
  ...

--- Turn #1 (assistant, pi_response) ---
  [1] heading: # Architecture Plan
  [2] paragraph: We recommend using Den for session persistence...
  [3] code_block: [code block: const session = await collabCrea...]
  [4] paragraph: The tooling layer should expose...

--- Annotations ---
  [note]: Consider adding a delete endpoint too (user)
  [flag]: Needs discussion on auth model (user)
```

**Annotate or delete an annotation:**

```text
/den-collab-annotate 42 3 flag "Add rate limiting concerns"

Annotation #101 (flag) created on segment #3.

/den-collab-delete-annotation 42 101 1

Annotation #101 deleted from session #42.
```

**Compile the response draft:**

```text
/den-collab-compile 42

Compiled response for session #42 (turn 7 segments, 2 annotations):
Draft saved: true

> [segment 2 · abc12345] We recommend using Den for session persistence...
  [note]: Consider adding a delete endpoint too

> [segment 3 · def67890] [code block: const session = await collabCrea...]
  [FLAG]: Needs discussion on auth model

---
[5 section(s) not annotated — treat as acknowledged, proceed with flagged items]
```

### Agent-driven workflow

An agent can drive the full workflow through model-callable tools without
requiring Desktop or TUI:

1. **Create** a session with `den_collab_create_session`, linking to task and run.
2. **List** open sessions with `den_collab_list_sessions` to find pending ones.
3. **Get** session detail with `den_collab_get_session` to read segments and annotations.
4. **Add/update/delete annotations** with `den_collab_add_annotation`,
   `den_collab_update_annotation`, and `den_collab_delete_annotation`.
5. **Compile** response with `den_collab_compile_response` to consume annotations
   as structured text. The saved draft is also visible in Den Desktop.
6. **Add a new turn** with `den_collab_add_turn` for follow-up responses.
7. **Resolve** the session with `den_collab_update_session_status` when done.

### Metadata and linking

Sessions created by Pi tools include in the `source_context`:

- `project_id`: the Den project ID
- `task_id` / `current_task_id`: the linked or current Den task ID
- `agent`, `role`, `instance_id`, and `den_binding_session_id`: Den agent binding context
- `pi_session_id` and `pi_session_file`: Pi runtime session identifiers when available
- `model`: the active model provider/id when available
- `source_kind`, `source_ref`, and `source_uri`: source provenance when provided

Tools also set top-level `pi_run_id` to the instance ID and `pi_session_id` to
the Pi runtime session ID when available.

### Implementation notes

- All collaboration data is persisted through the Den REST API. No local files.
- Response compilation mirrors the server-side `CollaborationResponseCompiler`
  output format for consistent formatting between Desktop-compiled and
  Pi-compiled drafts.
- The content hash segmenter runs server-side; Pi sends raw markdown and
  receives segmented turns in the response.
- Annotations use optimistic concurrency via `expected_revision`.

## Open follow-ups

- Persist richer sub-agent session IDs and run metadata, possibly in an
  `agent_runs` table after the stream-ops spike proves useful.
- Add parallel fanout, worktree isolation, and richer role-specific defaults
  beyond model/tools for coder/reviewer runs.
- Add orchestrator prompts for drift detection using task intent, acceptance
  criteria, review findings, and coder responses.
- Decide whether the long-term Den agent identity should be `pi`, `orchestrator`,
  or project-configurable per repo.
- Collaboration: threaded follow-up annotations per segment; parallel
  human/agent editing; delivery of compiled responses to agent input.
