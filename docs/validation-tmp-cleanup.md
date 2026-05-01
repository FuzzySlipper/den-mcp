# Validation Temp Cleanup Helper

## Purpose

Before long or full validation runs (`dotnet test`, `npm test`, etc.), temp files
left by previous agent sessions or test fixtures can fill tmpfs and cause
"no space left on device" failures. This helper provides a safe, Den-aware way
to clean project-owned `/tmp` artifacts.

## Usage

### Pi Slash Command

```
/den-tmp-cleanup                                  # Preview (dry-run) only
/den-tmp-cleanup --destructive                    # Delete after active-agent check
/den-tmp-cleanup --force                          # Delete and bypass active-agent check
/den-tmp-cleanup --destructive --project my-project   # Different project
```

### Pi Tool (for sub-agent / orchestrator use)

The `den_tmp_cleanup` tool is available through the Den Pi extension:

```json
{
  "tool": "den_tmp_cleanup",
  "args": {
    "project_id": "den-mcp",
    "destructive": false,
    "include_legacy_patterns": true
  }
}
```

For destructive cleanup with active-agent bypass:

```json
{
  "tool": "den_tmp_cleanup",
  "args": {
    "project_id": "den-mcp",
    "destructive": true,
    "force": true
  }
}
```

## What Gets Cleaned

### Primary: `/tmp/<project-id>/`

Default cleanup root. For project `den-mcp`, this is `/tmp/den-mcp/`.
Files and empty directories under this root are scanned recursively by default
so any project-owned temp layout can be cleaned while remaining scoped to the
project temp directory. Pass `recursive=false` to the tool/library only when a
more conservative one-level preview is needed.

### Legacy: `/tmp/den-mcp-test-*`

Known safe legacy pattern from test DB files that were created before the
`/tmp/<project-id>/` convention was adopted. Only the den-mcp project
defines legacy patterns. Other projects can be extended via the
`LEGACY_PATTERNS` registry in `pi-dev/lib/den-tmp-cleanup.ts`.

## Safety Checks

### Active-Agent Guard

Before destructive deletion, the helper checks Den for other active agents on
the same project. If other agents are active, cleanup is **blocked** unless
`force=true` (`--force`) is set.

Dry-run previews never check active agents — they only scan the filesystem.

### Dry-Run By Default

The helper always previews by default (dry-run), showing:
- Files found and total size
- Legacy pattern matches
- Clear confirmation prompt before actual deletion

Only `destructive=true`, `--destructive`, or `--force` triggers actual file deletion.

## Library API

The core logic lives in `pi-dev/lib/den-tmp-cleanup.ts` and exports:

| Export | Description |
|---|---|
| `planTmpCleanup(options)` | Scan `/tmp/<project>/` and legacy patterns, return a plan |
| `executeTmpCleanup(plan, options)` | Execute (or preview) the plan with safety checks |
| `formatCleanupPlan(plan)` | Format plan as human-readable lines |
| `formatCleanupResult(result)` | Format result as human-readable lines |
| `checkActiveAgents(agent, list)` | Pure safety check: are other agents active? |
| `scanDirectory(path)` | Recursive directory scan by default; pass `recursive: false` for direct children only |
| `scanByPrefix(path, prefix)` | Filename-prefix scan for legacy patterns |
| `buildTmpCleanupToolResult(result)` | Build Pi tool response from result |
| `buildTmpCleanupToolParameters()` | Build Pi tool parameter schema |

Tests: `tests/PiExtension.Tests/den-tmp-cleanup.test.mjs`
