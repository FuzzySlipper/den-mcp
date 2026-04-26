# Global Agent Stream Design

Date: 2026-04-23

Status update, 2026-04-26: dispatches are no longer the canonical wake/work queue. See [ADR: Retire dispatches from the canonical conductor workflow](dispatch-retirement-adr.md). Signal/Telegram mobile bridges and Codex/Claude dispatch bridges are retired from the supported runtime; see [Legacy Mobile Bridge Integrations](legacy-mobile-bridges.md) and [Legacy Codex/Claude Bridge Notes](legacy-codex-claude-bridges.md). Historical dispatch/bridge references in this document describe the original bridge design; the current normal path is Pi/Den web plus task/thread records and agent-stream ops/run state.

This note proposes a Den-native global agent stream for thin operational
handoffs and targeted lightweight messages across all projects.

The intent is to make agent-to-agent coordination visible and auditable without
turning adapter transports or retired bridge experiments into the primary
record of workflow state.

## Why add this layer

Den already has two useful coordination primitives:

- task-thread messages for rich context and durable discussion
- legacy dispatches for bridge compatibility when explicitly enabled

Those primitives still do not give us one obvious place to watch the
system-level workflow:

- who handed work to whom
- which project/role/instance was targeted
- whether a review loop is bouncing repeatedly
- whether an agent asked for clarification and got an answer
- whether a wake was requested, delivered, or ignored

The proposed agent stream fills that gap.

It is not a replacement for task-thread messages, and it is not a second work queue. It is the thin global event layer that lets us observe and route inter-agent activity across all projects from one place.

## Goals

- Keep a single global feed of agent coordination activity across all projects.
- Make routine baton-pass events easy to audit in the web UI.
- Distinguish low-noise operational events from optional targeted freeform
  messages without forcing separate storage systems in v1.
- Support multiple live instances of the same agent family across different
  projects and roles.
- Keep task-thread messages as the source of detailed work context.
- Keep legacy dispatch rows inspectable for compatibility/debugging without treating them as the default queue.
- Leave the door open for future `@user` and `@agent` clarification flows.

## Non-goals

- Replacing threaded task messages with a global chat room.
- Moving review findings, planning details, or implementation context out of
  task threads.
- Making any external mobile/chat bridge the primary inter-agent bus.
- Forcing all entries into rigid structured event types forever.
- Building a full Slack-like compose/reply UX in v1.

## Core proposal

Use one append-only global table, `agent_stream_entries`, and expose two
logical views over it:

- `ops`
  - structured, low-noise workflow events
  - default view in the UI
- `message`
  - optional targeted freeform messages
  - intended for clarifications and exceptions, not routine workflow chatter

This keeps the storage and audit model simple:

- one table
- one global feed
- two norms

The system behavior should encourage a social convention:

- routine workflow belongs in structured ops events plus task-thread messages
- freeform stream messages are allowed, but should be used sparingly

## Relationship to existing Den primitives

### Task-thread messages

Task-thread messages remain the source of truth for detailed work context.

Examples:

- review packet contents
- findings and code references
- planning details
- merge notes
- long-form clarification

An agent stream entry may point at a task thread, but it should not duplicate
the full content of that thread.

### Legacy dispatches

Dispatches are preserved as historical/legacy bridge rows, not the durable queue for normal agent work.

The stream records that a handoff or attention-worthy event happened. Bridges should consume agent-stream/attention state or explicit task/thread context by default; only legacy deployments that opt into `defaults.legacy_dispatch_enabled: true` should create or consume dispatch rows.

### Bridges and channels

The stream is Den-owned. Delivery remains adapter-specific:

- Pi/conductor runs and Den web/operator views are the supported active path.
- Retired Codex/Claude bridge experiments (`den-codex-bridge`, `den-agent`, Claude channel snippets) are historical examples only.
- Future adapters should consume Den-owned stream/attention/task context rather than becoming a workflow record.

The stream should therefore be treated as a coordination layer, not a transport
implementation.

## Data model

### `agent_stream_entries`

Recommended schema:

```sql
CREATE TABLE agent_stream_entries (
    id                    INTEGER PRIMARY KEY AUTOINCREMENT,
    stream_kind           TEXT NOT NULL
                          CHECK (stream_kind IN ('ops', 'message')),
    event_type            TEXT NOT NULL,
    project_id            TEXT REFERENCES projects(id) ON DELETE CASCADE,
    task_id               INTEGER REFERENCES tasks(id) ON DELETE SET NULL,
    thread_id             INTEGER REFERENCES messages(id) ON DELETE SET NULL,
    dispatch_id           INTEGER REFERENCES dispatch_entries(id) ON DELETE SET NULL,
    sender                TEXT NOT NULL,
    sender_instance_id    TEXT,
    recipient_agent       TEXT,
    recipient_role        TEXT,
    recipient_instance_id TEXT,
    delivery_mode         TEXT NOT NULL DEFAULT 'record_only'
                          CHECK (delivery_mode IN ('record_only', 'notify', 'wake')),
    body                  TEXT,
    metadata              TEXT,
    dedup_key             TEXT,
    created_at            TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_agent_stream_created
    ON agent_stream_entries(created_at DESC);

CREATE INDEX idx_agent_stream_project_created
    ON agent_stream_entries(project_id, created_at DESC);

CREATE INDEX idx_agent_stream_recipient_created
    ON agent_stream_entries(recipient_instance_id, recipient_agent, recipient_role, created_at DESC);

CREATE INDEX idx_agent_stream_task_created
    ON agent_stream_entries(task_id, created_at DESC);

CREATE INDEX idx_agent_stream_kind_created
    ON agent_stream_entries(stream_kind, created_at DESC);

CREATE UNIQUE INDEX idx_agent_stream_dedup
    ON agent_stream_entries(dedup_key)
    WHERE dedup_key IS NOT NULL;
```

### Column intent

- `stream_kind`
  - distinguishes `ops` from `message`
- `event_type`
  - closed enum for `ops`
  - narrower controlled vocabulary for `message`
- `project_id`
  - nullable so truly global/system entries are allowed
- `task_id`, `thread_id`, `dispatch_id`
  - links to the richer Den record instead of duplicating it
- `sender`, `sender_instance_id`
  - identify who emitted the entry and which live instance did so
- `recipient_agent`, `recipient_role`, `recipient_instance_id`
  - targeting hints for routing and audit
- `delivery_mode`
  - `record_only`: never wake anyone
  - `notify`: surface to relevant adapters or UI, but do not begin work
  - `wake`: suitable to create/trigger delivery for the targeted recipient
- `body`
  - compact summary or freeform message body
- `metadata`
  - JSON payload for structured details that do not deserve first-class columns
- `dedup_key`
  - prevents bridge/plugin retries from flooding the stream with identical
    records

## Agent binding model

To route a global stream entry to the correct live instance, Den needs an
explicit binding model. The current active-agent view is a good starting point,
but the stream design benefits from a more explicit registry.

Recommended schema:

```sql
CREATE TABLE agent_instance_bindings (
    instance_id      TEXT PRIMARY KEY,
    agent_family     TEXT NOT NULL,
    agent_identity   TEXT NOT NULL,
    project_id       TEXT REFERENCES projects(id) ON DELETE CASCADE,
    role             TEXT,
    transport_kind   TEXT NOT NULL
                     CHECK (transport_kind IN (
                         'pi_conductor',
                         'local_adapter',
                         'manual_mcp',
                         'other'
                     )),
    session_id       TEXT,
    status           TEXT NOT NULL
                     CHECK (status IN ('starting', 'idle', 'busy', 'degraded', 'offline')),
    metadata         TEXT,
    created_at       TEXT NOT NULL DEFAULT (datetime('now')),
    updated_at       TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX idx_agent_bindings_project_role
    ON agent_instance_bindings(project_id, role, updated_at DESC);

CREATE INDEX idx_agent_bindings_family_project
    ON agent_instance_bindings(agent_family, project_id, updated_at DESC);
```

This is what allows a single global stream to coexist with:

