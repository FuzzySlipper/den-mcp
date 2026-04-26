# Agent Conductor Application Notes

Date: 2026-04-25

Status update, 2026-04-26: dispatches are retired from the canonical conductor path; see [ADR: Retire dispatches from the canonical conductor workflow](dispatch-retirement-adr.md). Treat older dispatch language in this note as historical bridge context unless it explicitly says legacy/debug.

This note captures a research sanity check on turning Den from "MCP plus task
storage" into a more explicit agent conductor and observation application.

The immediate motivation is that chat/task streams become inadequate once
sub-agent delegation is a meaningful part of the workflow. A high-volume stream
of agent comments, status messages, failures, artifacts, code changes, and review
handoffs is too cluttered to use as the primary operator surface.

## Recommendation

Den should become an explicit agent operations application whose primary UI is a
web operator console.

MCP should remain one protocol boundary into Den, not the backbone of the
application. The server should own the durable structure: runs, agents,
workspaces, review rounds, terminal views, artifacts, routing, attention states,
and policy decisions. The web UI should render that model, not reconstruct the
model from chat messages.

This is not a recommendation to build a generic agent framework first. The
better shape is a small, concrete application framework around the workflow Den
already has:

- `AgentRun`
- `AgentWorkspace`
- `AgentInstance`
- `TerminalView`
- `Artifact`
- `ReviewDecision`
- `EventStream`
- `PolicyAutomation`

In short:

```text
Den Core:       durable truth, invariants, task/review/run/workspace models
Den Runtime:    spawn/stop/observe agents, worktrees, artifacts, policy decisions
Adapters:       MCP, Pi, Codex, Claude, terminal muxers, local shell
Web Console:    operator cockpit over the above
```

## Why Not Just a Larger MCP Web Frontend

A web frontend over MCP is useful, but it risks becoming a prettier log viewer if
the backend does not own the state model.

The frontend should not need to infer these facts from messages:

- this run is active, stuck, failed, retrying, or complete
- this agent owns this task
- this branch and worktree belong to this run
- these files changed during the run
- this review round is pending because a reviewer run failed
- this output is a valid assistant final answer, prompt echo, partial output, or no output
- this agent is blocked waiting for user input
- this terminal pane is only a view attached to a run

The server should already know those things. Then the UI can focus on high-level
operator questions:

- What is running right now?
- What needs attention?
- Which tasks are blocked?
- Which agents changed code?
- Which changes are ready for review?
- Which failures are infrastructure vs model/output failures?
- Where can I open the terminal/log/diff/artifacts for this run?

## Terminal Ownership

Den should own the session/run lifecycle for conductor-created work. Terminals
should own presentation and human interaction.

Recommended split:

```text
Den owns:       process/session lifecycle, cwd, env, worktree, logs, pid,
                artifacts, state, policy, review linkage

Terminal owns:  display, keyboard interaction, attach/detach, focus,
                pane/window layout

Agents report: structured events, summaries, decisions, questions,
                tool/result state
```

For Pi/Codex/Claude runs launched by Den, the preferred path is a Den-controlled
runner or supervisor. That gives Den hard truth about process start/stop/crash,
pid or process group, stdout/stderr/JSONL logs, cwd/worktree, timeouts, aborts,
artifact paths, final output extraction, and state.

For user-owned terminal sessions, a reporting bridge is still useful. It should
register the live client, publish status, and respond to wake or focus requests.
This mode is less authoritative because the terminal/client can disappear before
Den sees the full lifecycle.

Zellij, tmux, foot server mode, web terminals, and local terminal windows are
best treated as attachable views, not as the conductor brain.

Suggested model:

```text
agent_runs
  id, task_id, role, state, workspace_id, pid, process_group, backend,
  started_at, ended_at, duration_ms, output_status, artifact_set_id

terminal_views
  id, run_id, adapter, session_name, pane_id, window_id, socket_path, cwd,
  launch_command, status, last_seen_at, capabilities
```

Typical interaction:

1. Den launches a run headlessly or through a controlled runner.
2. The dashboard shows run state, changed files, logs, and artifacts.
3. Clicking "Open terminal" creates or focuses a zellij/tmux/foot/web view.
4. The view registers back to Den with run id and pane/window metadata.
5. If the terminal view dies, the run is still knowable from Den state and artifacts.

The invariant to protect: terminal windows are convenient portals, not the memory
of the system.

