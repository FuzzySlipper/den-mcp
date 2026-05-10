# Pi Extension Orchestration Port Audit — Task #1252

Date: 2026-05-10
Project: `den-mcp`
Parent task: #1237 — Convert orchestrator workflow to Hermes-driven Den Pi workers

## Executive summary

The existing Pi extension already contains a mature orchestration prototype: worker launch, session/artifact recording, task-thread packet production, drift and validation helpers, parent-facing tool result summaries, collaboration/session annotation helpers, and reviewer/coder prompt templates. The #1237 Hermes transition should **not** port this logic wholesale into Hermes Python.

Recommended direction:

1. Promote reusable workflow primitives into Den server APIs/MCP tools: worker lifecycle, context packet creation, role launch, completion packet extraction/posting, validation/drift packet production, run/artifact status, rerun/abort/control, and collaboration primitives where useful.
2. Keep Pi-local only the runtime-specific mechanics: spawning `pi --mode json`, parsing Pi stdout/session files, process-tree termination, local artifact files, model/tool defaults, and Pi extension UI widgets/commands.
3. Keep Hermes-side helpers minimal: call Den MCP tools, display compact run/tool results, and verify Den/gitrepo artifacts. Hermes should not own packet parsing, drift heuristics, or curl/glue orchestration.
4. Treat every custom script/curl workaround needed from Hermes as a Den API/tool backlog item.

## Files inspected

Minimum requested files were inspected:

- `pi-dev/extensions/den-subagent.ts`
- `pi-dev/extensions/den.ts`
- `pi-dev/lib/den-subagent-parent-tool-result.ts`
- `pi-dev/lib/den-subagent-pipeline.ts`
- `pi-dev/lib/den-subagent-runner.ts`
- `pi-dev/lib/den-collaboration.ts`
- Coder/reviewer/packet helpers:
  - `pi-dev/lib/den-coder-context-packet.ts`
  - `pi-dev/lib/den-implementation-packet.ts`
  - `pi-dev/lib/den-post-implementation-packet.ts`
  - `pi-dev/lib/den-validation-packet.ts`
  - `pi-dev/lib/den-prompt-templates.ts`
  - `pi-dev/skills/den-orchestrator/SKILL.md`
- Server-side related surfaces were spot-checked for existing capabilities:
  - `src/DenMcp.Server/Tools/*`
  - `src/DenMcp.Server/Routes/CollaborationRoutes.cs`
  - `src/DenMcp.Server/Routes/SubagentRunRoutes.cs`
  - `src/DenMcp.Core/Services/SubagentRunService.cs`
  - `src/DenMcp.Core/Services/AgentStreamOpsService.cs`
  - `src/DenMcp.Core/Services/CollaborationResponseCompiler.cs`

## Inventory and classification

