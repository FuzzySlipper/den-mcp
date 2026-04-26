# First-Class Message Intents

Task #617 proposes promoting message intent from optional `metadata.type`
convention into a first-class `messages.intent` field. This note captures the
recommended taxonomy, migration plan, and implementation split.

Status update, 2026-04-26: dispatch creation described here is now legacy bridge behavior and is disabled unless `dispatch-routing` explicitly sets `defaults.legacy_dispatch_enabled: true`. The canonical path is task/thread messages plus agent-stream ops/review records; see [ADR: Retire dispatches from the canonical conductor workflow](dispatch-retirement-adr.md).

## Why

Today Den stores messages as freeform markdown plus optional JSON metadata:

- `DispatchDetectionService` extracts `metadata.type` and
  `metadata.recipient` by convention.
- `PromptGenerationService` branches on raw strings like
  `planning_summary`, `review_feedback`, and `merge_request`.
- `ReviewWorkflowService` emits several packet-like messages whose meaning is
  known only by reading `metadata.type`.

That works, but it keeps a workflow-critical concept inside an untyped JSON
blob. The result is brittle routing, harder filtering, and message UIs that
cannot distinguish a review handoff from a note without inspecting metadata or
parsing prose.

The goal of `messages.intent` is to answer one stable question directly:

> What kind of message is this, at the workflow level?

`intent` should carry the durable workflow meaning. `metadata` should keep
payload-specific details.

## Goals

- Make message intent queryable and filterable without string-sniffing
  `metadata`.
- Give dispatch/routing/prompt generation a stable first-class field to branch
  on.
- Preserve backward compatibility with existing messages and existing clients
  during rollout.
- Keep richer packet payloads in `metadata` instead of flattening everything
  into columns.

## Non-goals

- Replacing threaded messages with a new envelope model.
- Eliminating structured metadata payloads for review rounds, findings, or
  handoff details.
- Inferring intent from message body text. Backfill should use `metadata.type`
  when present and default otherwise.

## Proposed Taxonomy

Use a closed enum with a single fallback:

| Intent | Meaning | Existing / legacy mappings |
| --- | --- | --- |
| `general` | Fallback for uncategorized or unknown messages. No implied workflow action. | missing `metadata.type`, unknown values |
| `note` | Informational note or lightweight comment with no explicit next action. | `note`, `comment` |
| `status_update` | Progress or state update that is not a handoff, ready signal, or blocked signal. | `status_update`, `merge_complete`, future explicit writes |
| `question` | Sender needs an answer before proceeding. | future explicit writes |
| `answer` | Direct response to a prior question. | future explicit writes |
| `handoff` | Explicit baton-pass or planning/context handoff outside formal review. | `planning`, `planning_summary` |
| `review_request` | Request for initial review or rereview. | `review_request`, `review_request_packet`, `rereview_packet`, `plan_review` |
| `review_feedback` | Reviewer findings or “changes needed” handoff back to implementer. | `review_feedback`, `review_findings_packet` |
| `review_approval` | Approved review / merge handoff. | `review_approval`, `merge_request` |
| `task_ready` | Task is ready for someone else to pick up. | `task_ready` |
| `task_blocked` | Task is blocked and needs intervention. | `task_blocked` |

### Taxonomy notes

- `general` is the only catch-all. We should not reintroduce free-string
  intent via arbitrary new values.
- `handoff` is intentionally broader than `planning_summary`. It covers
  “here is the context, you take it from here” messages without needing a new
  enum member for every subtype.
- `review_request`, `review_feedback`, and `review_approval` are first-class
  because they drive the coder/reviewer workflow and already have specialized
  prompt handling.
- `task_ready` and `task_blocked` stay explicit even though they could be
  modeled as `status_update`; they are actionable workflow states and should be
  easy to filter separately.

## Canonical Model

Recommended end-state:

- Add `MessageIntent` enum in Core.
- Add `Intent` to `DenMcp.Core.Models.Message`.
- Serialize intent as snake_case strings on REST/MCP/TS surfaces.
- Add optional `intent` parameter to `send_message`.
- Add optional `intent` filter to `get_messages`.

Example wire shape:

```json
{
  "id": 412,
  "project_id": "den-mcp",
  "task_id": 658,
  "thread_id": 294,
  "sender": "codex",
  "intent": "review_feedback",
  "content": "Findings addressed and ready for rereview.",
  "metadata": {
    "recipient": "claude-code",
    "review_round_id": 2,
    "handoff_kind": "review_feedback"
  },
  "created_at": "2026-04-13T07:12:00"
}
```

## Schema Plan

Add a first-class column:

```sql
ALTER TABLE messages
ADD COLUMN intent TEXT NOT NULL DEFAULT 'general'
    CHECK (intent IN (
        'general',
        'note',
        'status_update',
        'question',
        'answer',
        'handoff',
        'review_request',
        'review_feedback',
        'review_approval',
        'task_ready',
        'task_blocked'
    ));
```

Recommended supporting index:

```sql
CREATE INDEX IF NOT EXISTS idx_messages_project_task_intent_created
    ON messages(project_id, task_id, intent, created_at DESC);
```

### Why `NOT NULL DEFAULT 'general'`

- Existing rows get a valid value immediately.
- New writers that have not been updated yet still store a valid intent.
- The field stays usable for querying even during mixed-version rollout.

## Backfill Strategy

Backfill should be explicit and canonical, not a raw copy of `metadata.type`.

Pseudo-SQL:

```sql
UPDATE messages
SET intent = CASE json_extract(metadata, '$.type')
    WHEN 'note' THEN 'note'
    WHEN 'comment' THEN 'note'
    WHEN 'planning' THEN 'handoff'
    WHEN 'planning_summary' THEN 'handoff'
    WHEN 'review_request' THEN 'review_request'
    WHEN 'review_request_packet' THEN 'review_request'
    WHEN 'rereview_packet' THEN 'review_request'
    WHEN 'plan_review' THEN 'review_request'
    WHEN 'review_feedback' THEN 'review_feedback'
    WHEN 'review_findings_packet' THEN 'review_feedback'
    WHEN 'review_approval' THEN 'review_approval'
    WHEN 'merge_request' THEN 'review_approval'
    WHEN 'task_ready' THEN 'task_ready'
    WHEN 'task_blocked' THEN 'task_blocked'
    WHEN 'question' THEN 'question'
    WHEN 'answer' THEN 'answer'
    WHEN 'status_update' THEN 'status_update'
    WHEN 'merge_complete' THEN 'status_update'
    ELSE intent
END;
```

Notes:

- This preserves the default `general` for missing or unknown types.
- No content parsing.
- Backfill should run in the migration so fresh and migrated databases
  converge immediately.

## Compatibility Rules

The rollout should tolerate mixed producers for one migration window.

### Send path

For `send_message` and REST `POST /messages`:

1. If `intent` is provided, validate it against the closed enum.
2. If `intent` is omitted and `metadata.type` is present, derive `intent`
   using the canonical mapping above.
3. If neither is provided, store `intent = general`.
4. If both are provided and the canonical mapping for `metadata.type`
   disagrees with `intent`, reject the request as a 400 / invalid operation
   instead of silently storing conflicting semantics.

### Read path

- Always return `intent`.
- Keep returning `metadata` unchanged.
- Continue reading legacy rows whose meaning only exists in `metadata.type`.

## Relationship To `metadata.type`

`metadata.type` should be treated as legacy compatibility data, not the
long-term source of truth.

Recommended direction:

- `intent` becomes the canonical workflow field.
- Existing consumers may continue to read `metadata.type` during transition.
- New producers should stop inventing new `metadata.type` values once `intent`
  exists.
- Packet-specific metadata should move to domain-specific keys:
  - `packet_kind` for review request / rereview / review findings packet flavor
  - `handoff_kind` for planning vs other handoffs
  - existing keys like `review_round_id`, `branch`, `recipient`, `tests_run`
    remain as-is

This keeps `metadata` useful without making `metadata.type` a second competing
intent field forever.

## Dispatch, Routing, And Prompt Integration

This is where the workflow payoff lands.

### Detection

- `DispatchDetectionService` should populate `DispatchEvent.MessageIntent` from
  `Message.Intent`.
- It should still extract `recipient` and other routing hints from metadata.
- `recipient` should remain a concrete target agent identity.
- A separate metadata key such as `target_role` can be used for role-targeted
  handoffs that should resolve through the project's routing config.
- If both `recipient` and `target_role` are present, `recipient` should take
  precedence so direct addressing stays explicit.
- It should not hardcode target-agent selection from intent in the service
  itself; routing policy still belongs in `RoutingService` / routing docs.

### Routing config

Add a first-class `message_intent` predicate to `RoutingTrigger`.

Recommended compatibility plan:

- Support new `message_intent` in routing documents.
- Keep `message_type` as a deprecated alias for one compatibility window.
- Prefer `message_intent` in generated docs/examples going forward.
- Add exact-match `packet_kind` and `handoff_kind` predicates for the cases
  where projects need subtype routing within a shared canonical intent.
- Treat `message_type` as an intent-compatibility alias, not the long-term way
  to distinguish packet or handoff subtypes.

That lets a project express rules like:

