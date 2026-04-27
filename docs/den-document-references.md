# Den Document References

Date: 2026-04-27

Den Markdown documents can refer to other Den documents with a stable inline reference:

```text
[doc: project_id/slug]
```

Examples:

```text
[doc: den-mcp/agent-stream-design]
[doc: _global/agent-task-management-policy]
```

## Web behavior

Den web document detail views detect this syntax in rendered document content and show each detected reference as a clickable inline link.

Clicking a link opens the referenced document by project id and slug, including `_global` documents. If the target document has been deleted or the slug is wrong, the document detail view opens a non-destructive not-found state instead of failing the whole page.

## Scope

The supported syntax is intentionally explicit and project-qualified. Bare slugs are not auto-linked because the same slug can exist in multiple projects.