| Feature / concept | Current Pi implementation | Classification | Recommendation for #1237 |
| --- | --- | --- | --- |
| Low-level child process launch (`pi --mode json`) | `den-subagent-runner.ts` spawns Pi with session mode args, env identities, cwd, model/tools, startup/final-drain/force-kill timers, process group kill. | Keep Pi-local runtime detail; expose through Den server worker lifecycle. | Do not rewrite in Hermes. Den should provide a launch API that hides runtime mechanics and returns worker/run identity plus observation handles. |
| Worker role entrypoints (`den_run_subagent`, `den_run_coder`, `den_run_reviewer`) | `den-subagent.ts` exposes Pi model-callable tools/commands and wraps prompt rendering/run/posting. | Promote to Den server/MCP tool, with Pi runtime adapter underneath. | Add role-level MCP tools such as `launch_pi_worker`, `launch_coder_worker`, `launch_reviewer_worker`. Hermes calls these only. |
| Session modes (`fresh`, `continue`, `fork`, `session`) | `addSessionArgs` in `den-subagent-runner.ts`, CLI flags and task wrapper parsing in `den-subagent.ts`. | Promote policy/API contract; keep Pi CLI arg translation local. | Den launch request should accept a controlled session mode enum; Den adapter maps to Pi/container runtime. |
| Run identity and env binding | Parent/child env vars: `DEN_PI_AGENT`, `DEN_PI_ROLE`, `DEN_PI_INSTANCE_ID`, `DEN_PI_PARENT_INSTANCE_ID`; run ids from `makeRunId`. | Promote contract to Den server; runtime-local assignment implementation. | Den should allocate worker/run id and capability scope, then pass minimal env/session file to worker. |
| Artifact directories and local files | Recorder writes status/events/stdout/stderr/results under `~/.pi/agent/den-subagent-runs/{run_id}`; session jsonl persisted. | Mixed: keep local artifact storage mechanics Pi-local; promote artifact index/status summaries into Den. | Den run detail should expose artifact handles, status, recent output, session id, container/tmux ids, and not require Hermes filesystem spelunking. |
| JSON stdout and child work event normalization | `parsePiStdoutLine`, `normalizePiWorkEvent`, message/tool/reasoning summarizers in `den-subagent-pipeline.ts`. | Keep runtime parser local; promote normalized event schema to Den. | Den should receive/store bounded `worker_work_*` events; Hermes should query them via MCP rather than parse jsonl. |
| Reasoning capture policy | `resolveReasoningCaptureOptions`, reasoning preview/summary helpers, redaction/visibility behavior in `den-subagent-pipeline.ts`. | Promote policy/schema to Den; keep provider-specific extraction local. | Worker event schema should explicitly separate private reasoning artifacts from safe summaries/previews. |
| Infrastructure failure classification | `classifySubagentInfrastructureFailure`, quota/extension/runtime checks, stderr warnings. | Promote to Den server/worker status model. | Add standard failure categories (`quota`, `extension_load`, `extension_runtime`, `no_assistant_final`, `prompt_echo_only`, `timeout`, `aborted`). Hermes should consume category, not classify stderr. |
| Parent-facing tool result | `den-subagent-parent-tool-result.ts` produces compact text/details, recovery guidance, artifact summaries, retry actions. | Promote to Den server/MCP result formatting. | MCP launch/status tools should return this style of compact payload directly, including recovery actions and handles. |
| Rerun and abort control | `den-subagent.ts` polls agent stream control messages; handles `abort`/`rerun`, snapshots previous run request. | Promote to Den server/MCP control tools and run state machine. | Add `abort_worker_run` and `rerun_worker_run`; avoid requiring the parent process to keep in-memory snapshots as the durable source. |
| Coder context packet formatting | `den-coder-context-packet.ts` builds markdown with task identity, dependencies, docs, recent packets, constraints, file pointers, validation commands, packet conventions, expected output. | Promote to Den server/MCP tool. | Add `prepare_coder_context_packet` (or generic `prepare_worker_context_packet`) as Den MCP tool. It should post message with metadata and return message id/content. |
| Effective coder config resolution | `resolveEffectiveCoderConfig` checks merged Pi config and candidate config paths. | Keep runtime-local/config-adapter detail; expose resolved model/source in Den launch result. | Den should not require Hermes to understand Pi config files. |
| Prompt template rendering | `den-prompt-templates.ts` fallback coder/reviewer prompts, structured packet insertion, reviewer identity section. | Promote prompt/guidance documents and render operation into Den server. | Add `render_worker_prompt` or make launch tools accept packet/message references and perform server-side rendering. Maintain prompts as Den docs/guidance. |
| Reviewer identity convention | `buildReviewerIdentity`, `ensureReviewerIdentitySection`; server review tools already enforce subagent role identity in some calls. | Promote as Den server policy. | Reviewer launch tool should return/enforce reviewer identity (`<agent>-reviewer`) and include it in prompt/metadata. |
| Implementation packet extraction/posting | `den-implementation-packet.ts` and `den-post-implementation-packet.ts` parse coder final output, detect incomplete prompt, post `implementation_packet`/`implementation_packet_missing`, dedupe. | Strong promote to Den server/MCP tool or worker completion hook. | Add `complete_worker_run` or `post_worker_completion_packet` that validates/extracts packets server-side. Avoid Hermes-side parsing. |
| Validation packet producer | `den-validation-packet.ts` executes commands, classifies pass/fail/blocked/partial, formats `validation_packet`. | Split: command execution belongs worker/runtime; packet schema/format and posting should be Den server/MCP. | Add `run_validation_worker` or `post_validation_packet`; Den should normalize validation results. Hermes should not shell out except as temporary development workaround. |
| Drift check deterministic analysis | `den-drift-check.ts` and command/tool wiring in `den-subagent.ts` compare changed paths against context scope, derive risk, post `drift_check_packet`. | Promote analysis and packet production to Den server/MCP where possible; keep git collection local/runtime-specific if Den cannot access repo. | Add `run_drift_check` MCP tool accepting repo/worktree/ref context and latest packet refs. If Den server cannot access repo directly, Pi worker should collect git facts and Den should classify/record them. |
| Drift sentinel role | `den-drift-sentinel.ts` + `runAndMaybePostDriftSentinel` launch cheap sub-agent over deterministic analysis and suspicious hunks. | Promote as Den worker role adapter; keep runtime execution local. | Add `launch_drift_sentinel_worker` after coder/reviewer baseline. |
| Final branch/head inspection | `den-subagent-final-head.ts` called from `den-subagent.ts` to observe final worktree branch/head/status and enrich metadata. | Promote to Den worker completion/status contract; git inspection may be runtime-local. | Worker completion must report verified branch/head/status; Den should store final vs requested head distinctly. |
| Packet lifecycle ops | `appendPacketLifecycleOps` records `taskThreadPacketOperatorEvent` and agent-stream ops for packets. | Promote to Den server event projection. | Packet posting tools should create lifecycle ops automatically; Hermes should not append separate glue events. |
| Agent stream lifecycle and work events | `appendOps`, `createSubagentProgressPublisher`, `subagentOpsEventTypeForEvent`, visibility mapping. | Promote core event schema/projection to Den. | Den worker APIs should own status/lifecycle events, with MCP query tools for run summaries/details. |
| Context budget/compaction status | `den.ts` implements `/den-context-status`, `/den-compact-context`, parent work mirroring. | Minimal Hermes helper only / obsolete for Pi worker launch. | Hermes has its own session management. Keep Pi context UI local; do not port unless it becomes a generic Den orchestrator diagnostic. |
| Den inbox/task/note/done commands | `den.ts` wraps existing Den APIs in Pi commands. | Obsolete/do not port to Hermes; existing Den MCP tools cover it. | Hermes already has `get_task`, `send_message`, `update_task`, etc. No new worker work needed. |
| Collaboration sessions/annotations/drafts | `den.ts` plus `den-collaboration.ts` wrap collaboration routes and local response compiler. | Promote missing pieces to Den MCP if Hermes needs them; otherwise keep Desktop/Pi UI-specific helpers local. | Candidate MCP tools: `collab_create_session`, `collab_list_sessions`, `collab_get_session`, `collab_add_annotation`, `collab_compile_response`, `collab_save_draft`, `collab_add_turn`, `collab_update_status`. Do not implement a Hermes-specific collaboration client. |
| `/den-config` and subagent defaults UI | `den-subagent.ts` interactive config commands for base URL, role defaults, fallback model, reasoning capture. | Keep Pi-local/admin UI; Den server should own durable worker defaults later. | For #1237, prefer Den project/space settings or guidance docs over Hermes/Pi local config. |
| Tmp cleanup | `den.ts` has `den_tmp_cleanup` for `/tmp/<project-id>` artifacts. | Promote if worker sessions create temp artifacts; otherwise keep utility local. | Add cleanup/status endpoint only if worker lifecycle leaves Den-managed temp/session dirs. |

