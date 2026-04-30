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

## Console command output

Structured console commands run through the typed sidecar bridge and return structured `ConsoleCommandLine` output in the final `consoleRunCommand` response. The sidecar command handler also reports progress frames, but the current renderer/preload API is intentionally batch-only: `BridgeClientTransport.send()` exposes the final response and does not yet provide a per-request progress subscription callback. A real-time ConsoleDock renderer should first add that typed progress subscription to the Electron/preload bridge, then append in-flight command lines before reconciling with the final response.

## Terminal attach contract notes

The `den_desktop.terminal.attach` request accepts typed `viewport` and `replay` fields. The direct PTY backend uses both for replay from its output buffer. The tmux backend now applies `viewport` to the tmux window before `capture-pane` replay and limits the capture start to the requested row count; tmux replay is still snapshot-based rather than a live per-cursor history.

`TerminalExternalAttachInfo.command` is display/copy-only operator text. The renderer must never auto-execute that string or pass it to a generic shell runner. If the UI later adds an attach button, it must call a typed app-core command that owns validation/audit instead of executing the displayed command text.

## Boundaries

- Den remains the durable source of truth for tasks, messages, reviews, runs, and published snapshots.
- This app owns local observation/control state for paths and sessions visible on the operator machine.
- Missing paths, non-git folders, detached heads, git errors, and Den disconnects are shown as status/warnings rather than fatal UI failures.
- Terminal/session support is currently prototype observer mode: it reads local Pi run artifacts and publishes structured snapshots, but does not stream raw terminal output or send controls yet.
