# DenMcp.Desktop

Local desktop operator app for Den, built with an Electron shell and a reusable .NET sidecar behind a typed bridge.

This app is intentionally a sibling to `src/DenMcp.Server/ClientApp`: it bundles and runs its own local UI instead of serving or iframing Den web from the Den server.

## Dev commands

```bash
cd src/DenMcp.Desktop
npm install
npm run ui:build
npm run test:helpers
```

### Electron dev shell (primary)

The Electron dev shell launches the .NET sidecar, connects via typed WebSocket bridge, and loads the React UI in a BrowserWindow with a secure preload boundary.

```bash
# Build UI and Electron main/preload bundles, then launch Electron from built dist/
npm run electron:dev
```

This default command intentionally loads the built UI directly from `dist/index.html`; it does not probe the Vite dev server first. This avoids a noisy failed connection attempt when you are testing the sidecar/preload/Electron shell without hot reload.

This will:
1. Build the React UI to `dist/` via Vite.
2. Bundle the Electron main process (`src/electron/main.ts`) and preload (`src/electron/preload.ts`) to `electron-dist/` via esbuild.
3. Launch Electron which starts the .NET sidecar, waits for the ready sentinel, connects the typed bridge, and loads the built UI.

For hot React reload, run Vite separately and launch Electron in hot mode:

```bash
# Terminal 1: serve the renderer
npm run ui:dev

# Terminal 2: bundle Electron main/preload and load http://127.0.0.1:1421
npm run electron:dev:hot
```

`electron:dev:hot` runs a cross-platform Node launcher that sets the `DEN_DESKTOP_ELECTRON_LOAD_MODE=hot` env var internally; `VITE_DEV_SERVER_URL` can override the default `http://127.0.0.1:1421`.

**Manual step:** the sidecar `.NET` project must be buildable (`dotnet build src/DenMcp.Desktop.Sidecar`). If `dotnet` is not on PATH, the sidecar will fail to launch.

The renderer communicates with the sidecar exclusively through `window.denDesktopSidecar`, exposed by the preload via `contextBridge`. No raw token, endpoint URL, Node APIs, or shell access are available to the renderer.

## First-slice behavior

- Loads local settings from the platform app config directory.
- Maintains a stable `sourceInstanceId` for this desktop app instance.
- Checks Den health and syncs projects plus agent workspaces from a configured Den server URL.
- Scans locally visible project roots/worktrees with safe `git` process calls.
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