## Candidate Den MCP tools/APIs

These names are suggested for #1237 descendants. Exact naming can be adjusted to existing MCP conventions.

### Worker lifecycle

- `launch_pi_worker`
  - Inputs: `project_id`, `task_id`, `role`, `prompt_packet_message_id` or `prompt_packet_slug/ref`, optional `review_round_id`, `workspace_id`, `branch`, `base_ref/base_commit`, `head_commit`, `session_mode`, `model_hint`, `capability_scope`, `timeout_policy`.
  - Outputs: `run_id`, `role`, `status`, `worker_identity`, `session_id`, `container_name`, `tmux_session`, artifact handles, and compact recovery guidance.
- `get_worker_run`
  - Return full run status, lifecycle, final output summary, artifact/session handles, branch/head metadata, failure classification.
- `list_worker_runs`
  - Filter by project, task, role, status, review round, branch.
- `abort_worker_run`
  - Request cancellation by run id with reason/actor. Den records durable control entry.
- `rerun_worker_run`
  - Server-side rerun using durable stored launch request. Avoid parent-only in-memory snapshots.
- `cleanup_worker_runs`
  - Expire stale sessions/artifacts according to policy.

### Role adapters

- `launch_coder_worker`
  - Higher-level wrapper around `launch_pi_worker` that prepares/uses coder context packet, renders coder prompt, sets role defaults, and returns `run_id`.
