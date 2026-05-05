# Hermes Memory Document Conventions

Conventions for storing Hermes/assistant long-term memories as Den documents.

## Representation

Memory documents use the standard Den document model with these conventions:

- **`doc_type = "memory"`** — Explicit type for all long-term memory documents.
- **`summary`** — Optional short summary (recommended) for lightweight indexing and listing.
- **`tags`** — Flexible, model-chosen descriptive tags. No fixed tag taxonomy.
- **`slug`** — Stable identifier within the space. Use descriptive slugs like `user-preference-shell` or `project-context-rust`.
- **`title`** — Human-readable title.
- **`content`** — Full memory content in markdown.

## Summary Field

The `summary` field exists so that Hermes agents can include an **index of memories** in always-injected context without loading every full document.

- Keep summaries concise (1–3 sentences).
- Summaries are preserved on update and returned in `list_documents` / `search_documents` results.
- If a memory grows large, the summary lets agents decide whether to retrieve the full doc via `get_document`.

## Tag Guidelines

- Tags are flexible and may be chosen by the storing agent/model.
- Common useful tags: `hermes`, `context`, `user-preference`, `project-knowledge`, `decision`, `anti-pattern`.
- Topic-clipping tags are controlled separately by the curation pipeline; do not mix controlled clipping tags with freeform memory tags.

## Spaces

Memory documents live in Den projects/spaces. Typical patterns:

- Assistant space (`kind = "assistant"`) — Personal assistant memories.
- Knowledge-base space (`kind = "knowledge_base"`) — Shared reference memories.
- Project space — Memories scoped to a specific project.

## Indexing and Retrieval

Agents should:

1. **List/index** memories via `list_documents` or `search_documents` with `doc_type = "memory"`.
2. **Read summaries** from the listing to decide relevance.
3. **Fetch full docs** on demand via `get_document` or `search_documents` when a summary indicates relevance.

This avoids over-retrieval of large memory bodies into context.

## Update Behavior

- Upserting a memory with the same `project_id + slug` overwrites the existing document.
- Update the `summary` when the content changes significantly.
- `updated_at` is automatically refreshed on every upsert.

## When to Use Topic Clipping Instead

Topic clipping (via the topic-clip queue and curation pipeline) is for:

- Automatically extracted conversation fragments.
- Content awaiting curator review before promotion to long-term memory.

Use ordinary `doc_type = "memory"` documents for:

- Curated, approved long-term memories.
- Agent-authored knowledge and preferences.
- Stable reference material.

## API / Tool Notes

- `store_document` accepts `doc_type = "memory"` and an optional `summary` parameter.
- `list_documents` returns `summary` in each `DocumentSummary`.
- `search_documents` returns `summary` in each `DocumentSearchResult`.
- All existing document tools remain backward-compatible: `summary` is optional and defaults to omitted.