## Current Den Direction

The current sub-agent work already points in the right direction.

Useful existing pieces:

- `agent_stream_entries` as a thin operational audit stream
- task-thread messages as durable summaries and review handoff records
- legacy dispatch rows as historical/debug bridge artifacts
- Pi sub-agent run lifecycle events such as `subagent_started`, `subagent_timeout`, `subagent_failed`, and `subagent_completed`
- local run artifacts such as `status.json`, `events.jsonl`, `stdout.jsonl`, and `stderr.log`
- normalized sub-agent run summaries and detail routes
- a first web UI surface for active/recent sub-agent runs

The important next move is to keep moving state out of "chat-shaped text" and
into durable domain objects or normalized projections.

## External Repository Takeaways

The repos below were evaluated for ideas to borrow or integrate. The conclusion
is to borrow patterns, not adopt any repo wholesale.

### BloopAI/vibe-kanban

Source: https://github.com/BloopAI/vibe-kanban

Suitability: high product inspiration, low direct dependency.

Helpful patterns:

- Kanban issue planning connected directly to coding-agent execution.
- Workspaces as the primary execution object.
- Each workspace gives an agent a branch, terminal, and dev server.
- Built-in diff review with inline comments.
- Built-in preview browser with devtools, inspect mode, and device emulation.
- Multi-agent backend support across Claude Code, Codex, Gemini CLI, Amp, Cursor,
  OpenCode, and others.
- PR creation and merge workflow from the same operator surface.
- Modular backend structure with crates for server, db, executors, services,
  worktree manager, workspace manager, git, review, preview proxy, desktop bridge,
  relay, and MCP.

What to borrow:

- Treat "workspace" as a first-class object, not just a task plus a process.
- Put branch, terminal, dev server, preview, changed files, and review together.
- Make diff review and agent feedback part of the operator loop.
- Keep agent backend adapters behind a shared execution contract.

Caveat:

- The README says Vibe Kanban is sunsetting, so it is a reference, not a platform
  to depend on.

### johannesjo/parallel-code

Source: https://github.com/johannesjo/parallel-code

Suitability: best concrete reference for near-term mechanics.

Helpful patterns:

- Each task gets its own git branch and worktree.
- Gitignored directories such as `node_modules` can be symlinked into the
  worktree to reduce setup friction.
- Agents are spawned directly inside the task worktree.
- Per-task shell terminals are scoped to the worktree.
- Changed files and diff viewer are attached to the task.
- State persists across restarts.
- A keyboard-first desktop UI makes context switching cheap.

Useful PTY/process details:

- Validate commands before spawn.
- Reject shell metacharacters in command names.
- Filter environment overrides from the renderer.
- Remove nested-agent environment variables before spawning a child agent.
- Batch terminal output and retain scrollback.
- Keep a tail buffer for exit diagnostics.
- Emit explicit spawn/exit/list-changed lifecycle events.
- Include `exit_code`, `signal`, and recent `last_output` on exit.
- Optionally run the agent in Docker with a predictable container name, resource
  limits, mounted cwd, and cleanup on kill.

What to borrow:

- One task -> one branch -> one worktree -> one agent process as the default
  isolation model.
- PTY scrollback and last-output diagnostics as first-class run artifacts.
- Terminal spawn, write, resize, pause/resume, kill, and subscribe operations as
  capabilities on a run/view.
- Environment and command validation before launching agents.
- Optional containerized run mode later, especially for risky automation.

### coollabsio/jean

Source: https://github.com/coollabsio/jean

Suitability: strong product and architecture reference.

Helpful patterns:

- Native desktop app with Tauri, React, Rust, TypeScript, CodeMirror, and xterm.js.
- Multi-project and worktree management.
- Multiple sessions per worktree.
- Execution modes such as Plan, Build, and Yolo, including plan approval flows.
- Session recap/digest and saved contexts with AI summarization.
- Code review with finding tracking.
- GitHub and Linear integrations.
- Multi-dock terminal, command palette, editor integration, git operations, diff
  viewer, file tree, debug panel, and token tracking.
- Headless web access through an embedded HTTP server and WebSocket support.

Useful architecture details:

- "Onion" state architecture: component `useState`, global UI state in Zustand,
  persistent data in TanStack Query.
- Command-centric design: user actions flow through centralized command objects.
- Event-driven bridge: frontend invokes backend commands, backend emits events
  for real-time updates.
