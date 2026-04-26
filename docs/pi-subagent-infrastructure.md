# Pi Sub-Agent Infrastructure

Date: 2026-04-26
Status: current stable shape after tasks `#785` and `#813`

This note captures the intended shape for Pi-launched Den sub-agents after the
observability and control hardening work. The goal is not to make Pi the whole
conductor application. Pi remains the interactive agent runner; Den owns durable
workflow state and the primary observation surface.

## Shape

- Pi extension: thin runner/tool adapter.
- Den task messages: concise final success or failure records.
- Den agent stream: live lifecycle/control bus.
- Artifact files: detailed forensic logs for stdout, stderr, status, and events.
- Den web: primary place to watch and manage sub-agent runs.

The conductor chat can mention important outcomes, but it should not be the only
place where run state is visible. As run volume grows, Den web should carry the
operational view instead of flooding a chat stream with every agent heartbeat.

## Components

Tracked Pi resources live under `pi-dev/`:

```text
pi-dev/extensions/den.ts
pi-dev/extensions/den-subagent.ts
pi-dev/lib/den-subagent-pipeline.ts
pi-dev/lib/den-subagent-recorder.ts
pi-dev/lib/den-subagent-runner.ts
pi-dev/extensions/pi-powerline-footer/
pi-dev/skills/den-conductor/SKILL.md
```

`den-subagent.ts` owns the Pi tool/command surface and Den posting. The helper
modules own pipeline schema, output extraction, artifact recording, and the
Pi CLI runner. Keep helper modules outside auto-loaded extension discovery
unless they intentionally export a Pi extension factory.

The local `pi-powerline-footer` copy is an owned fork of the upstream extension,
based on the upstream stale-context fix. Den-launched child runs set
`DEN_PI_PARENT_INSTANCE_ID`, and the footer returns early in that case so
interactive footer hooks do not pollute headless JSON sub-agent runs. Normal
interactive Pi sessions still load the footer.

## Runner Contract

The model-callable tools are:

```text
den_run_subagent
den_run_coder
den_run_reviewer
```

By default a sub-agent launch:

1. Creates a run id and artifact directory under
   `~/.pi/agent/den-subagent-runs/{run_id}`.
2. Appends `subagent_started` to Den agent-stream.
3. Spawns `pi --mode json -p ...` in a fresh child process.
4. Mirrors lifecycle events from artifacts into Den agent-stream.
5. Posts one task-thread result or failure message when `task_id` and
   `post_result` allow it.

The child process receives Den/Pi identity environment variables:

```text
DEN_PI_AGENT
DEN_PI_ROLE
DEN_PI_INSTANCE_ID
DEN_PI_PARENT_INSTANCE_ID
```

These variables let child extensions recognize that they are running as a
headless Den sub-agent rather than as an interactive conductor.

## Durable Records

Every run should produce these artifacts:

```text
stdout.jsonl
stderr.log
status.json
events.jsonl
```

Task-thread messages should stay compact. They include final output for success
or a concise failure explanation plus structured `den_subagent_run` metadata.
Large transcripts and raw event details belong in the artifact files.

The canonical metadata schema is:

```text
schema: den_subagent_run
schema_version: 1
run_id
role
task_id
cwd
backend
model
tools
session_mode
session
rerun_of_run_id
artifacts
```

Completion metadata also includes execution status such as `exit_code`,
`duration_ms`, `aborted`, `timeout_kind`, `assistant_final_found`,
`prompt_echo_detected`, `output_status`, and infrastructure failure/warning
classification.

## AgentRun Durable Projection

Task `#806` promotes sub-agent run state into the first-class `agent_runs`
table. `agent_stream_entries` remains the append-only lifecycle/audit log; the
AgentRun record is a mutable projection optimized for list/detail filters,
controls, retention, and future review/workspace linkage.

The projection stores run identity and current state (`run_id`, project/task,
optional `review_round_id`, optional `workspace_id`, role/backend/model,
sender instance, state, start/end/duration, pid/exit/signal/timeout/output,
infrastructure classification, artifact paths, rerun linkage, and counters such
as heartbeats/assistant outputs/event count). New ops appended through
`AgentStreamOpsService` update this projection from the existing `subagent_*`
lifecycle events.

Migration/backfill behavior is deliberately conservative: existing stream-only
runs remain readable. `SubagentRunService` reads durable AgentRun records first,
then merges stream-derived summaries as a fallback and rebuilds missing/stale
AgentRun rows from `agent_stream_entries` during list/detail reads. This lets old
artifact-backed runs stay inspectable without losing the audit log as the source
of truth for event history.

## Agent Stream Events

The agent stream is the live lifecycle bus. Important event types include:

```text
subagent_started
subagent_process_started
subagent_heartbeat
subagent_assistant_output
subagent_prompt_echo_detected
subagent_fallback_started
subagent_abort_requested
subagent_abort
subagent_aborted
subagent_rerun_requested
subagent_rerun_accepted
subagent_rerun_unavailable
subagent_completed
subagent_timeout
subagent_startup_timeout
subagent_terminal_drain_timeout
subagent_failed
subagent_spawn_error
subagent_work_turn_start
subagent_work_turn_end
subagent_work_tool_start
subagent_work_tool_end
subagent_work_message_end
```

Den web derives run state from this stream. That keeps the current design
compatible with alternate future runners, because a runner only has to emit the
same lifecycle schema and task result/failure metadata.

## Child Pi Work Feed

Task `#813` audited live child `pi --mode json` output from recent sub-agent
runs. The child JSON stream includes these Pi event families before they are
condensed into Den-visible run state:

```text
session
agent_start
turn_start
turn_end
message_start
message_update
message_end
tool_execution_start
tool_execution_update
tool_execution_end
```