- multiple Pi/conductor or future adapter instances
- multiple projects
- project-local reviewer/implementer pairings

## Event taxonomy

### `ops` stream

The `ops` view should use a closed event taxonomy. It exists for monitoring and
automation, so consistency matters more than expressiveness.

Recommended v1 event types:

| Event type | Meaning |
| --- | --- |
| `dispatch_created` | Den created a dispatch for an agent. |
| `dispatch_approved` | A pending dispatch became actionable. |
| `dispatch_rejected` | A dispatch was rejected and should not run. |
| `wake_requested` | A wake was requested through a trusted path. |
| `wake_delivered` | A bridge/channel accepted the wake for delivery. |
| `wake_dropped` | Wake could not be delivered due to ambiguity, policy, or invalid state. |
| `review_requested` | Implementer handed a task to a reviewer. |
| `review_started` | Reviewer began the requested review. |
| `changes_requested` | Reviewer sent blocking or substantive feedback. |
| `rereview_requested` | Implementer asked for another review pass. |
| `review_approved` | Reviewer approved the reviewed head. |
| `merge_handoff` | Reviewed work was handed back for merge. |
| `clarification_requested` | An agent asked a targeted question outside the main task thread. |
| `clarification_answered` | A targeted clarification reply was recorded. |
| `agent_busy` | Agent or bridge entered busy state. |
| `agent_idle` | Agent or bridge returned to idle state. |
| `agent_error` | Transport or workflow error worth operator attention. |

Not every one of these must wake an agent. Most are audit entries first.

### `message` stream

The `message` view is for targeted freeform text that should remain globally
visible and auditable, but should not become the default place where work
details are stored.

Recommended v1 message subtypes in `event_type`:

| Event type | Meaning |
| --- | --- |
| `note` | Thin targeted note with no wake behavior. |
| `question` | Clarification request that may need a reply. |
| `answer` | Direct response to a prior targeted question. |
| `nudge` | Explicit “check Den” or “please look now” hint. |

Unlike `ops`, `message.body` is expected to contain human-readable text.

## Routing and delivery rules

### Resolution order

When a stream entry is intended to reach a live agent, Den should resolve the
recipient in this order:

1. `recipient_instance_id`
   - exact target
2. `project_id + recipient_role`
   - preferred for coder/reviewer workflows
3. `project_id + recipient_agent`
   - acceptable when exactly one matching live binding exists
4. `recipient_agent` alone
   - only if this resolves unambiguously

If resolution is ambiguous, Den should:

- record a `wake_dropped` or `agent_error` ops entry
- not wake any instance

Silent best-guess routing would make the stream impossible to trust.

### Delivery rules

- `record_only`
  - append to the stream
  - visible in UI
  - no wake attempt
- `notify`
  - append to the stream
  - surface through adapters or notifications if configured
  - no work-start implied
- `wake`
  - append to the stream
  - attempt delivery through the relevant local adapter
  - if real work is required, create or reuse a dispatch and let the bridge
    consume authoritative Den context from there

### Internal vs external trust

Trusted internal workflow events should not require a human approval click for
routine delivery.

Examples:

- implementer requests review
- reviewer starts review
- reviewer sends changes requested
- implementer asks for rereview
- reviewer approves and hands back for merge

Those should auto-emit to the stream and auto-deliver according to routing
rules.

Human approval is still appropriate for less trusted ingress:

- retired/experimental external bridge wakeups, if a future adapter reintroduces them
- public webhook-like sources
- unknown sender identities

The point is to stop asking the operator to say “okay” on every normal handoff.

## Normal workflow mapping

The intended relationship between task messages and stream entries is:

1. Implementer posts a full review request on the task thread.
2. Den emits a compact `review_requested` ops entry linked to the same
   `task_id` and `thread_id`.
3. Reviewer/conductor attention is represented through agent-stream/run state,
   role-aware targeted messages, or future first-class attention items.
4. Reviewer posts detailed findings or approval on the task thread.
5. Den emits `changes_requested` or `review_approved` plus the appropriate
   handoff event.
