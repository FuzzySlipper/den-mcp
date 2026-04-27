# DenMcp.Desktop

Local Tauri desktop operator app for Den.

This app is intentionally a sibling to `src/DenMcp.Server/ClientApp`: it bundles and runs its own local UI instead of serving or iframing Den web from the Den server.

## Dev commands

```bash
cd src/DenMcp.Desktop
npm install
npm run ui:build
npm run test:helpers
npm run tauri:dev
```

Rust-only validation:

```bash
cd src/DenMcp.Desktop/src-tauri
cargo check
```

## First-slice behavior

- Loads local settings from the Tauri app config directory.
- Maintains a stable `sourceInstanceId` for this desktop app instance.
- Checks Den health and syncs projects plus agent workspaces from a configured Den server URL.
- Scans locally visible project roots/worktrees with safe Rust-side `git` process calls.
- Publishes desktop git snapshots to `/api/projects/{projectId}/desktop/git-snapshots`.
- Keeps local in-memory snapshots and shows queued/stale/offline-style status when Den is disconnected.
- Renders a local React UI for connection health, observer status, diagnostics, task/workspace snapshot cards, changed-file grouping, bounded diff lookup status, and prototype Pi session snapshots.

## Boundaries

- Den remains the durable source of truth for tasks, messages, reviews, runs, and published snapshots.
- This app owns local observation/control state for paths and sessions visible on the operator machine.
- Missing paths, non-git folders, detached heads, git errors, and Den disconnects are shown as status/warnings rather than fatal UI failures.
- Terminal/session support is currently prototype observer mode: it reads local Pi run artifacts and publishes structured snapshots, but does not stream raw terminal output or send controls yet.