`stdout.jsonl` remains the raw forensic transcript. The runner also normalizes
these events into bounded `subagent.work_*` records in `events.jsonl`, with
prompt/user messages intentionally skipped. The normalized records include:

- turn start/end timestamps
- assistant message start/update/end summaries without raw prompts
- tool call id/name, bounded args previews, bounded result previews, and error
  status
- content type and tool-call summaries when an assistant message requests tools

Only selected low-volume work events are mirrored into Den agent-stream ops
(`subagent_work_turn_start`, `subagent_work_turn_end`, `subagent_work_tool_start`,
`subagent_work_tool_end`, and `subagent_work_message_end`). High-frequency
message/tool update deltas stay in `events.jsonl`; Den web reads and parses the
artifact tail for the run detail "Work" timeline. This keeps the top-level stream
human-scale while still making runaway searches, repeated tools, and off-scope
commands visible while the run is active.

## Tau Reference

Tau is useful as a design reference for interactive parent Pi sessions: it
subscribes to Pi events, mirrors session/message/tool state over WebSocket, and
renders compact tool cards in a browser. Den sub-agents should not depend on Tau
for visibility in this slice. They are headless child `pi --mode json` processes
whose stdout is already captured by the Den runner. Loading Tau into every child
would require per-run ports, auth, cleanup, and a registry of child servers, while
Den already has run ids, artifacts, and a web surface. The supported sub-agent
path is therefore the normalized Den work feed above; Tau can still be evaluated
separately for interactive parent-session mirroring.

## Controls

Den web can request runtime control through agent-stream ops entries.

Abort:

- Web appends `subagent_abort_requested` for a running run id.
- The live Pi runner polls for requests matching that `run_id`.
- The runner terminates the process group and records `subagent_abort` followed
  by `subagent_aborted`.
- The task message is a `subagent_failure` with `aborted: true`.

Rerun:

- Web appends `subagent_rerun_requested` for a terminal run.
- The live Pi conductor polls for requests targeted at its instance id.
- If the conductor still has an in-memory snapshot for that run, it appends
  `subagent_rerun_accepted` and launches a fresh run with `rerun_of_run_id`.
- If the snapshot is stale, foreign, or unavailable, it appends
  `subagent_rerun_unavailable` with a reason such as
  `snapshot_not_available`.

Rerun snapshots are intentionally in-memory for now. This avoids storing raw
prompts in Den while the control surface is still settling. A future durable run
table can revisit this if audited reruns become important.

## Failure Classification

Sub-agent failure records distinguish infrastructure from model/task failure.
Examples:

- `extension_load`: Pi failed to load an extension before reaching the model.
- `extension_runtime`: extension warning or runtime noise in stderr.
- `startup`: child produced no JSON output before startup timeout.
- `terminal_drain`: child produced terminal assistant output but did not exit.
- `aborted`: Den or caller requested abort.
- `spawn_error`: Pi child process could not start.

Infrastructure failures do not trigger fallback-model retries. Fallback is for
normal model/run failure, not broken runner plumbing.

## Observation Norms

Use Den web as the primary active run surface. The Pi TUI may show immediate
tool-call progress for direct runs, but background reruns are intentionally
quiet in the local Pi window and visible through Den agent-stream/task records.

This is a deliberate tradeoff:

- Pi remains usable as the interactive conductor.
- Den becomes the durable place to inspect many concurrent runs.
- Chat commentary can stay human-scale instead of becoming a raw event log.

If operators need more local confidence, add small Pi notifications for
control events such as rerun accepted/unavailable. Do not move the primary run
state back into transient TUI widgets.

## Smoke Tests

Run these against a live Pi conductor and Den web before treating the pipeline
as healthy:

1. Success path:
   - launch a planner with prompt `Reply exactly: SUBAGENT_SMOKE_OK`
   - expect `subagent_started`, `subagent_process_started`,
     `subagent_assistant_output`, `subagent_completed`
   - expect one `subagent_result` task message with no prompt echo and no
     infrastructure warning

2. Abort path:
   - launch a planner with `tools: bash`
   - prompt it to run `bash -lc 'sleep 120'` before replying
   - click Abort in Den web
   - expect `subagent_abort_requested`, `subagent_abort`,
     `subagent_aborted`, `aborted: true`, no lingering sleep process

3. Live rerun path:
   - launch a tiny completed run
   - click Request rerun while the same Pi conductor session is still alive
   - expect `subagent_rerun_requested`, `subagent_rerun_accepted`, a new run
     with `rerun_of_run_id`, and a second clean task result

4. Stale rerun path:
   - reload Pi or choose a run from another runner/session
   - click Request rerun
   - expect `subagent_rerun_unavailable` with `snapshot_not_available` or an
     equivalent explicit reason, and no silent new run

5. Footer isolation:
   - run a Den child-style Pi startup with `DEN_PI_PARENT_INSTANCE_ID` set
   - expect no `pi-powerline-footer` status output or stale-context warning
   - run a normal Pi startup
   - expect the footer to load normally

## Regression Commands

Local checks used for this slice:

```bash
node --test tests/PiExtension.Tests/*.mjs tests/ClientApp.Tests/*.mjs
dotnet test den-mcp.slnx
git diff --check
timeout 8s env PI_OFFLINE=1 DEN_PI_PARENT_INSTANCE_ID=den-parent-smoke DEN_PI_INSTANCE_ID=den-child-smoke DEN_PI_AGENT=codex-subagent pi --mode rpc --no-session --no-context-files --no-skills --no-prompt-templates --no-themes --no-tools
timeout 8s env PI_OFFLINE=1 pi --mode rpc --no-session --no-context-files --no-skills --no-prompt-templates --no-themes --no-tools
```

