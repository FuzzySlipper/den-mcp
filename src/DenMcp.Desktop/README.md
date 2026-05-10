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

## Local release/update workflow

For a simple one-user local install on the desktop machine, use the repo script:

```bash
# From any checkout of den-mcp, or after copying the script to ~/bin:
scripts/update-den-desktop
# or
update-den-desktop
```

The updater is intentionally local and deliberate rather than a GitHub Release/installer flow. It:

1. Ensures a local checkout exists at `${DEN_DESKTOP_REPO_DIR:-~/dev/den-mcp}`.
2. Fails closed if the checkout has uncommitted or untracked changes.
3. Fetches and fast-forwards `${DEN_DESKTOP_BRANCH:-main}` from origin.
4. Runs `npm ci` in `src/DenMcp.Desktop` only when `package.json`/`package-lock.json` changed or `node_modules` is missing.
5. Builds the renderer and Electron bundles with `npm run ui:build` and `npm run electron:build`.
6. Publishes the .NET sidecar into a commit-addressed release directory.
7. Copies `dist/`, `electron-dist/`, `node_modules/`, package metadata, and the sidecar schema fixture into that release.
8. Atomically updates `${DEN_DESKTOP_INSTALL_DIR:-~/.local/opt/den-desktop}/current`.
9. Writes a stable launcher at `${DEN_DESKTOP_BIN_DIR:-~/.local/bin}/den-desktop`.

Default paths:

```text
~/dev/den-mcp                                      local source checkout
~/.local/opt/den-desktop/releases/<commit>/        installed releases
~/.local/opt/den-desktop/current -> releases/...   active release symlink
~/.local/bin/den-desktop                           stable launcher
```

Launch the current release with:

```bash
den-desktop
```

The launcher prints the release commit and exports it to Electron as `DEN_DESKTOP_RELEASE_COMMIT`; the Electron main process also logs `[DenDesktop] Starting release <commit>` on startup. The same launcher points Electron at the published sidecar via `DEN_DESKTOP_SIDECAR_PATH`, so release mode does not depend on the dev `.csproj` path.

Rollback is a symlink change. The updater prints the previous release path when it replaces an existing `current` link; to roll back manually:

```bash
previous="$HOME/.local/opt/den-desktop/releases/<previous-commit>"
ln -sfn "$previous" "$HOME/.local/opt/den-desktop/current.tmp"
mv -Tf "$HOME/.local/opt/den-desktop/current.tmp" "$HOME/.local/opt/den-desktop/current"
```

Path overrides are available for testing or non-default installs:

```bash
DEN_DESKTOP_REPO_DIR="$HOME/apps/den-mcp" \
DEN_DESKTOP_INSTALL_DIR="$HOME/.local/opt/den-desktop" \
DEN_DESKTOP_BIN_DIR="$HOME/.local/bin" \
scripts/update-den-desktop
```

The existing `npm run electron:dev` and `npm run electron:dev:hot` workflows remain the active-development path and still launch the sidecar from the source project.

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