- `launch_reviewer_worker`
  - Uses latest review round/implementation packet/validation/drift context, enforces reviewer identity, and returns review run handles.
- `launch_validator_worker` or `run_validation_worker`
  - Runs declared validation commands in bounded worker/runtime context and posts `validation_packet`.
- `launch_drift_sentinel_worker` and/or `run_drift_check`
  - Runs deterministic and optional model-assisted drift checks and posts `drift_check_packet`.

### Packet and prompt primitives

- `prepare_coder_context_packet`
  - Server-side equivalent of `den_prepare_coder_context`.
- `prepare_reviewer_context_packet`
  - Optional counterpart for reviewer launch.
- `render_worker_prompt`
  - If launch tool does not render internally; takes prompt doc slug plus packet refs and returns bounded prompt/state-file content.
- `post_worker_completion_packet`
  - Server-side extraction/validation/posting of `implementation_packet`, `review_findings_packet`, `validation_packet`, or `worker_failure_packet` from structured worker output.
- `get_latest_task_packet`
  - Convenience lookup by `metadata.type`, task, role, and completeness/status.

### Observability/status

- `get_task_worker_summary`
  - Compact current worker/run summary for a task, including latest packets and open worker actions.
- `get_worker_recent_output`
  - Bounded recent stdout/stderr/assistant output for human/sysadmin visibility.
- `get_worker_artifact`
  - Optional controlled artifact retrieval by run id/artifact name; respect redaction and reasoning privacy.

### Collaboration (only if Hermes workflows need it)

The server has collaboration routes and Desktop/Pi clients, but the current MCP surface visible from Hermes does not expose them. If Hermes orchestration needs annotation/draft collaboration, add MCP wrappers rather than local curl scripts:

- `collab_create_session`
- `collab_list_sessions`
- `collab_get_session`
- `collab_add_annotation`
- `collab_update_annotation`
- `collab_delete_annotation`
- `collab_compile_response`
- `collab_save_draft`
- `collab_add_turn`
- `collab_update_status`

## Recommended edits to #1237 subtasks

### Add before #1239: Worker contract/design task

Title suggestion: `Define Den Pi worker contract and MCP API surface`

Scope:

- Launch request schema and small prompt-reference/state-file model.
- Worker identity/capability scope.
- Run state machine and failure categories.
- Completion packet contract and idempotency/dedup rules.
- Artifact/session/tmux/container observability fields.
- Privacy boundary for reasoning/session files.

