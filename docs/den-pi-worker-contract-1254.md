# Den Pi Worker Contract and MCP API Surface — Task #1254

Date: 2026-05-10
Project: `den-mcp`
Parent task: #1237 — Convert orchestrator workflow to Hermes-driven Den Pi workers
Source audit: [doc: den-mcp/pi-extension-orchestration-port-audit-1252]

## Status and intent

This document defines the reusable Den Pi worker contract that downstream #1237 tasks should implement against. It intentionally describes Den-owned API/state contracts rather than a Hermes-side runtime implementation.

The contract preserves the useful parts of the existing Pi extension/sub-agent prior art while changing the durable owner:

- **Den owns** worker run state, packet references, launch/control/status APIs, lifecycle projections, completion packet semantics, and safe observability.
- **Pi/runtime adapters own** process spawning, `pi --mode json`, tmux/docker/container mechanics, stdout/session parsing, local artifact writes, and provider/runtime quirks.
- **Hermes orchestrators own** high-level coordination: call Den MCP tools, verify branch/head/tests/packets, update task workflow, and escalate missing APIs instead of normalizing local curl/script glue.

## Prior-art mapping

| Contract area | Pi prior art | New owner / boundary |
| --- | --- | --- |
| Role launch commands | `den_run_subagent`, `den_run_coder`, `den_run_reviewer` in `pi-dev/extensions/den-subagent.ts` | Den MCP role launch tools; Pi adapter keeps actual launch mechanics. |
| Child process lifecycle | `den-subagent-runner.ts`, `den-subagent-pipeline.ts`, `den-subagent-recorder.ts` | Den run state machine and normalized event schema; runtime adapter emits events. |
| Context packet rendering | `den-coder-context-packet.ts`, `den-prompt-templates.ts` | Den packet/prompt tools that post/retrieve bounded task-thread packet references. |
| Completion packet extraction | `den-implementation-packet.ts`, `den-post-implementation-packet.ts`, validation/drift helpers | Den completion/status API validates and posts packets idempotently. |
| Parent-facing summary | `den-subagent-parent-tool-result.ts` | MCP tool return payloads and `get_worker_run` summaries. |
| Failure classification | `classifySubagentInfrastructureFailure` and pipeline status metadata | Standard Den worker failure categories and status fields. |
| Branch/head observation | `den-subagent-final-head.ts` | Completion contract stores requested refs separately from final observed refs. |
| Raw transcripts/reasoning | Pi session/artifact files and redaction config | Runtime-local artifacts by default; Den stores safe summaries and handles only. |

## Naming and compatibility

The existing implementation uses `subagent` and `AgentRun` vocabulary. The #1237 API should introduce `worker` vocabulary for the new MCP surface, while allowing the server implementation to bridge to the existing `agent_runs`/`subagent_*` projection during migration.

Recommended compatibility rule:

- New public MCP/API tools use `worker` names.
- Existing `subagent` REST/routes/projections may be reused internally if the response schema is normalized to this document.
- Event names may remain `subagent_*` during transition, but new fields should be role-neutral and documented as `worker_*` equivalents.

## Core entities

### Worker role

Allowed initial roles:

```text
raw
coder
reviewer
validator
drift_sentinel
```

`raw` is the lifecycle substrate. Role adapters (`coder`, `reviewer`, later `validator` and `drift_sentinel`) are thin server-side presets over `raw` launch plus packet/prompt/completion conventions.

### Worker run id

Den allocates `run_id` before launch. It must be globally unique and stable across all events, artifacts, task messages, and control actions.

Recommended format:

```text
piw_<yyyyMMddHHmmss>_<short-random-or-ulid>
```

The exact format is not semantically important; clients must treat it as opaque.

### Worker identity

A worker has both a Den identity and a runtime identity:

```json
{
  "worker_identity": "den-mcp-runner-coder",
  "agent_identity": "den-mcp-runner",
  "role": "coder",
  "instance_id": "piw_20260510_abc123",
  "parent_instance_id": "hermes-den-mcp-runner-...",
  "capability_scope_id": "cap_..."
}
```