- Rust backend modules for chat session lifecycle, detached process recovery,
  active session registry, JSONL tailing, run history logging, project/worktree
  operations, background polling, embedded HTTP server, terminal registry, and
  diagnostics.
- Documented quality gates across TypeScript, linting, tests, Rust formatting,
  clippy, and Rust tests.

What to borrow:

- Command-centric UI actions so keyboard, menu, stream events, and buttons share
  the same behavior.
- Session digest/recap as a first-class thing, especially for finished runs.
- Plan approval and review finding panels.
- Embedded HTTP/WebSocket or local-companion design for "open/focus/attach"
  desktop actions.
- Clear state layering so the frontend is not quietly becoming the domain model.

### joewinke/jat

Source: https://github.com/joewinke/jat

Suitability: idea source, not an architecture to copy.

Helpful patterns:

- A visual IDE that unifies tasks, agent sessions, code editor, git, and terminal.
- Agent session states such as Working, Needs Input, Review, and Completed.
- Smart question UI where agent questions become clickable choices.
- Task -> agent -> review workflow as the central loop.
- Epic Swarm for spawning parallel agents on subtasks.
- Auto-proceed rules based on task type/priority.
- External triggers from maintained adapters such as Slack, RSS, Gmail, custom plugins, and a scheduler.
- One-agent-one-task command model with `/jat:start`, `/jat:complete`,
  `/jat:pause`, `/jat:verify`, and `/jat:status`.
- File reservations and conflict checks before starting work.
- Scheduler daemon for recurring or delayed task spawns.

What to borrow:

- Attention states and smart questions as a UI primitive.
- Auto-proceed policy rules, but only after Den has reliable run/review state.
- File reservations or changed-file conflict detection for parallel work.
- Scheduled/recurring automation as a later Den runtime feature.

Caveat:

- JAT leans heavily on tmux, shell tools, and a bundled local tool universe. Den's
  current typed server/core model is cleaner for durable cross-agent workflow.

## Candidate Den Domain Objects

### AgentRun

First-class record for a single execution attempt.

Fields to consider:

- `id`
- `project_id`
- `task_id`
- `review_round_id`
- `role`
- `backend`
- `model`
- `agent_instance_id`
- `workspace_id`
- `state`
- `started_at`, `ended_at`, `duration_ms`
- `pid`, `process_group`
- `exit_code`, `signal`
- `timeout_kind`
- `failure_classification`
- `output_status`
- `artifact_set_id`
- `summary`

The current derived sub-agent run summaries are a good bridge. Once concurrency
and retention matter, promote this into a real table or durable snapshot.

### AgentWorkspace

Execution context for code-changing work.

Fields to consider:

- `id`
- `project_id`
- `task_id`
- `branch`
- `worktree_path`
- `base_branch`
- `base_commit`
- `head_commit`
- `state`
- `dev_server_url`
- `preview_url`
- `created_by_run_id`
- `cleanup_policy`
- changed-file summary

This is the biggest missing concept if Den is going to conduct coding agents
rather than only track tasks and messages.

### TerminalView

Attachable human-facing view of an `AgentRun`.

Fields to consider:

- `id`
- `run_id`
- `adapter`
- `session_name`
- `pane_id`
- `window_id`
- `socket_path`
- `cwd`
- `launch_command`
- `status`
- `capabilities`
- `last_seen_at`

Adapters could include:

- web terminal
- zellij
- tmux
- foot
- local terminal app
- log-only view

### ArtifactSet

Durable run evidence.

Fields to consider:

- `id`
- `run_id`
- `status_json_path`
- `events_jsonl_path`
- `stdout_jsonl_path`
- `stderr_log_path`
- `transcript_path`
- `diff_path`
- `summary_path`
- retention policy

Near-term API additions:

- fetch artifact metadata
- tail stderr/stdout
- view lifecycle events
- download or inspect full transcript

### AttentionItem

Operator-facing queue item.

Examples:

- agent asked a question
- run failed infrastructure checks
- reviewer produced changes requested
- reviewer run failed and review remains pending
- merge blocked because branch no longer matches reviewed head
- task stuck running longer than expected
- dispatch could not route to an agent

This should be derived from durable facts where possible, not manually typed into
chat.

## Suggested Implementation Sequence

