# Pi Session Source/Deployment Authority — Task #1238

Date: 2026-05-10
Project: `den-mcp`
Parent task: #1237 — Convert orchestrator workflow to Hermes-driven Den Pi workers

## Conclusion

For #1239+ implementation, use the normal `den-mcp` repository as the source of truth. In the live den-srv deployment notes, the canonical agent/dev workspace is `/data/dev/den-mcp` on branch `main`; service/runtime trees under `/data/services/den-mcp` are deployment artifacts and must not be treated as the editable source tree.

In this temporary Discord/Hermes workflow, Patch provided the working clone at:

```text
/home/dev/den-mcp
```

That temp clone is valid for current implementation work. It contains the current Pi-session server code and deploy script shape needed for #1239+.

## Evidence checked in this session

### Den task context

Planner refined #1238 after #1254 so it is a short codification/verification task, not a new large audit.

The existing sysadmin inventory note on #1238 states:

- `/data/dev/den-mcp` — real dev repo, origin `git@github.com:FuzzySlipper/den-mcp.git`, branch `main`, HEAD `f575558`; appears to contain current Pi-session related files/untracked work.
- `/data/services/den-mcp/repo` — service/deployment copy, origin `/home/patch/dev/den-mcp`, branch `task/signal-runtime-followups`, HEAD `5be58b8`; stale/confusing for inspection.
- `/data/services/den-mcp-backup/repo` — backup/service copy, git metadata not cleanly readable.
- old Den metadata may have pointed at `/home/patch/dev/den-mcp` and should not guide agents toward stale code.

Planner's #1237 note recommended closing this loop first, then proceeding to #1239.

### Current project metadata

`mcp_den_get_project(project_id="den-mcp")` now reports:

```text
root_path: den-mcp
```

It no longer reports `/home/patch/dev/den-mcp` from the old sysadmin note. Because this run is intentionally using a temp clone at `/home/dev/den-mcp`, no Den project-root update was attempted from this session.

### Local/temp clone inspection

The live `/data/...` den-srv paths are not mounted in this temporary environment; direct filesystem checks from this runner returned `missing` for:

- `/data/dev/den-mcp`
- `/data/services/den-mcp/repo`
- `/data/services/den-mcp/app`
- `/data/services/den-mcp-backup/repo`
- `/home/patch/dev/den-mcp`

The working temp clone is readable and has origin `git@github.com:FuzzySlipper/den-mcp.git`.

At the time of this verification:

```text
/home/dev/den-mcp
branch: task/1238-source-deployment-authority
base work includes #1254 commit 244ba7e
```

### Pi-session source exists in the temp clone

The temp clone contains the current server-side Pi-session and launch-profile surfaces that #1239 should build on:

- `src/DenMcp.Server/Routes/PiSessionRoutes.cs`
  - `POST /api/projects/{projectId}/pi-sessions/`
  - `GET /api/projects/{projectId}/pi-sessions/`
  - `GET /api/projects/{projectId}/pi-sessions/{sessionId}`
  - attach, terminate, and cleanup routes.
- `src/DenMcp.Core/Services/PiSessionService.cs`
  - launch/list/get/terminate/cleanup/attach service methods.
  - records launch profile JSON, launch command display, host/tmux/container state, output tail, attention state, and lifecycle audit ops.
- `src/DenMcp.Core/Services/PiSessionHost.cs`
  - `IPiSessionHost` abstraction.
  - `TmuxDockerPiSessionHost` runtime adapter for tmux + Docker Compose.
- `src/DenMcp.Server/Routes/PiLaunchProfileRoutes.cs`
  - render/defaults API for Pi Docker launch profiles.
- `src/DenMcp.Core/Services/PiDockerLaunchProfileRenderer.cs`
  - Den-rendered compose profile/command construction.
- `src/DenMcp.Core/Models/PiSession.cs`
  - session records, requests, summaries/details, attach/control DTOs.
- `src/DenMcp.Core/Data/PiSessionRepository.cs`
  - persistence for Pi session records.

This confirms the temp clone includes the Pi session substrate and is safe to use for #1239 implementation in the current workflow.

### Deployment path documentation exists

The repo deploy script and docs identify the intended live deployment path:

- `scripts/deploy-live-server.sh`
  - local mode is explicitly described as running on den-srv from `/data/dev/den-mcp` and installing into `/data/services/den-mcp/app`.
  - auto mode selects local when the repository path is under `/data/dev/den-mcp` and `/data/services/den-mcp/app` exists.
  - remote mode can upload/publish from another workstation, defaulting to `patch@192.168.1.10`.
  - deploy preserves `/data/services/den-mcp/app/appsettings.json`.
  - deploy syncs pi-docker assets to `/data/services/den-mcp/pi-docker`, creates `/data/services/den-mcp/pi-sessions`, credential fallback directories, and `/data/dev`.
- `docs/den-pi-docker-launch-profile-1188.md`
  - states live den-srv systemd service runs as user `den-mcp` from `/data/services/den-mcp/app`.
  - warns not to point deployed `PiSessionHost` at `/home/patch` or `~` paths.
  - identifies `scripts/deploy-live-server.sh` as the deployment path.
- `docs/den-owned-pi-host-security-1191.md`
  - documents the rootless `docker-rt`/`DenMcp:PiSessionHost:DockerHost` posture and `/data/services/den-mcp` runtime paths.

## Safe deployment rule for #1239+

When changes are ready to deploy to live den-srv:

1. Treat `/data/dev/den-mcp` as the canonical live dev checkout.
2. Treat `/data/services/den-mcp` as runtime/deployment output, not source.
3. Use `scripts/deploy-live-server.sh --local` from `/data/dev/den-mcp` on den-srv when doing the preferred local deploy.
4. Preserve deployed `/data/services/den-mcp/app/appsettings.json`; do not overwrite Pi-session host config with workstation defaults.
5. Do not inspect or edit `/data/services/den-mcp/repo` as if it were current source unless a separate cleanup task proves it contains unique work.
6. For the current temp workflow, implement and validate in `/home/dev/den-mcp`, then reconcile/push/apply through the normal repo/deploy path rather than copying edits into service artifacts.

## Open limitation

This runner's temp environment cannot directly inspect the live den-srv `/data/...` trees because they are not mounted here. Therefore, this task relies on:

- the existing sysadmin inventory note as live-path evidence;
- the current Den task/planner update;
- source inspection in the provided temp clone;
- deploy-script/docs evidence in the repo.

That is sufficient to unblock #1239 for the temp `/home/dev/den-mcp` implementation workflow. A later sysadmin cleanup task can archive or remove stale service repo copies after reviewing any unique/uncommitted files.

## Decision

#1239+ should proceed in `/home/dev/den-mcp` for this session. For live den-srv deployment, the intended canonical source remains `/data/dev/den-mcp` and the deploy target remains `/data/services/den-mcp/app` via `scripts/deploy-live-server.sh`.