Rules:

- Reviewer identities should follow the existing review-tool convention: `<agent>-reviewer` when structured review findings are posted.
- Capability scope is explicit and recorded even if the first implementation only logs a conservative placeholder.
- The worker must not receive broad Den credentials by default; it receives a scoped launch state file or token/capability sufficient for the requested role.

## Launch request contract

Raw lifecycle tool input should be a small request that references durable Den packet/state records rather than carrying a giant prompt in process args.

```json
{
  "project_id": "den-mcp",
  "task_id": 1242,
  "role": "coder",
  "requested_by": "den-mcp-runner",
  "prompt_ref": {
    "kind": "task_message",
    "message_id": 5555,
    "metadata_type": "coder_context_packet"
  },
  "state_file_ref": {
    "kind": "den_managed_state_file",
    "path_hint": null
  },
  "review_round_id": null,
  "workspace_id": "optional-workspace-id",
  "repo": {
    "project_root": "/home/pi/dev/den-mcp",
    "worktree_path": "/home/pi/dev/den-mcp-worktrees/task-1242",
    "base_branch": "main",
    "base_commit": "08919df...",
    "requested_branch": "task/1242-coder-path",
    "requested_head_commit": null
  },
  "session": {
    "mode": "fresh",
    "rerun_of_run_id": null,
    "timeout_seconds": 7200
  },
  "model": {
    "hint": null,
    "allow_runtime_default": true
  },
  "capability_scope": {
    "den": ["read_task", "read_task_thread", "post_task_message", "post_completion_packet", "append_worker_event"],
    "repo": ["read_project", "write_task_worktree"],
    "network": "default_runtime_policy"
  },
  "post_result": true,
  "dedupe_key": "task-1242:coder:context-message-5555"
}
```

Required fields for raw launch:

- `project_id`
- `role`
- `requested_by`
- exactly one prompt source: `prompt_ref` or `state_file_ref`
- `session.mode`
- `capability_scope` or a named server-side capability preset

Required fields for task-linked launches:

- `task_id`
- `dedupe_key` when the launch should be idempotent

### Prompt/state-file model

The launch command line must not include the full prompt, secrets, broad task context, or raw packet bodies. It may include only small identifiers such as `run_id`, `project_id`, and a Den-managed state-file path.

The state file may contain rendered prompt text and scoped capability material if:

- It is written under a per-run state directory with non-world-readable/writable permissions.
- It is referenced by `run_id` and cleaned/retained according to policy.
- It does not include server-owned provider API keys or unrelated credentials.
- Its presence is recorded as an artifact handle, not copied into task messages.

## Run state machine

Canonical states:

```text
queued
launching
running
completion_pending
completed
failed
aborted
timed_out
cleanup_pending
cleaned_up
```

Terminal states:

```text
completed
failed
aborted
timed_out
cleaned_up
```

Allowed high-level transitions:

```text
queued -> launching -> running -> completion_pending -> completed
queued -> launching -> failed
launching -> running -> failed
launching -> timed_out
running -> completion_pending -> failed
running -> timed_out
running -> aborted
completed|failed|aborted|timed_out -> cleanup_pending -> cleaned_up
```

`completion_pending` means the runtime process ended or supplied final output, and Den is validating/posting the structured completion packet. The orchestrator should not treat process exit as success until the run is terminal and completion packet status is known.

## Standard failure categories

Den should use these categories for worker-level failures:

| Category | Meaning | Typical owner |
| --- | --- | --- |
| `quota` | Model/provider quota, billing, rate limit, or exhausted credit. | Operator/user/provider. |
| `extension_load` | Runtime failed before model work because extension/plugin load failed. | Runtime adapter/tooling. |
| `extension_runtime` | Extension produced runtime error/noise that invalidates or threatens the run. | Runtime adapter/tooling. |
| `no_assistant_final` | Process ended without a usable assistant final message. | Runtime/model/prompt. |
| `prompt_echo_only` | Output appears to only echo the prompt/instructions. | Runtime/model/prompt. |
| `timeout` | Startup, execution, final-drain, or completion-post timeout. | Runtime/worker; may be retryable. |
| `aborted` | Den/user/orchestrator requested cancellation. | Control path. |
| `malformed_completion` | Worker final output exists but completion packet is invalid/unparseable. | Worker/prompt or completion parser. |
| `missing_packet` | Worker did not provide a required packet for its role. | Worker/prompt. |
| `spawn_error` | Runtime process/container/tmux session could not start. | Runtime host. |
| `infrastructure` | Catch-all for infrastructure failure after specific categories are exhausted. | Runtime host/tooling. |
| `task_blocked` | Worker reports it cannot proceed for task reasons, not runtime failure. | Orchestrator/planner. |

Downstream tools may add `failure_subcategory` and `failure_detail`, but clients should branch on the standard `failure_category` set above.

## Completion packet contract

Each role has an expected packet type:

| Role | Required packet metadata type | Notes |
| --- | --- | --- |
| `coder` | `implementation_packet` | Must include branch/head, summary, files changed, tests run, acceptance checklist, gaps/risks. |
| `reviewer` | `review_findings_packet` or structured review findings/verdict | Must link review round, reviewed base/head, findings, verdict. |
| `validator` | `validation_packet` | Must include command list, exit codes, output summaries, verdict. |
| `drift_sentinel` | `drift_check_packet` | Must include scope inputs, changed paths, risk signals, recommendation. |
| `raw` | `worker_result_packet` or role-specific override | Used for smoke tests and low-level lifecycle validation. |

Completion post operation inputs:

```json
{
  "run_id": "piw_...",
  "project_id": "den-mcp",
  "task_id": 1242,
  "role": "coder",
  "packet_type": "implementation_packet",
  "packet_body": "markdown or structured-json body",
  "packet_metadata": {
    "type": "implementation_packet",
    "prepared_by": "coder",
    "workflow": "den_pi_worker",
    "version": 1,
    "run_id": "piw_..."
  },
  "final_observation": {
    "branch": "task/1242-coder-path",
    "head_commit": "abc123...",
    "worktree_status": "clean"
  },
  "dedupe_key": "piw_...:implementation_packet:v1"
}
```

Rules:

- Completion posting must be idempotent by `run_id + packet_type + dedupe_key`.
- Duplicate completions return the existing task message/packet reference rather than posting again.
- Missing required packets move the run to `failed` with `failure_category = missing_packet` and post a compact `worker_failure_packet` if `post_result` is true.
- Malformed packets move the run to `failed` with `failure_category = malformed_completion`; the raw final output remains an artifact, not a task-thread dump.
- Completion posting should update run status and post packet/lifecycle ops atomically where practical. If atomicity is impossible in the first slice, the response must expose partial state clearly.

## Branch/head reporting

Run status must distinguish requested source-control context from final observed state.

```json
{
  "requested_repo": {
    "base_branch": "main",
    "base_commit": "08919df...",
    "branch": "task/1242-coder-path",
    "head_commit": null,
    "worktree_path": "/home/pi/dev/..."
  },
  "final_repo": {
    "branch": "task/1242-coder-path",
    "head_commit": "abc123...",
    "worktree_status": "clean",
    "diff_base": "08919df...",
    "diff_summary": "5 files changed, ...",
    "observed_at": "2026-05-10T10:00:00Z"
  }
}
```

The orchestrator must verify final branch/head/status before review or merge. A worker claiming success without final observed head should be treated as incomplete.

## Observability contract

`get_worker_run` should return a compact summary suitable for orchestrators and humans:

```json
{
  "run_id": "piw_...",
  "project_id": "den-mcp",
  "task_id": 1242,
  "role": "coder",
  "state": "running",
  "failure_category": null,
  "worker_identity": "den-mcp-runner-coder",
  "session": {
    "mode": "fresh",
    "pi_session_id": "...",
    "tmux_session": "den-pi-piw_...",
    "container_name": "den-pi-piw_...",
    "compose_project": "den-pi-piw-...",
    "host_id": "den-srv"
  },
  "artifacts": [
    {"name": "status", "kind": "json", "handle": "artifact://piw_.../status"},
    {"name": "recent_output", "kind": "text", "handle": "artifact://piw_.../recent-output"}
  ],
  "safe_summary": {
    "last_assistant_summary": "bounded summary",
    "recent_tool_names": ["bash", "edit"],
    "event_count": 42,
    "heartbeat_count": 3
  },
  "created_at": "...",
  "started_at": "...",
  "updated_at": "...",
  "completed_at": null
}
```

