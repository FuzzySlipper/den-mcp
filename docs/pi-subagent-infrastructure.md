# Pi Sub-Agent Infrastructure

Date: 2026-04-26
Status: current stable shape after tasks `#785`, `#806`, `#813`, `#808`, `#815`, `#824`, `#825`, `#826`, `#851`, and `#852`

This note captures the intended shape for Pi-launched Den sub-agents after the
observability and control hardening work. The goal is not to make Pi the whole
conductor application. Pi remains the interactive agent runner; Den owns durable
workflow state and the primary observation surface.

## Shape

- Pi extension: thin runner/tool adapter.
- Den task messages: concise final success or failure records.
- Den agent stream: live lifecycle/control bus.
- Artifact files: detailed forensic logs for stdout, stderr, status, live events, and child Pi session JSONL.
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
3. Spawns `pi --mode json --session-dir {artifact_dir}/sessions -p ...` in a fresh child process by default.
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
sessions/*.jsonl
```

Task-thread messages should stay compact. They include final output for success
or a concise failure explanation plus structured `den_subagent_run` metadata.
Large transcripts and raw event details belong in the artifact files. Fresh child Pi sessions are persisted under each run's local artifact directory so Den can reconstruct historical work structure without copying raw prompts into Den database rows or task-thread messages.

## Parent Tool Return Boundary

Task `#851` separates child-run forensics from the parent conductor's tool-result
context. Pi persists model-callable tool results as parent session
`toolResult` messages, and those messages include a `details` field in the
session/context object. Pi's current first-party provider adapters and
compaction serializer use the tool-result `content` text rather than serializing
`details` into provider payloads, but `details` is still stored in the parent Pi
session and is visible to hooks/adapters. Treat it as parent-context-adjacent
metadata, not as an unlimited UI-only side channel.

`den_run_subagent`, `den_run_coder`, and `den_run_reviewer` therefore return a
bounded parent-facing payload with schema `den_subagent_parent_tool_result`:

- content text: run id, task/review ids when present, role, state, exit/duration,
  model, output status, artifact paths, and a bounded final or failure summary
- details: the same compact identifiers/status fields plus bounded previews and
  artifact paths
- never: full child stdout, stderr/stderr tail, work-event arrays, raw session
  transcript content, or unbounded result metadata

Full forensic data remains in `stdout.jsonl`, `stderr.log`, `events.jsonl`,
`sessions/*.jsonl`, `status.json`, Den AgentRun/run detail, and task-thread
result/failure messages where appropriate. The parent tool return may point at
those artifacts, but it must not copy their raw contents into the conductor
conversation.

Task `#852` adds the complementary parent-session budget surface documented in
[`pi-conductor-context-status.md`](pi-conductor-context-status.md). The
model-callable `den_context_status` tool and `/den-context-status` command report
a clearly labeled estimate of the conductor's current Pi context budget so the
conductor can compact between tasks instead of relying on child-run transcript
artifacts or stale intuition. Task `#967` adds `den_compact_context` and
`/den-compact-context` so conductors can request compaction themselves at safe
Den-recorded task boundaries instead of stopping solely to ask the user to run
`/compact`.

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
pi_session_id
pi_session_dir
pi_session_file_path
pi_session_persisted
review_round_id
workspace_id
worktree_path
branch
base_branch
base_commit
head_commit
purpose
artifacts
```

Completion metadata also includes execution status such as `exit_code`,
`duration_ms`, `aborted`, `timeout_kind`, `assistant_final_found`,
`prompt_echo_detected`, `output_status`, and infrastructure failure/warning
classification.

## Conductor Context Metadata

Task `#808` carries optional conductor context through Pi launch options and the
run metadata layer. `den_run_subagent`, `den_run_coder`, and `den_run_reviewer`
accept review/workspace/git context fields (`review_round_id`, `workspace_id`,
`worktree_path`, `branch`, `base_branch`, `base_commit`, `head_commit`) plus a
normalized `purpose`. The same context is present on `subagent_started`, terminal
lifecycle ops, task-thread result/failure metadata, `status.json`, and artifact
lifecycle events such as `subagent.process_started` / `subagent.process_finished`.

Reviewer runs launched through `den_run_reviewer` can resolve the latest pending
review round from the task detail when a caller does not provide an explicit
`review_round_id`, and will use that round's branch/base/head metadata when the
caller has not overridden it. This keeps run records linked to review workflow
without storing raw prompts durably.

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
agent_work_reasoning_update
agent_work_message_update
agent_work_message_end
agent_work_tool_start
agent_work_tool_end
```

`agent_work_*` entries are emitted by the parent interactive Pi extension for the
operator stream. `subagent_work_*` entries are emitted by Den-launched child runs
and also feed the AgentRun/run-detail projection.

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
- reasoning/thinking activity as dedicated work items with event kind, provider,
  model, run/task context, char counts, and redaction state
- tool call id/name, bounded args previews, bounded result previews, and error
  status
- content type and tool-call summaries when an assistant message requests tools

Only selected low-volume work events are mirrored into Den agent-stream ops
(`subagent_work_turn_start`, `subagent_work_turn_end`, `subagent_work_tool_start`,
`subagent_work_tool_end`, and `subagent_work_message_end`). High-frequency
message/tool/reasoning update deltas stay in `events.jsonl`; Den web reads and
parses the artifact tail for the run detail "Work" timeline. This keeps the
top-level stream human-scale while still making runaway searches, repeated tools,
off-scope commands, and reasoning activity visible while the run is active.

Task `#815` adds session-tree enrichment for historical run detail views. Fresh
sub-agent runs now keep a child Pi session file in `{artifact_dir}/sessions` by
default, and the runner records `pi_session_id`, `pi_session_dir`,
`pi_session_file_path`, and `pi_session_persisted` in `status.json`, artifact
metadata, terminal lifecycle events, and task-thread/run metadata where
available. Set `DEN_PI_SUBAGENT_NO_SESSION=1` to force the older ephemeral
`--no-session` mode for local debugging.

`SubagentRunService` prefers the child session tree when building run-detail work
events, then falls back to `events.jsonl`, then to mirrored agent-stream work ops.
The session normalizer maps Pi session entries to bounded `subagent.work_*`
records:

- `message` assistant entries -> assistant commentary/tool-call summaries
- `message` tool-result entries -> tool result cards
- `message` bash-execution entries -> bash/tool-effect cards
- `compaction` and `branch_summary` -> lifecycle/context cards
- `custom` and `custom_message` -> extension context cards

User-role session messages are intentionally skipped so generated prompts do not
become Den API/work-feed content. Thinking blocks are represented by dedicated
reasoning cards, counts, redaction flags, and content type flags rather than raw
full reasoning text. When Pi/provider output includes an explicit, already
provider-visible reasoning summary (for example an OpenAI Responses reasoning
summary), Den may store it separately as a bounded `reasoning_summary_preview`
operator breadcrumb while keeping `reasoning_redacted: true`. Raw local
previews are controlled by Den Pi extension config rather than hidden process
environment alone: set `reasoning.capture_raw_local_previews` in
`.pi/den-config.json` or `~/.pi/agent/den-config.json` only in trusted local
contexts if Den web/API should include bounded raw reasoning previews from local
artifacts. `reasoning.preview_chars` controls the bounded preview length, and
`DEN_PI_SUBAGENT_RAW_REASONING=1|true|yes|on` or
`DEN_PI_SUBAGENT_RAW_REASONING=0|false|no|off` remains a temporary process-level
compatibility override for raw local previews. Task-thread result messages never
include raw reasoning. The session JSONL remains a local artifact for forensic
inspection.

## Parent Pi Agent Work Mirror

Task `#825` extends the same low-noise operator-signal policy to parent
interactive Pi sessions. Parent sessions are not child `AgentRun` records and do
not have a per-run artifact directory, but the Den Pi extension can observe Pi's
live extension events and append bounded Den agent-stream `ops` entries for the
operator view.

Observed parent Pi event families include:

- `agent_start`, `turn_start`, and `turn_end` for lifecycle activity.
- `message_update` and `message_end` for assistant narrative updates/final
  assistant messages. User-role messages are skipped so prompts do not become Den
  stream content.
- `message_update.assistantMessageEvent` updates such as `text_delta`,
  `thinking_start`, `thinking_delta`, and `thinking_end` for the reasoning lane.
- `tool_execution_start` and `tool_execution_end` for bounded tool-effect cards;
  high-frequency tool updates are intentionally not mirrored.

The parent mirror normalizes these to `agent.work_*` payloads and stores them as
agent-stream event types such as `agent_work_reasoning_update`,
`agent_work_message_end`, `agent_work_tool_start`, and `agent_work_tool_end`.
Each metadata payload includes project id, agent identity, role, instance id,
Den session id, Pi session id/file when Pi exposes it, current task id when the
extension knows one, cwd, model, timestamp, and the bounded normalized event.

Reasoning/thinking content follows the same local policy as sub-agent reasoning:
raw previews are redacted by default and represented by event kind, provider,
model, char counts, and `reasoning_redacted`. Provider-visible summaries are
stored separately as bounded `reasoning_summary_preview` breadcrumbs and do not
make a raw preview available. Set `reasoning.capture_raw_local_previews` in Den
Pi extension config only in trusted local contexts to allow bounded raw reasoning
previews in Den stream metadata; the temporary `DEN_PI_SUBAGENT_RAW_REASONING`
compatibility override uses the same precedence described above. Task-thread
messages still never include raw reasoning.

This parent mirror is best-effort observability, not a durable run transcript.
It avoids raw terminal streaming as a signal, throttles high-frequency assistant
and reasoning update deltas before posting to Den, and depends on what the live
Pi extension API emits during the interactive session. Detailed forensic replay
still belongs to sub-agent run artifacts and task-thread/review records; parent
operator entries are for situational awareness while the conductor is active.

## Den Web Thoughts Lane

Task `#826` adds a Den web `Thoughts` feed beside the existing `Stream` and
`Messages` feed modes. The lane is intentionally narrower than the raw stream:
it classifies `agent.work_reasoning_*`, `agent.work_message_*`,
`subagent.work_reasoning_*`, and `subagent.work_message_*` entries while leaving
tool, bash, and file-edit effects in the run detail/stream surfaces.

The lane combines parent-agent stream ops with recent sub-agent run-detail work
events. It supports selected-project and `_global` views, plus project, task,
agent, and role filters. Items deep-link to the stream entry for parent/stream
ops or to the sub-agent run detail for artifact-backed child work events.
Reasoning items render as redacted markers by default. If a provider-visible
summary breadcrumb is present, the lane shows that bounded summary even while the
item remains redacted. When a trusted local run captured bounded raw reasoning
previews, the `Raw local` toggle can reveal those previews in the lane without
changing task-thread messages; provider-visible summaries alone do not enable the
raw toggle.

## Pi Session Manager / psm-bridge Reference

Pi Session Manager and psm-bridge are useful references for how to interpret Pi's
JSONL session tree, especially the `id`/`parentId` topology and typed entries for
messages, tool results, bash executions, compaction, branch summaries, and custom
extension data. Den borrows that data model directly from Pi's documented session
format instead of depending on either runtime: child sub-agents are already
headless Pi processes, and Den already owns run ids, artifacts, task/review links,
and the web operator surface.

The integration boundary is therefore the local session file under the run
artifact directory, not a second frontend service or bridge process.

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

