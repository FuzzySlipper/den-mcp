# Pi Conductor Context Status

Date: 2026-04-27
Status: implemented for task `#852`

Long-lived Pi conductor sessions need a cheap way to decide whether to compact
between Den tasks. The Den Pi extension exposes that signal without pretending it
is exact tokenizer accounting.

## Surfaces

- Model-callable tool: `den_context_status`
- Slash command: `/den-context-status`
- Slash command alias: `/den-compaction-status`
- Active-agent binding metadata: compact `context_status` summary on Den Pi
  check-in state updates

The tool returns schema `den_context_status` with:

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

## Recommended conductor behavior

- Check `den_context_status` before starting a large implementation/review task
  in a long-running conductor session.
- If the result is `watch`, prefer compacting between tasks or after posting a
  durable Den planning/review/merge note.
- If the result is `compact_after_current_task`, finish the current small step,
  make sure Den has the durable state, then compact before starting another
  substantial task.
- Avoid compacting in the middle of review/merge handoffs unless necessary; post
  the branch/head/tests/review state to Den first.
- Do not treat child Pi sub-agent artifacts as parent context. Full child run
  transcripts remain in artifacts and Den run detail; the parent tool result is
  intentionally bounded by task `#851`.