Minimum observability fields:

- `run_id`, `project_id`, optional `task_id`, optional `review_round_id`, optional `workspace_id`
- `role`, `state`, `failure_category`, `failure_summary`
- `requested_by`, `worker_identity`, `capability_scope_id`
- `session.mode`, `rerun_of_run_id`
- `host_id`, `tmux_session`, `container_name`, `compose_project`, `pi_session_id`
- artifact handles for `status`, `events`, `stdout`, `stderr`, and `sessions` when available
- bounded recent output/assistant summary
- requested and final repo refs
- timestamps and duration

## Privacy and reasoning boundary

Worker output is untrusted and may contain prompt-injection attempts. Reasoning/session artifacts may be sensitive. The contract therefore separates data into three visibility classes:

1. **Task-thread packets**: concise, human-reviewable, structured summaries. Never include raw reasoning, full prompts, secrets, or full transcripts.
2. **Den run summaries/events**: bounded status, tool names, short output previews, redacted reasoning markers, provider-visible reasoning summaries only when explicitly safe.
3. **Runtime-local artifacts**: raw stdout/stderr/session files and optional raw reasoning previews, accessible only through controlled artifact APIs or host inspection.

Default policy:

- Skip user-role prompt/session messages in Den work feeds.
- Redact raw reasoning by default.
- Store safe summaries/previews with explicit character limits.
- Treat provider-visible reasoning summaries as summaries, not permission to expose raw local reasoning.
- Redact secrets before storing any Den-visible field.

## MCP/API surface proposal

### Raw lifecycle tools

#### `launch_pi_worker`

Purpose: create a Den run, prepare a small state reference, launch a runtime adapter, and return run handles.

Inputs: launch request contract above.

Outputs:

- `run_id`
- `state`
- `worker_identity`
- `capability_scope_id`
- `session` handles (`host_id`, `tmux_session`, `container_name`, `compose_project`, `pi_session_id` when known)
- artifact handles
- compact recovery/observation guidance
- idempotency result (`created`, `existing`, or `conflict`)

#### `get_worker_run`

Return full normalized run detail, including current status, failure category, packet refs, final repo refs, artifact handles, and safe summaries.

#### `list_worker_runs`

Filters:

- `project_id`
- `task_id`
- `role`
- `state`
- `review_round_id`
- `workspace_id`
- `branch`
- `created_since`

#### `abort_worker_run`

Inputs: `run_id`, `requested_by`, `reason`.

Records a durable control request and returns the updated control state. The runtime adapter observes and acts on the request. If the worker is already terminal, return a clear terminal/no-op result.

#### `rerun_worker_run`

Inputs: `run_id`, `requested_by`, optional overrides.

Uses the durable stored launch request, not parent-process memory. Returns a new `run_id` with `rerun_of_run_id` set, or a non-ambiguous rejection reason.

#### `cleanup_worker_runs`

Inputs: filters and dry-run/force options.

Returns what would be or was cleaned. Must not delete live sessions by default.

### Packet and prompt tools

#### `prepare_coder_context_packet`

Server-side equivalent of the Pi coder context helper. It gathers task, dependencies, relevant docs/messages/packets, constraints, validation expectations, and expected output, then posts a task-thread message with metadata:

```json
{"type":"coder_context_packet","prepared_by":"orchestrator","workflow":"den_pi_worker","version":1}
```

Returns `message_id`, metadata, content summary, and a prompt reference usable by launch tools.

#### `prepare_reviewer_context_packet`

Gathers review round, implementation packet, validation/drift packets, diff refs, and focus areas; posts/reuses a task-thread packet.

