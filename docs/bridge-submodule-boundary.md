# Den.Bridge Submodule Boundary

## Overview

`Den.Bridge` lives in a separate git repository consumed as a submodule at
`external/den-bridge`. This keeps generic bridge/sidecar infrastructure
reusable across projects with similar Electron or web-based desktop needs.

- **Submodule URL**: `git@github.com:FuzzySlipper/den-bridge.git`
- **Submodule path**: `external/den-bridge`
- **Commit**: `9a084eb1c423afac091688fae370482dcb8c8340`

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

### DenMcp.Desktop.Sidecar (Den-specific — lives in den-mcp)

| Area | Responsibility |
|------|---------------|
| `AppCore` | Den-specific handlers, DTOs, services (tasks, messages, documents, terminal, console, app-agent, collaboration) |
| `DesktopSidecarBridge` | Sidecar DI composition and bridge registry configuration |
| `DesktopSidecarProtocol` | Den-desktop command/event constants and schema definitions |
| `DesktopSidecarRuntime` | Process health, uptime, runtime state |
| `DesktopSidecarStartup` | Ready sentinel formatting |

**Rules**:
- References `Den.Bridge` for generic abstractions.
- References `DenMcp.Core` for domain models.
- No references to Electron, Tauri, or WebView packages.
- Enforced by `DesktopSidecarBoundaryTests.Sidecar_DoesNotReferenceElectronOrDesktopRendererPackages`.

## TypeScript Boundary

The TypeScript side has three conceptual layers. For stability, the generic code
remains in `src/DenMcp.Desktop/src/` but is clearly scoped for future extraction.

### Layer 1: Generic bridge contract (`src/DenMcp.Desktop/src/bridge/`)

**Files**:
- `src/bridge/contract.ts`

**Responsibility**: Pure bridge frame types, schema validation, checked client,
command facade, and transport contract. No Den-specific concepts. No Electron APIs.

**Future home**: `external/den-bridge/packages/den-bridge/` (npm package).

### Layer 2: Generic WebSocket transport (`src/DenMcp.Desktop/src/electron/`)

**Files**:
- `src/electron/sidecarBridgeConnection.ts`

**Responsibility**: WebSocket-based `BridgeClientTransport` implementation.
Works with any WebSocket constructor (DOM or Node `ws`). No Den-specific
concepts except the file location.

**Future home**: `external/den-bridge/packages/den-bridge/` or
`packages/den-bridge-electron/` if Electron preload wiring is added.

### Layer 3: Den-specific protocol and API (`src/DenMcp.Desktop/src/electron/` + `src/desktop/`)

**Files**:
- `src/electron/sidecarProtocol.ts` — Den-desktop command/event specs, ready sentinel, protocol version constants
- `src/electron/preloadSidecarApi.ts` — Electron preload API surface for Den Desktop
- `src/desktop/sidecarBridgeApi.ts` — Den-specific DTOs and sidecar API types

**Responsibility**: Den Desktop product-specific commands, events, DTOs, and
Electron preload wiring. This code must stay in `den-mcp`.

**Future home**: stays in `den-mcp` (or a Den-specific desktop support package).

## Boundary Test Matrix

| Test | Location | What it enforces |
|------|----------|------------------|
| `BridgeBoundaryTests.BridgeProject_DoesNotReferenceDenMcpDomainOrUiAssemblies` | `external/den-bridge/tests/Den.Bridge.Tests/` | Den.Bridge has no product/ UI package refs |
| `DesktopSidecarBoundaryTests.Sidecar_DoesNotReferenceElectronOrDesktopRendererPackages` | `tests/DenMcp.Desktop.Sidecar.Tests/` | Sidecar has no Electron/WebView/Tauri refs |

## Build / Test References After Submodule

| Project | New Path |
|---------|----------|
| Den.Bridge | `external/den-bridge/src/Den.Bridge/Den.Bridge.csproj` |
| Den.Bridge.Tests | `external/den-bridge/tests/Den.Bridge.Tests/Den.Bridge.Tests.csproj` |
| DenMcp.Desktop.Sidecar reference to Den.Bridge | `..\..\external\den-bridge\src\Den.Bridge\Den.Bridge.csproj` |
| DenMcp.Desktop.Sidecar.Tests reference to Den.Bridge | `..\..\external\den-bridge\src\Den.Bridge\Den.Bridge.csproj` |

## Adding or Updating the Submodule

```bash
# Initial clone (already done)
git submodule add git@github.com:FuzzySlipper/den-bridge.git external/den-bridge

# Update to latest
cd external/den-bridge && git pull origin main
cd ../.. && git add external/den-bridge && git commit -m "update den-bridge submodule"
```

## Open Decision: TypeScript Extraction

The generic TypeScript bridge contract (`bridge/contract.ts`) and WebSocket transport
(`electron/sidecarBridgeConnection.ts`) are identified as reusable but remain in
`den-mcp` for now. Extracting them into `external/den-bridge/packages/den-bridge`
as a publishable npm package is a follow-up task once:

1. The npm package layout and build toolchain are defined.
2. Consumer import paths can be migrated safely.
3. Electron-specific helpers have a separate `packages/den-bridge-electron` boundary.