1. Keep hardening sub-agent run lifecycle.
   - Preserve structured ops events.
   - Keep task-thread messages concise and outcome-oriented.
   - Add artifact read/tail endpoints so the web UI does not only show paths.

2. Promote run state when the projection starts to strain.
   - Today, grouping `agent_stream_entries` by `metadata.run_id` is fine.
   - Once runs need retention, indexing, active state, artifact browsing, or
     cross-run queries, add a first-class `agent_runs` table.

3. Add `AgentWorkspace`.
   - Track branch, worktree, base/head commits, and changed files.
   - Make workspace the place where terminal, dev server, preview, diff, and
     review attach.

4. Add terminal view registration.
   - Start with a simple registry and no dependency on zellij/tmux.
   - Let adapters register `open`, `focus`, `attach`, `send_input`, `resize`,
     `close_view`, and `capture_scrollback` capabilities.

5. Build the web console around operator attention.
   - Active runs.
   - Needs-input items.
   - Failed runs.
   - Ready-for-review workspaces.
   - Changed files and diff entry points.
   - Artifact/log drill-down.

6. Add local desktop companion behavior if needed.
   - A server can store and expose session truth.
   - A local companion can safely handle GUI focus/open actions for foot, tmux,
     zellij, VS Code, Zed, or other desktop tools.

7. Add policy automation last.
   - Auto-rerun on infrastructure failure.
   - Auto-request review after successful implementation.
   - Auto-proceed only for low-risk tasks with clean tests and valid review state.
   - Escalate to the user on ambiguity.

## Invariants To Keep

- Task-thread messages are durable summaries and decisions, not high-frequency
  telemetry.
- `agent_stream_entries` are operational events and lightweight messages, not a
  global chat room.
- Agent-stream/run state and future attention items are the normal wake/visibility path; dispatches are legacy/debug artifacts.
- Terminal panes are attachable views, not the source of truth.
- Den should still know what happened if a terminal window dies.
- Failed or partial reviewer runs are not review verdicts.
- Runs should link to tasks, review rounds, workspaces, terminal views, and
  artifacts.
- The dashboard should show attention and risk before raw chatter.
- External tools should be adapters, not the architecture center.

## Planning Questions

- When should derived sub-agent run summaries become a real `agent_runs` table?
- Should `AgentWorkspace` be introduced before or after terminal view registry?
- Which terminal adapter is worth proving first: web terminal, tmux, zellij, foot,
  or generic "open local terminal here"?
- Does Den need a local desktop companion for focus/open/window-management actions?
- What is the minimum artifact browser that makes the current sub-agent run panel
  genuinely useful?
- How should Den represent "needs input" so agent questions do not disappear into
  logs?
- Which policies are safe enough to automate, and which must remain operator
  decisions?

## Bottom Line

The right move is not "more web frontend to MCP." It is "Den as an agent conductor
application with MCP as one interface."

The web UI is still the best operator surface, but the backend needs to own the
workflow structure. That matches what the useful external projects converge on:
durable tasks, isolated workspaces, supervised runs, terminals as views, explicit
review/diff surfaces, and a dashboard optimized for attention rather than chat.

## Sources Checked

- BloopAI/vibe-kanban: https://github.com/BloopAI/vibe-kanban
- vibe-kanban README: https://raw.githubusercontent.com/BloopAI/vibe-kanban/main/README.md
- vibe-kanban Cargo workspace: https://raw.githubusercontent.com/BloopAI/vibe-kanban/main/Cargo.toml
- johannesjo/parallel-code: https://github.com/johannesjo/parallel-code
- parallel-code README: https://raw.githubusercontent.com/johannesjo/parallel-code/main/README.md
- parallel-code PTY implementation: https://raw.githubusercontent.com/johannesjo/parallel-code/main/electron/ipc/pty.ts
- coollabsio/jean: https://github.com/coollabsio/jean
- Jean README: https://raw.githubusercontent.com/coollabsio/jean/main/README.md
- Jean architecture guide: https://raw.githubusercontent.com/coollabsio/jean/main/docs/developer/architecture-guide.md
- joewinke/jat: https://github.com/joewinke/jat
- JAT README: https://raw.githubusercontent.com/joewinke/jat/master/README.md
- JAT scheduler docs: https://raw.githubusercontent.com/joewinke/jat/master/shared/scheduler.md
- JAT command reference: https://raw.githubusercontent.com/joewinke/jat/master/COMMANDS.md