#### `render_worker_prompt`

Optional helper if role launch tools do not render internally. It accepts a prompt template/guidance slug and packet refs, then writes/returns a state-file reference. It should not encourage callers to copy giant prompt text into process args.

#### `post_worker_completion_packet`

Validates/extracts/posts role completion packets and transitions run state as described above.

#### `get_latest_task_packet`

Lookup helper by `project_id`, `task_id`, `metadata.type`, optional `role`, optional `run_id`, and completeness/status.

### Role launch adapters

#### `launch_coder_worker`

Wrapper that prepares/reuses coder context packet, renders coder prompt/state, selects coder capability preset, calls `launch_pi_worker`, and returns run handles.

#### `launch_reviewer_worker`

Wrapper that resolves review round and reviewed refs, prepares/reuses reviewer context, enforces reviewer identity, calls `launch_pi_worker`, and returns run handles.

#### `launch_validator_worker` / `run_validation_worker`

Runs declared validation commands in bounded runtime context and posts `validation_packet`.

#### `launch_drift_sentinel_worker` / `run_drift_check`

Runs deterministic and optional model-assisted drift checks and posts `drift_check_packet`.

### Observability/status tools

#### `get_task_worker_summary`

Compact task-level view of active/recent workers, latest packet refs, open failure/control actions, and recommended next orchestrator action.

#### `get_worker_recent_output`

Bounded recent stdout/stderr/assistant output. Must respect redaction and preview limits.

#### `get_worker_artifact`

Controlled artifact retrieval by `run_id` and artifact name/handle. Must apply privacy policy; raw artifacts may require local/operator capability and should never be dumped into normal orchestrator context by default.

## Idempotency and dedupe

- Launch tools accept a caller-supplied `dedupe_key` for role launches.
- Packet tools dedupe by `(project_id, task_id, metadata.type, run_id or context hash)`.
- Completion tools dedupe by `(run_id, packet_type, dedupe_key)`.
- Dedupe conflicts must be explicit: returning an existing compatible result is OK; silently creating a second run/packet for the same key is not.

## Missing Den tools/API backlog surfaced by this contract

Downstream tasks should create/implement these missing surfaces rather than adding Hermes scripts:

1. Worker lifecycle MCP tools (`launch_pi_worker`, `get_worker_run`, `list_worker_runs`, `abort_worker_run`, `rerun_worker_run`, `cleanup_worker_runs`).
2. Packet helpers (`prepare_coder_context_packet`, `prepare_reviewer_context_packet`, `get_latest_task_packet`).
3. Completion/status API (`post_worker_completion_packet`) with malformed/missing packet behavior.
4. Role adapters (`launch_coder_worker`, `launch_reviewer_worker`, validator/drift variants).
5. Artifact and recent-output query tools with reasoning/privacy enforcement.
6. Durable capability scope model for worker-scoped Den access.
7. Optional collaboration MCP wrappers if Hermes orchestration needs collaboration sessions; do not use local curl glue as the normal path.

## Downstream implementation guidance

- #1239 should implement the raw lifecycle tools and normalized run projection first, even if the runtime adapter initially reuses existing `SubagentRunService`/Pi session code.
- #1240 should implement packet and prompt-reference helpers in Den MCP, not Hermes helper scripts.
- #1241 should implement `post_worker_completion_packet`, state transitions, final branch/head observation fields, and failure packet behavior.
- #1255 should add role adapters on top of #1239/#1240/#1241 rather than duplicating raw launch logic.
- #1242 and #1243 should convert Hermes orchestration paths to thin calls into Den MCP tools.
- #1247 should smoke-test in stages: raw lifecycle, packet/reference flow, completion/status flow, coder adapter, reviewer adapter, then full loop.

## Non-goals restated

- Do not port Pi stdout parsing, process-tree termination, tmux/docker command construction, or extension UI code into Hermes Python.
- Do not pass large prompts or secrets via process args.
- Do not make tmux the control API; it remains observability/break-glass.
- Do not treat process exit as successful completion without structured packet validation.
- Do not expose raw reasoning/session files in task-thread messages.
