# Codex App-Server Bridge Plan

Date: 2026-04-20

Status update, 2026-04-26: this dispatch-centered bridge plan is historical/legacy context. The canonical conductor workflow now uses task/thread messages plus agent-stream/run state; see [ADR: Retire dispatches from the canonical conductor workflow](dispatch-retirement-adr.md).

This note replaces the earlier tmux-conductor direction for the current
experimentation path. The new goal is to validate a per-project Codex
app-server bridge that can accept wake-ups from Telegram, queue work while
Codex is busy, and load real task context from Den when the agent is ready.

## Why pivot

The tmux-backed conductor solved "how do we notice work and launch agents," but
it still treated terminal/session control as the main integration seam.

For Codex specifically, the stronger seam is now the app-server transport:

- Codex app-server is an officially documented JSON-RPC interface for rich
  clients.
- `codex --remote ws://...` can attach a TUI to an existing app-server session.
- A persistent bridge can observe whether Codex is idle or busy and choose to
  queue work instead of injecting into a live turn.
- Telegram can stay a wake/notify surface rather than becoming a second source
  of truth for prompt content.

This also matches the desired behavior better than interrupting a live turn:

- avoid token-burning startup loops
- avoid prompt spam while an agent is already working
- keep Den as the place where pending work is actually queued and read

## Goals

- Keep one managed Codex session per project, not one global session per
  machine.
- Allow multiple projects to each have their own Codex bridge at the same time.
- Never interrupt an in-progress Codex turn to deliver new work.
- Queue newly arrived work while Codex is busy and drain that queue when the
  current slice ends.
- Let Telegram wake the bridge with a small payload such as project id and
  dispatch id.
- Pull authoritative task/message/dispatch context from Den at execution time.
- Keep a fallback path that can still launch fresh Codex work without the
  bridge.

## Non-goals

- Building a generic bridge for every coding CLI in this first pass.
- Mid-turn prompt injection as the default behavior.
- Making Telegram carry full prompt text or become a workflow database.
- Solving OMP integration now.

## Session model

The bridge scope is one project at a time.

- One bridge daemon per Den project.
- One managed Codex app-server instance per Den project.
- One active Codex thread per bridge-managed project session in the MVP.
- A user may still have other Codex sessions in other projects; that is normal.

This avoids the main limitation in `agent-bridge`, which uses fixed ports and a
single machine-wide instance. Den should use project-scoped runtime state
instead.

Suggested identity:

- project key: Den project id
- state dir: `~/.local/state/den-codex/<project_id>/`
- transport: prefer project-local socket or dynamically allocated localhost
  ports recorded in state, not hard-coded global ports

State should include:

- daemon pid
- app-server listen endpoint
- last known Codex thread id
- busy/idle status
- pending dispatch ids or a compact queue marker
- crash-loop counters and last failure timestamps

## Core architecture

Use four layers:

1. Den
   - remains the source of truth for tasks, messages, dispatches, and context
2. Telegram transport
   - delivers a tiny wake event such as `{project_id, dispatch_id}`
3. `den-codex-bridge`
   - long-lived per-project daemon
   - owns Codex app-server lifecycle
   - tracks busy/idle state
   - queues pending work while busy
   - starts the next Codex turn when idle
4. Codex TUI
   - optional foreground client attached via `--remote`
   - useful for visibility and human steering
   - not required to keep the bridge session alive

## Queue-first behavior

The bridge should never inject new work into an active turn.

Rules:

- If Codex is idle and a new approved dispatch arrives, enqueue it and start one
  new turn immediately.
- If Codex is busy, enqueue the dispatch id and do nothing else.
- When the active turn completes, drain queued work once.
- When draining, fetch current Den context instead of replaying every wake-up
  prompt.
- If several queued dispatches point at the same task/thread, coalesce them
  where safe and prefer the newest authoritative state from Den.

This is the behavior we want to reinforce in `AGENTS.md` as well: finish the
current slice, then check Den again before going idle.