- `review_request` -> `reviewer`
- `target_role = reviewer` -> `{target_role}`
- `review_feedback` -> `{recipient}` or `implementer`
- `task_blocked` -> coordinator / human overseer
- `message_intent = review_request` plus `packet_kind = rereview_request` ->
  specialized rereview handling without breaking canonical intent routing
- `message_intent = handoff` plus `handoff_kind = planning_summary` ->
  planner/context handoff handling distinct from other handoffs

without relying on raw packet subtype strings.

### Prompt generation

`PromptGenerationService` should switch on `MessageIntent`, not raw
`metadata.type`.

Suggested mapping:

- `handoff` -> generic handoff / planning-context prompt
- `review_feedback` -> findings / fix-and-rereview prompt
- `review_approval` -> merge handoff prompt
- `review_request` -> review prompt when triggered by message rather than task
  status
- everything else -> generic message prompt

This directly improves the automatic coder/reviewer handoff path because the
specialized prompts stop depending on exact legacy subtype strings like
`merge_request` or `planning_summary`.

### Legacy routing behavior

Dispatch routing is now opt-in compatibility mode. When a project explicitly sets `defaults.legacy_dispatch_enabled: true`, keep the legacy trigger set conservative:

- task status transition to `review` can dispatch the configured reviewer
- explicit `recipient` can dispatch that agent
- explicit `target_role` can dispatch to the agent currently configured for that
  role
- if both are present, `recipient` wins over `target_role`
- do not auto-dispatch every `review_request` / `question` / `task_blocked`
  message solely because the intent exists

Intent should make routing possible and explicit, not magical. Projects that still need legacy dispatch bridges can opt into stronger intent-based rules in `dispatch-routing`; normal conductor work should use task/thread messages and agent-stream attention instead.

## Producer Changes

After the schema/API lands, Den-owned producers should write intent explicitly:

- review request packet -> `intent = review_request`
- rereview packet -> `intent = review_request`
- review findings packet -> `intent = review_feedback`
- verdict handoff with changes requested / follow-up needed ->
  `intent = review_feedback`
- verdict handoff with looks good -> `intent = review_approval`
- planning summary / context handoff -> `intent = handoff`

This is the key change that removes brittle string-sniffing from the
coder/reviewer workflow.

## UI And CLI Implications

Minimum useful surface after backend support:

- CLI `messages` command can show intent badges and accept `--intent`.
- CLI `send` can accept `--intent`.
- Dashboard/web message feed can display an intent chip next to sender/time.
- Message detail can show intent and selected metadata fields.

Follow-on UX made possible by intent:

- filter recent messages to only `question`, `handoff`, `review_feedback`, or
  `task_blocked`
- show “open questions / handoffs” panels without parsing content
- suppress `note` / `general` noise in workflow-focused views

## Suggested Implementation Split

### Task A: schema + core API

- add `MessageIntent`
- add `messages.intent` migration + backfill + index
- update `Message`, repository read/write logic, REST/MCP send/get surfaces,
  CLI client types, TS types
- add compatibility derivation from `metadata.type`
- add tests for migration, repository filtering, and conflicting `intent` vs
  legacy `metadata.type`

### Task B: workflow integration

- update `DispatchEvent`, `DispatchDetectionService`, `RoutingTrigger`,
  `RoutingService`, and `PromptGenerationService` to use intent
- keep `message_type` trigger alias temporarily
- update `ReviewWorkflowService` and any other built-in producers to write
  explicit intent
- move producer-specific subtype data off `metadata.type` where practical
- add tests covering automatic review request / feedback / approval handoffs

### Task C: CLI and UI follow-through

- add intent filtering and display in CLI/dashboard/web
- expose intent in message detail surfaces
- optionally add focused views for `question`, `handoff`, `review_feedback`,
  and `task_blocked`

## Open Questions

1. Should `review_request` messages auto-route to the configured reviewer when
   no explicit recipient is present, or should that remain opt-in via
   `dispatch-routing`?
   Recommendation: keep it opt-in so Den does not surprise projects with new
   dispatch behavior.

2. Should new system-generated messages continue writing legacy `metadata.type`
   during the compatibility window, or switch immediately to `intent` plus
   `packet_kind` / `handoff_kind`?
   Recommendation: task A may keep writing both for one pass; task B should
   move internal consumers to `intent`; task C or a short cleanup task can
   remove remaining legacy writes once everything is switched.

3. Do we want a dedicated `handoff_kind` enum in metadata now, or only when a
   second non-planning handoff producer appears?
   Recommendation: add the field opportunistically where a producer already
   knows the subtype, but do not block the intent migration on a second enum
   design.

4. Is `general` the right fallback label, or would `misc` communicate
   “non-actionable” better in the UI?
   Recommendation: `general` reads better in APIs and UI badges.