Reason: `den-subagent-pipeline.ts`, `den-subagent-parent-tool-result.ts`, packet helpers, and final-head handling already imply this contract. Codifying it first prevents another runtime-specific implementation.

### Split #1239

Current #1239 is `Add Den Pi worker launch adapter for Hermes orchestrators`.

Recommended split:

1. `Add raw Den MCP worker lifecycle tools`
   - `launch_pi_worker`, `get_worker_run`, `list_worker_runs`, `abort_worker_run`, `rerun_worker_run`.
2. `Add role-level Den worker launch adapters`
   - `launch_coder_worker`, `launch_reviewer_worker`, later validator/drift roles.

Reason: role adapters should sit on a stable raw lifecycle API.

### Adjust #1240

Current #1240 is `Implement Den prompt-packet reference flow for Pi workers`.

Recommended scope refinement:

- Promote `formatCoderContextPacket` and prompt rendering behavior into Den server/MCP rather than Hermes helper code.
- Store launch prompt/body in a Den message/document/state file referenced by id, not in process args.
- Add `get_latest_task_packet` or equivalent packet lookup helper.

### Adjust #1241

Current #1241 is `Add structured completion hook/status for Pi workers`.

Recommended scope refinement:

- Reuse/promote `den-implementation-packet.ts` extraction, incomplete-prompt detection, dedupe, and missing-packet behavior.
- Completion should update run status and post packet/lifecycle ops atomically where possible.
- Distinguish requested head vs final observed head.

### Adjust #1242 and #1243

Coder/reviewer conversion should become thin Hermes orchestration steps:

- Prepare/retrieve context packet via Den MCP.
- Launch role worker via Den MCP.
- Poll/query run via Den MCP.
- Verify final branch/head/packets/tests before task progression.

Do not implement packet parsing, Pi CLI spawning, or stderr classification in Hermes.

### Split #1244

Current #1244 is `Add validator/drift worker roles and explicit orchestrator state machine`.

Recommended split:

1. `Promote validation packet/command result schema to Den MCP`
2. `Promote deterministic drift check packet to Den MCP`
3. `Add drift-sentinel worker role`
4. `Add explicit orchestrator state machine`

Reason: validation/drift primitives are independently useful and should not be hidden inside the state machine.

### Adjust #1245

Current #1245 is `Polish Pi worker observability, status, and cleanup lifecycle`.

Recommended scope refinement:

- Implement Den run status/detail projections that include `run_id`, status, role, task, session id, container/tmux names, artifact handles, final output summary, failure category, and latest lifecycle/work events.
- Add cleanup policy and MCP command only after artifact/session storage is explicit.

### Adjust #1246

Current #1246 is docs/guidance. It should update guidance to say:

- Hermes orchestrators call Den MCP worker tools; they do not spawn local subagents or write curl glue.
- Pi extension code is prior art and runtime adapter logic, not the Hermes API surface.
- If a needed action lacks an MCP tool, create a Den improvement task rather than normalizing local scripts.

## Workarounds encountered / Den improvement signals

During this audit, one operational gap was visible from the Hermes side:

- `mcp_den_query_librarian` failed with a generic error while starting the task. This should be made more observable (error category/details) but is not directly part of the Pi worker port.

Potential future workaround to avoid:

- The collaboration API exists as server routes and Pi/Desktop clients, but not as visible Hermes MCP tools. If Hermes needs collaboration sessions for orchestration, add MCP wrappers rather than using custom curl scripts.

No worker-specific custom curl/glue was used for this audit.

## Bottom line for #1237

The reusable assets to preserve are the **contracts**: run lifecycle, packet types, failure classification, prompt references, branch/head verification, and compact parent-facing summaries. The code that launches/parses Pi should remain an adapter detail behind Den APIs. Hermes should become a consumer of Den MCP worker tools, not the new owner of orchestration runtime logic.
