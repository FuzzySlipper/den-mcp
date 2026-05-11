# Den.Bridge Submodule Boundary

## Overview

`Den.Bridge` lives in a separate git repository consumed as a submodule at
`external/den-bridge`. This keeps generic bridge/sidecar infrastructure reusable
across projects with similar Electron or web-based desktop needs.

- **Submodule URL**: `git@github.com:FuzzySlipper/den-bridge.git`
- **Submodule path**: `external/den-bridge`
- **Desktop consumer**: the standalone `den-desktop` repository

## .NET Boundary

### Den.Bridge (generic — lives in submodule)

| Area | Responsibility |
|------|---------------|
| `Abstractions` | Handler contracts, transport interfaces, error types |
| `Protocol` | JSON frame types, serialization, wire protocol constants |
| `Registry` | Command/event registry and builder |
| `Schema` | Schema bundle generation and named schema helpers |
| `Transport/WebSockets` | WebSocket server/client transport |
| `Hosting` | Bridge host integration, DI extensions, command invoker |
| `InMemory` | In-memory test harness for unit tests |

**Rules**:
- No references to `DenMcp.*` assemblies.
- No references to Electron, Tauri, WebView, ASP.NET Core, or Terminal.Gui packages.
- Enforced by `BridgeBoundaryTests.BridgeProject_DoesNotReferenceDenMcpDomainOrUiAssemblies`.

### Den Desktop sidecar (Den-specific — lives in `den-desktop`)

The product sidecar/app-core moved out of this repository during the physical
`den-desktop` extraction. The desktop repo owns:

| Area | Responsibility |
|------|---------------|
| `src/DenMcp.Desktop.Sidecar/AppCore` | Den-specific handlers, DTOs, services (tasks, messages, documents, terminal, console, app-agent, collaboration) |
| `DesktopSidecarBridge` | Sidecar DI composition and bridge registry configuration |
| `DesktopSidecarProtocol` | Den Desktop command/event constants and schema definitions |
| `DesktopSidecarRuntime` | Process health, uptime, runtime state |
| `DesktopSidecarStartup` | Ready sentinel formatting |

**Rules**:
- References `Den.Bridge` for generic abstractions.
- Does not project-reference `DenMcp.Core`; desktop-only contracts copied into the desktop repo should become formal API/package contracts before being shared again.
- No references to Electron, Tauri, or WebView packages from the .NET sidecar project.
- Desktop boundary tests run in the `den-desktop` repository.

## TypeScript Boundary

The TypeScript/Electron bridge consumer code moved with Den Desktop to the
standalone `den-desktop` repository:

| Layer | Current owner |
|-------|---------------|
| Generic bridge frame/client contract | `den-desktop` until promoted into `den-bridge` npm package |
| WebSocket bridge transport | `den-desktop` until promoted into `den-bridge` / `den-bridge-electron` package |
| Den Desktop protocol, preload API, and renderer DTOs | `den-desktop` |

Reusable TypeScript helpers should be extracted into `external/den-bridge`
under a package boundary such as `packages/den-bridge` or
`packages/den-bridge-electron`; do not reintroduce desktop TypeScript code into
`den-mcp` as a legacy fallback.

## Boundary Test Matrix

| Test | Location | What it enforces |
|------|----------|------------------|
| `BridgeBoundaryTests.BridgeProject_DoesNotReferenceDenMcpDomainOrUiAssemblies` | `external/den-bridge/tests/Den.Bridge.Tests/` | Den.Bridge has no product/UI package refs |
| Desktop sidecar boundary tests | `den-desktop/tests/DenMcp.Desktop.Sidecar.Tests/` | Sidecar has no Electron/WebView/Tauri refs |

## Build / Test References

| Project | Path |
|---------|------|
| Den.Bridge | `external/den-bridge/src/Den.Bridge/Den.Bridge.csproj` |
| Den.Bridge.Tests | `external/den-bridge/tests/Den.Bridge.Tests/Den.Bridge.Tests.csproj` |
| Den Desktop sidecar | `den-desktop/src/DenMcp.Desktop.Sidecar/DenMcp.Desktop.Sidecar.csproj` |
| Den Desktop sidecar tests | `den-desktop/tests/DenMcp.Desktop.Sidecar.Tests/DenMcp.Desktop.Sidecar.Tests.csproj` |

## Adding or Updating the Submodule

```bash
# Initial clone (already done)
git submodule add git@github.com:FuzzySlipper/den-bridge.git external/den-bridge

# Update to latest
cd external/den-bridge && git pull origin main
cd ../.. && git add external/den-bridge && git commit -m "update den-bridge submodule"
```

## Open Decision: TypeScript Extraction

The generic TypeScript bridge contract and WebSocket transport are reusable but
currently live in `den-desktop`. Extract them into `external/den-bridge` once:

1. The npm package layout and build toolchain are defined.
2. Consumer import paths can be migrated safely.
3. Electron-specific helpers have a separate `packages/den-bridge-electron` boundary.
