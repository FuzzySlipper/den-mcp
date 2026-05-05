# Topic Registry

The **topic registry** provides controlled governance for consolidation topics used in topic-clipping workflows (e.g. Hermes memory pipeline). It prevents uncontrolled tag drift by maintaining a canonical list of valid topic tags with metadata, aliases, and lifecycle status.

## Scope

- **Controlled**: consolidation-topic tags for queue append / clipping workflows.
- **Flexible**: ordinary document tags and long-term memory document tags remain unconstrained.

## Model

| Field        | Description                                            |
|--------------|--------------------------------------------------------|
| `slug`       | Canonical unique identifier for the topic.             |
| `display_name` | Human-readable name.                                 |
| `description` | Optional longer explanation.                          |
| `aliases`    | JSON array of alternate tags that resolve to this slug.|
| `status`     | `active`, `inactive`, or `deprecated`.                 |
| `owning_space` | Optional project/space ID for scoping.              |

## APIs

### REST

- `GET /api/topics` — List topics (active only by default).
  - Query: `?owning_space=<id>&include_inactive=true`
- `GET /api/topics/{id}` — Get topic by id.
- `GET /api/topics/by-slug/{slug}` — Get topic by slug.
- `POST /api/topics` — Create a topic.
- `PUT /api/topics/{id}` — Update a topic.
- `DELETE /api/topics/{id}` — Delete a topic.
- `POST /api/topics/validate` — Validate tags against the registry.
  - Body: `{ "tags": ["perf", "unknown"], "allow_inactive": false }`

### MCP Tools

- `create_topic` — Create a new consolidation topic.
- `list_topics` — List topics with optional filters.
- `get_topic` — Get a topic by slug.
- `update_topic` — Update a topic by id.
- `delete_topic` — Delete a topic by id.
- `validate_topic_tags` — Validate tags, resolving aliases to canonical slugs.

## Validation Behavior

- By default, only `active` topics are considered valid.
- Aliases resolve to their canonical slug.
- Inactive/deprecated topics are rejected unless `allow_inactive` is set.
- Unknown tags are rejected with a clear reason.

## Prompt Injection

Hermes or other agents can call `list_topics` (or `GET /api/topics`) to retrieve the current active topic set for injection into prompts, ensuring agents use only approved tags.

## Future Queue Integration

Queue append operations (task #1163/#1164) should validate incoming topic tags via the registry by default. Unknown/inactive tags can be rejected or routed to an admin override path.
