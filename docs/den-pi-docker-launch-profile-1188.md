# Den-managed Pi Docker launch profile (#1188)

This slice adapts the existing `/home/patch/dev/linux/pi-docker` Compose setup into a Den-rendered launch profile. It does not start or supervise sessions; the lifecycle API is a later slice.

## Configuration points

Server config lives under `DenMcp:PiSessionHost`:

- `ComposeFile`: base pi-docker compose file, default `/home/patch/dev/linux/pi-docker/compose.yaml`.
- `Service`: compose service to run, default `pi`.
- `DevDir`: broad host development root mounted read-write at `/home/pi/dev`.
- `PiStateRootDir`: root for per-session `PI_STATE_DIR` directories mounted read-write at `/home/pi/.pi`.
- `Image`, `PiVersion`, `NodeVersion`: image/build version inputs passed through compose environment.
- `SandboxUid`, `SandboxGid`: container user id/group id build args.
- `GitConfigPath`, `SshDir`, `GhConfigDir`: optional read-only credential mount sources.
- `CredentialFallbackRootDir`: empty fallback mount roots used when a credential path is intentionally not configured.
- `HostCallbackBindAddress`: fixed to host loopback (`127.0.0.1`).

The pi-docker callback container ports are currently `1455`, `53692`, `8085`, and `51121`, but they are not applied as launch defaults. Each launch must provide explicit host/container callback mappings.

## Rendering API

`POST /api/projects/{projectId}/pi-launch-profile/render`

Required per launch:

- `session_id`
- `callback_ports`: explicit host/container callback mappings for this session

Optional per launch:

- `task_id`, `workspace_id`, `title`
- overrides for `dev_dir`, `pi_state_dir`, compose/image/version fields, and credential paths

The response includes:

- compose project name and profile id
- compose file/service reference
- environment values (`DEV_DIR`, `PI_STATE_DIR`, image/version, credential paths)
- effective volume mounts and read-only/read-write posture
- `docker compose ... config`, `build`, and `run` argument vectors
- loopback callback port mappings
- cache volume names
- warnings and known limitations

## Concurrency posture

The renderer avoids accidental state/cache collisions by deriving a unique Compose project name and default `PI_STATE_DIR` from `session_id`. Compose named cache volumes are therefore session-scoped. `session_id` must be unique for each live launch; reusing a session id intentionally reuses Compose names and state paths.

Callback ports are intentionally not defaulted from the static pi-docker compose ports. A caller must provide explicit host ports for each launch; the renderer validates loopback binding and duplicate ports within one profile. It does not probe or reserve host ports across active sessions. The lifecycle API should allocate and reserve unique host callback ports before launch.

## Security posture in this first version

The profile intentionally preserves broad read-write `DEV_DIR` access and does not add per-repository restrictions. Optional git/SSH/GH credential mounts remain read-only. Callback ports are bound only to `127.0.0.1`.