6. Implementer follows the task-thread handoff/attention item rather than a
   pending dispatch queue.

This keeps the stream thin while making the workflow globally visible.

## Future clarification flow

The design should deliberately leave room for flows like:

- `@user should I do X?`
- `@pi yes, do X`

That does not require chat-first UX in v1. It only requires:

- a global targeted `message` stream entry
- sender/recipient identity
- optional `wake` delivery mode
- links back to the relevant task/thread

The detailed answer should still be copied or summarized into the relevant task
thread when it materially changes task context.

## Monitoring and loop detection

The biggest operational value of the stream is monitoring unhealthy loops
without forcing manual approval on every normal cycle.

Recommended derived checks:

- repeated `changes_requested` -> `rereview_requested` -> `changes_requested`
  loop on the same task beyond a threshold
- repeated wakes to the same instance with no `review_started`, `agent_busy`,
  or dispatch completion evidence
- frequent `wake_dropped` because routing is ambiguous
- bridges repeatedly entering `degraded` or `agent_error`
- long gaps between `dispatch_approved` and any sign of pickup

These can start as server-side derived warnings or dashboard badges. They do
not need to block workflow in v1.

## Web UI shape

The web dashboard should treat the stream as global first, project-filterable
second.

Recommended UI:

- new top-level “Agent Stream” panel or page
- default filter to `ops`
- optional `messages` filter or tab
- filters for:
  - project
  - sender
  - recipient
  - task
  - stream kind
  - event type
- each entry should show compact links to:
  - task
  - thread
  - dispatch
  - recipient binding when known

The default operator view should feel like an audit/event log, not like a chat
room.

## API and MCP surface

Recommended first-pass tools/endpoints:

- `list_agent_stream_entries`
  - filters: `project_id`, `task_id`, `sender`, `recipient`, `stream_kind`,
    `event_type`, `since`, `limit`
- `send_agent_stream_entry`
  - explicitly append a targeted entry
- `get_agent_stream_entry`
  - fetch a single entry with resolved links

Possible future tools:

- `ack_agent_stream_entry`
  - for explicit human/agent acknowledgement
- `list_agent_stream_alerts`
  - derived loop/stall warnings

For REST, mirror these under `/api/agent-stream`.

## V1 scope recommendation

V1 should stay operationally focused.

Recommended scope:

- add `agent_stream_entries`
- auto-emit structured `ops` entries from existing review/dispatch workflow
- add a global web view with filtering
- support targeted `message` rows at the schema/API level
- do not build a rich chat composer yet
- do not move work details out of task threads

This is enough to prove the audit/monitoring value without committing Den to a
chat-heavy workflow too early.

## Suggested implementation slices

### Slice 1: schema and read APIs

- add `agent_stream_entries`
- add read endpoints and MCP tools
- add a basic web feed

### Slice 2: auto-emitted ops events

- emit review and dispatch lifecycle events automatically
- link stream records to task/thread/dispatch ids

### Slice 3: recipient binding model

- formalize live instance bindings and role-based resolution
- surface ambiguity clearly

### Slice 4: targeted message support

- add `send_agent_stream_entry`
- support `question` / `answer` / `nudge`
- keep wake behavior opt-in and explicit

### Slice 5: monitoring and alerts

- add derived loop/stall detection
- show warnings in the dashboard

### Slice 6: bridge integration

- Pi/Den web consumes run state and future attention items as the supported operator path
- Codex/Claude dispatch bridges remain retired legacy experiments unless a future adapter is explicitly reintroduced under a new design
- retired mobile bridges stay out of the supported runtime unless a future adapter is explicitly reintroduced

## Design summary

The best shape is:

- one global Den-owned agent stream
- two logical views: `ops` and `message`
- task-thread messages remain the place for detailed context
- agent-stream/run state and future attention items are the normal wake/visibility path
- legacy dispatch rows remain inspectable for compatibility/debugging
- bridges/channels stay adapter-specific
- retired mobile bridges are historical examples, not supported runtime transports

That gives us the operational visibility we want today without closing the door
on future targeted clarifications tomorrow.