## Den contract

The bridge should rely on Den for state, not on Telegram payload text.

Recommended flow:

1. Telegram wake hits the bridge with project id and dispatch id.
2. Bridge records the dispatch id in its local queue.
3. When ready to act, the bridge reads:
   - `get_dispatch_context`
   - relevant task/thread/messages
   - optionally `next_task` if no specific dispatch remains actionable
4. Bridge starts a Codex turn with a small prompt that tells Codex to load the
   structured context from Den rather than trusting stale injected prose.

The wake text should be small and stable. The real handoff stays in Den.

## Telegram boundary

Telegram should be wake-only in the MVP.

Good uses:

- notify that work is waiting
- approve or reject dispatches
- trigger the bridge to re-check Den

Avoid:

- embedding the full dispatch prompt in Telegram
- carrying branch/task state that Den already stores
- attempting to mirror the full review loop in chat messages

## Bridge turn policy

The bridge should start a Codex turn only when all of the following are true:

- the bridge is connected to a healthy app-server session
- no active turn is in progress
- at least one pending dispatch or queued wake exists
- crash-loop guardrails are not tripped

The first turn prompt should be bridge-owned and minimal, for example:

- say that queued work is available for the current project
- instruct Codex to query Den for the dispatch id and current context
- remind Codex to finish the current slice before checking for more queued work

## Crash-loop guardrails

To avoid burning tokens and thrashing:

- cap automatic relaunch attempts over a short window
- stop auto-starting after repeated immediate failures
- mark bridge status as degraded/error and require manual re-entry after the cap
- never create a new turn just because a previous turn produced no durable work
- dedupe repeated wake-ups for the same dispatch id

The bridge should fail boringly: queue work, surface a warning, and stop
spamming Codex.

## Fallback path

The bridge is not the only path.

If the app-server bridge is unavailable:

- keep the approved dispatch in Den
- allow a manual or scripted fallback such as
  `den-agent codex resume --last "<dispatch prompt>"`
- keep Telegram notifications working even if the bridge is down

This makes the experiment reversible and keeps Den useful before the bridge is
fully trusted.

## Implementation slices

### Slice 1: design and local validation

- write this plan
- tighten `AGENTS.md` queue/drain guidance
- verify current Codex app-server and remote connection behavior against docs

### Slice 2: local bridge daemon

- add a small `den-codex-bridge` prototype outside the server
- start `codex app-server`
- track busy/idle state from JSON-RPC notifications
- persist per-project runtime state

### Slice 3: Den queue integration

- enqueue dispatch ids from Den-driven wake events
- drain the queue only when Codex is idle
- fetch real handoff context from Den before each turn

### Slice 4: Telegram wake path

- accept a minimal wake payload from Telegram
- authenticate it
- translate it into queue updates for the right project bridge

### Slice 5: operator UX

- status command for bridges by project
- show idle/busy/degraded state
- expose the currently attached thread id and queue depth

## Open questions

- Should the first prototype use WebSocket app-server transport or stdio with a
  local proxy layer?
- Which exact app-server notifications are the most reliable for idle/busy turn
  tracking in practice?
- Should the queue be purely local to the bridge, or mirrored in Den metadata
  for observability?
- Do we want one Codex thread per project forever, or should some dispatches
  intentionally fork/resume another thread?

## Sources

- OpenAI Codex app-server docs:
  https://developers.openai.com/codex/app-server
- OpenAI Codex remote connections docs:
  https://developers.openai.com/codex/remote-connections
- `agent-bridge` reference implementation:
  https://github.com/raysonmeng/agent-bridge
- `agent-bridge` Codex adapter:
  https://raw.githubusercontent.com/raysonmeng/agent-bridge/master/src/codex-adapter.ts
- Codex issue discussing outbound queue/backpressure in persistent app-server
  sessions:
  https://github.com/openai/codex/issues/18203
