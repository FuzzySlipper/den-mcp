# Shared Blackboard Memory

Date: 2026-04-27

## Decision

Shared blackboard memory uses a dedicated `blackboard_entries` storage path rather than `_global` documents.

Rationale:

- blackboard entries are explicitly not scoped to a project;
- temporary handoff/scratch entries need idle-expiry semantics;
- reads and lists may update `last_accessed_at` for TTL refreshes, which would be surprising for normal durable documents;
- deletion of expired scratch entries should not interact with document guidance/history semantics.

`_global` documents remain the durable cross-project documentation/guidance space. Blackboard entries are for reusable cross-project memory and temporary scratch/handoff notes.

## Model

Each entry has:

- `slug` — unique cross-project identifier;
- `title` — human-readable title;
- `content` — Markdown body;
- `tags` — optional string tags for discovery;
- `created_at`, `updated_at`, `last_accessed_at` timestamps;
- `idle_ttl_seconds` — optional positive TTL in seconds.

When `idle_ttl_seconds` is null, the entry is durable until deleted. When set, the entry expires if it has not been accessed for that many seconds.

## Expiry behavior

Expiry is lazy and predictable:

- `get`, `list`, `upsert`, and explicit cleanup first remove entries whose `last_accessed_at + idle_ttl_seconds` is in the past.
- `get` refreshes `last_accessed_at` for expiring entries it returns.
- `list` refreshes `last_accessed_at` for returned expiring entries.
- `upsert` refreshes `last_accessed_at` because writes count as access.

This MVP supports idle TTL only. Absolute expiration can be added later if a concrete workflow needs it.

## REST API

```http
POST /api/blackboard
GET /api/blackboard?tags=handoff,scratch
GET /api/blackboard/{slug}
DELETE /api/blackboard/{slug}
POST /api/blackboard/cleanup
```

Create/update body:

```json
{
  "slug": "handoff-note",
  "title": "Handoff Note",
  "content": "Markdown body",
  "tags": ["handoff", "scratch"],
  "idle_ttl_seconds": 86400
}
```

## MCP tools

Agents can use:

- `store_blackboard_entry`
- `get_blackboard_entry`
- `list_blackboard_entries`
- `delete_blackboard_entry`
- `cleanup_blackboard_entries`

`list_blackboard_entries` supports comma-separated tag filtering.

## CLI

The CLI exposes the same MVP surface:

```bash
den blackboard list --tags handoff,scratch
den blackboard get handoff-note
den blackboard set handoff-note --title "Handoff Note" --content "Markdown body" --tags handoff,scratch --idle-ttl-seconds 86400
den blackboard delete handoff-note
den blackboard cleanup
```

Full-text search is intentionally deferred; tags and slugs are the MVP discovery mechanism.
