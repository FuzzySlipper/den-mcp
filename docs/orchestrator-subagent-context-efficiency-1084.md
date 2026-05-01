# Orchestrator/Sub-agent Context Efficiency Study (#1084)

## Scope

Task #1084 asked for a representative workflow study of what context is actually consumed by the Den orchestrator and sub-agents, what is merely terminal/tool feedback, and which details should be summarized, silenced, moved to artifacts, or promoted to durable summaries.

Representative workflow: task #1082, `Streamline review finding triage into follow-up tasks`.

Why #1082 was selected:

- It included a coder sub-agent run, reviewer sub-agent run, drift check, validation packets, review findings, approval, follow-up split, merge, and post-merge validation.
- It had both successful focused validations and a full-suite validation failure that was later explained as worktree frontend-bundle environment noise.
- It exposed a very noisy agent-stream trace when the orchestrator queried recent ops after compaction.
- It exercised new final-head reporting and showed a real metadata mismatch: the parent result knew the final head, but `status.json` retained only the requested/starting `head_commit`.

## Raw artifacts recorded

| Item | Location / source | Notes |
|---|---|---|
| Coder run | `/home/patch/.pi/agent/den-subagent-runs/4605487592aa8db7` | GLM coder continuation for #1082. |
| Coder Pi session | `.../sessions/2026-05-01T02-08-18-754Z_019de14b-4141-71ca-9519-9daa29681f79.jsonl` | Persisted transcript used for model-context analysis. |
| Reviewer run | `/home/patch/.pi/agent/den-subagent-runs/2f1f3e6fa2bc1150` | DeepSeek reviewer for review round #482. |
| Reviewer Pi session | `.../sessions/2026-05-01T02-19-00-903Z_019de155-0da6-71b8-8f5b-8ff8e3a713de.jsonl` | Persisted transcript used for model-context analysis. |
| Durable task thread | Den task #1082 messages #3547-#3558 | Validation, drift, implementation, review, merge packets. |
| Agent stream trace | `den_list_agent_stream(project_id="den-mcp", recipient_agent="pi", limit=50)` after compaction | Demonstrated orchestrator-facing debug-event noise. |

## Measured artifact sizes

| Run | `events.jsonl` | `stdout.jsonl` | Pi session JSONL | Session messages | Reported usage summary |
|---|---:|---:|---:|---:|---|
| Coder `4605487592aa8db7` | 8.9 MiB / 11,037 lines | 186 MiB / 11,114 lines | 203 KiB / 67 records | 1 user, 27 assistant, 36 tool results | 36,835 input, 10,891 output, 1,135,552 cache-read tokens |
| Reviewer `2f1f3e6fa2bc1150` | 5.8 MiB / 9,747 lines | 55 MiB / 9,833 lines | 217 KiB / 59 records | 1 user, 20 assistant, 35 tool results | 53,631 input, 10,550 output, 902,784 cache-read tokens |

Important distinction: `stdout.jsonl` and `events.jsonl` are operational artifacts, not the compact persisted Pi model transcript. The session JSONL is much smaller and is the best available approximation of what entered the sub-agent model context.

## What actually entered sub-agent model context

Using the persisted Pi session JSONL:

| Run | User prompt chars | Assistant text/tool-call chars | Tool-result chars | Largest model-visible tool results |
|---|---:|---:|---:|---|
| Coder | 15,252 | 18,660 | 108,116 | File reads of repository source/tests: 23,240, 14,707, 11,680, 10,960 chars; implementation-packet Den response: 5,651 chars |
| Reviewer | 13,985 | 22,377 | 135,253 | Diffs/file reads: 30,738, 15,162, 14,707, 11,680 chars; `den_set_review_verdict`/review packet result: 8,314 chars |

Findings:

1. The sub-agent model context is dominated by legitimate code/diff inspection, not by lifecycle artifacts.
2. Durable Den mutation tool results still add non-trivial model-visible noise inside sub-agents. The reviewer received full review-round/finding payloads after mutation calls even though the durable record already existed in Den.
3. The parent/orchestrator sub-agent result is already fairly concise. For example, `den_run_reviewer` returned a bounded final summary and artifact paths, not the 55 MiB stdout trace.
4. The model-visible context inside sub-agents is not the same as terminal-visible `stdout.jsonl`: the coder stdout artifact was 186 MiB, while the persisted session was 203 KiB.

## What entered the orchestrator context

Observed after compaction while resuming #1076 work:

- `den_run_reviewer` returned a bounded parent result: enough to know verdict, run ID, artifact path, and a truncated summary.
- `den_validate` returned a full validation packet with stdout/stderr previews. This is useful for immediate triage, but full previews should be artifact-first and summary-by-default for routine pass/fail states.
- `den_get_task(1082)` returned the full task plus recent messages, review workflow, open/resolved findings, review rounds, and long packet bodies. This is useful when explicitly reading a task thread, but expensive as a default context refresher.
- `den_list_agent_stream(... limit=50)` returned many debug-level `subagent_work_*` entries with large metadata payloads, tool arguments, and tool-result previews. This was the highest-value noise candidate because the orchestrator normally needs summary/attention events, not every debug event.

Limitations: the parent Pi context window is not directly introspected here. The report separates model-context evidence where Pi session artifacts exist (sub-agents) from observed tool responses that were sent into this orchestrator turn.

## Keep / summarize / silence / artifact-only recommendations

| Category | Recommendation | Rationale |
|---|---|---|
| Coder/reviewer final parent result | Keep concise summary + artifact paths | This is the right default: actionable and bounded. |
| `status.json` / artifacts | Keep full details artifact-only | Required for auditability and debugging. |
| Sub-agent `stdout.jsonl` / raw `events.jsonl` | Artifact-only by default | They can be tens or hundreds of MiB and are operational traces, not planning context. |
| Agent-stream lifecycle summaries (`subagent_completed`, `validation_completed`, review approved) | Keep in default orchestrator drain | Useful state transitions. |
| Agent-stream debug events (`subagent_work_message_*`, `subagent_work_tool_*`, `subagent_work_turn_*`) | Hide by default; expose with explicit debug filter | They flooded `den_list_agent_stream` and repeated tool previews already preserved in artifacts. |
| Den mutation responses (`create_review_finding`, `set_review_verdict`, `post_review_findings`, `create_task`, `set_review_finding_status`) | Summary-by-default with IDs/status; full object only on verbose/debug request | Durable data is already in Den; full payloads repeatedly enter model context. |
| Validation packets | Summary-by-default in parent tool result; preserve full stdout/stderr previews in Den packet/artifact | Routine pass/fail should not inject long build logs into orchestrator context unless requested. |
| Task-thread reads | Provide a compact thread-summary/read mode | Full recent messages are sometimes necessary, but startup/drain loops usually need the latest packet headers and unresolved actions. |
| Final-head metadata | Promote `final_head_commit`/`final_branch` into `status.json`, not only parent result/agent-stream metadata | The artifact source of truth should include final head for post-hoc studies and automated merge checks. |
| Sub-agent context metrics | Promote compact per-run metrics into `status.json` and parent result | Makes future context studies deterministic without parsing session JSONL. |

## Important details that should be promoted to orchestrator summaries

1. **Final branch/head and cleanliness**: parent result for #1082 included `final_head_commit=94b274a...`, but `status.json` still showed the originally supplied `head_commit=1abb3ff...` and did not persist top-level `final_head_commit`. This is exactly the class of detail the orchestrator needs at merge/review time.
2. **Model-context metrics**: input/output/cache token counts exist in `usage_summary`, but session char counts and top model-visible tool-result categories require custom parsing. A compact `context_metrics` block would make future tuning easier.
3. **Validation failure classification**: the #1082 full-suite validation failure was environment-related. The validation packet preserved failure previews but did not separately expose a short `failure_classification` field.
4. **Actionability of agent-stream entries**: lifecycle entries should distinguish `attention_required` from debug telemetry so orchestrator startup can ask for actionable work without pulling raw traces.

## Concrete follow-ups

The following implementation tasks were created from this study:

- #1106 — Make agent-stream/default inbox reads summary-first and hide debug sub-agent events by default.
- #1107 — Make review/finding workflow mutation tool responses concise by default.
- #1108 — Add compact validation result mode with artifact-backed log previews.
- #1109 — Add compact task-thread/workflow summary read for orchestrator startup.
- #1110 — Persist final-head and context-metrics fields in sub-agent status artifacts.

## Acceptance criteria checklist

- Representative workflow selected: #1082.
- Raw context/artifact inputs recorded: coder/reviewer run directories, session files, Den task/thread packets, agent-stream trace.
- Terminal/tool feedback separated from model-context evidence: stdout/events artifacts versus Pi session JSONL.
- Sub-agent details needing promotion identified: final head, context metrics, validation classification, actionability flags.
- Orchestrator-facing noise candidates identified: debug agent-stream events, verbose mutation responses, long validation previews, full task-thread reads.
- Recommendations provided by keep/summarize/silence/artifact-only/promote category.
- Follow-up tasks created while preserving auditability: full Den records and artifacts remain durable; only default model-facing summaries should shrink.
