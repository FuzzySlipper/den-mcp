# Pi Conductor Context Status

Date: 2026-04-27
Status: implemented for task `#852`; compaction trigger added for task `#967`;
post-compaction resume added for task `#974`

Long-lived Pi conductor sessions need a cheap way to decide whether to compact
between Den tasks. The Den Pi extension exposes that signal without pretending it
is exact tokenizer accounting.

## Surfaces

- Model-callable tool: `den_context_status`
- Model-callable tool: `den_compact_context`
- Slash command: `/den-context-status`
- Slash command alias: `/den-compaction-status`
- Slash command: `/den-compact-context [custom instructions]`
- Active-agent binding metadata: compact `context_status` summary on Den Pi
  check-in state updates

The status tool returns schema `den_context_status` with:

- provider/model id
- configured context window when the active Pi model exposes one
- estimated used tokens and percent when available
- remaining-token estimate when both used tokens and context window are known
- source and confidence labels
- Pi compaction settings when available (`reserveTokens`, `keepRecentTokens`,
  derived auto-compaction threshold)
- recommendation: `ok`, `watch`, or `compact_after_current_task`

## Estimation method and limitations

The preferred source is Pi extension context `ctx.getContextUsage()`.
Current Pi computes this from the active session context, combining provider
usage from the latest valid assistant response with heuristic token estimates for
trailing messages. After compaction, Pi may report `tokens: null` / `percent:
null` until another model response refreshes usage. This is treated as unknown
and the recommendation becomes conservative `watch`.

When `ctx.getContextUsage()` is unavailable, the extension falls back to the last
successful assistant message on the current session branch and sums provider
reported usage (`totalTokens`, or input/output/cache-read/cache-write). This is a
low-confidence snapshot because it can be stale and does not include messages or
tool results added after that assistant response.

The status is therefore a conductor decision aid, not exact tokenizer accounting.
It should not be used to make hard safety guarantees about provider limits.

## Recommendation policy

The status helper derives the Pi auto-compaction threshold when the active model
context window and compaction reserve are known:

```text
auto threshold = context_window - reserve_tokens
```

It recommends:

- `ok`: below the watch threshold
- `watch`: elevated usage or unknown usage; avoid starting a large new task
  without rechecking
- `compact_after_current_task`: near the derived auto-compaction threshold or at
  the default high-water mark

The warning thresholds intentionally fire before Pi auto-compaction so the
conductor can finish the current Den handoff, review, or merge step and compact
at a natural boundary.

## Compaction trigger

The compact tool returns schema `den_context_compaction_request` with:

- `requested`: whether Pi compaction was requested
- `status`: `requested`, `blocked`, `unavailable`, or `failed`
- `reason`: human-readable result
- `custom_instructions`: instructions passed to Pi compaction
- `safe_point_notes`: caller-provided safe-boundary rationale
- `resume_configured`: whether a follow-up prompt will be sent after compaction
- `resume_note`: explanation of what happens after compaction
- `guardrails`: durable-state and task-boundary reminders

The model-callable tool requires `durable_context_posted: true`. This is a
soft guardrail that forces the conductor to confirm Den already has the current
handoff/status/review/merge state before starting a lossy compaction. The slash
command is an explicit operator/conductor action and treats command invocation
itself as that durable-state assertion; use the model-callable tool when a model
needs the guardrail enforced in parameters. If the runtime does not expose
`ctx.compact()`, the tool reports `unavailable` and the operator can fall back to
manual `/compact` or an RPC entrypoint.

### Fire-and-forget semantics

Pi extension `ctx.compact()` is fire-and-forget: it does **not** return a
Promise and compaction runs asynchronously after the current agent turn ends.
This means:

1. The `den_compact_context` tool/command returns immediately with
   `status: "requested"`.
2. Pi continues the current agent turn (processing tool results, etc.).
3. After the turn ends and the agent goes idle, Pi runs compaction (calls the
   LLM to generate a summary).
4. Compaction completes, the session context is rebuilt from the summary + kept
   messages.
5. If `resume_after_compaction` is enabled (default), the extension sends a
   follow-up user message to trigger a new conductor turn with the compacted
   context.

If `resume_after_compaction` is `false` or the `sendResumeMessage` callback is
not available, the conductor session is **suspended** after compaction until the
operator manually sends a prompt.

### Post-compaction resume

The `resume_after_compaction` parameter (default: `true`) controls whether the
extension automatically sends a follow-up prompt after compaction completes.
When enabled, the extension calls `pi.sendUserMessage()` from the compaction
`onComplete` callback with a message like:

> Conductor context compaction completed. Re-read your current Den task/thread
> state and continue with the next step.

This triggers a new agent turn with the compacted context, allowing the
conductor to continue without manual intervention.

The resume message intentionally asks the conductor to re-read Den state
because compaction discards fine-grained context. The conductor should re-check
task/thread state before continuing substantive work.

## Recommended conductor behavior

- Check `den_context_status` before starting a large implementation/review task
  in a long-running conductor session.
- If the result is `watch`, prefer calling `den_compact_context` between tasks or
  after posting a durable Den planning/review/merge note.
- If the result is `compact_after_current_task`, finish the current small step,
  make sure Den has the durable state, then call `den_compact_context` with
  `durable_context_posted: true` before starting another substantial task.
- Avoid compacting in the middle of review/merge handoffs unless necessary; post
  the branch/head/tests/review state to Den first.
- Do not stop solely to ask the user to run `/compact` when `den_compact_context`
  is available and the safe-boundary guardrail is satisfied. Ask only if the tool
  reports `unavailable`/`failed` or the safe point is ambiguous.
- Do not treat child Pi sub-agent artifacts as parent context. Full child run
  transcripts remain in artifacts and Den run detail; the parent tool result is
  intentionally bounded by task `#851`.

## Pi compaction behavior by mode

| Mode | Mechanism | Returns result | Auto-resume |
|------|-----------|---------------|-------------|
| TUI `/compact` | Slash command | Not applicable (user sees events) | Yes (user continues typing) |
| RPC `compact` | JSON command | Yes (synchronous response with summary) | Caller controls |
| SDK `session.compact()` | Promise | Yes (await CompactionResult) | Caller controls |
| Extension `ctx.compact()` | Fire-and-forget | No (callbacks only) | No (session suspended) |
| `den_compact_context` tool | Extension tool | Yes (immediate `requested`) | Optional (default: yes) |

The `den_compact_context` tool and `/den-compact-context` command use
`ctx.compact()` internally but add a deterministic resume step: when
`resume_after_compaction` is enabled, the extension calls
`pi.sendUserMessage()` in the `onComplete` callback to trigger a new conductor
turn automatically.
