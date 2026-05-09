# Den-managed Pi Docker launch profile (#1188)

This slice adapts the pi-docker Compose setup into a Den-rendered launch profile. On den-srv the compose assets are deployed to `/data/services/den-mcp/pi-docker` so the `den-mcp` service user can read them without traversing `/home/patch`. It does not start or supervise sessions; the lifecycle API is a later slice.

## Configuration points

Server config lives under `DenMcp:PiSessionHost` in the Den server config file (for example `src/DenMcp.Server/appsettings.json` or the deployed appsettings override). Prefer durable config-file settings over one-off shell environment snippets so operators can inspect the effective deployment later:

```json
{
  "DenMcp": {
    "PiSessionHost": {
      "HostId": "den-srv",
      "TmuxExecutable": "/usr/bin/tmux",
      "TmuxShellCommand": [
        "/bin/sh",
        "-i"
      ],
      "DockerExecutable": "/usr/bin/docker",
      "DockerHost": "unix:///run/den-mcp/docker-rt/docker.sock",
      "ComposeFile": "/data/services/den-mcp/pi-docker/compose.yaml",
      "Service": "pi",
      "DevDir": "/data/dev",
      "PiStateRootDir": "/data/services/den-mcp/pi-sessions",
      "Image": "pi-sandbox:latest",
      "PiVersion": "0.71.0",
      "NodeVersion": "22",
      "SandboxUid": 1000,
      "SandboxGid": 1000,
      "GitConfigPath": "",
      "SshDir": "",
      "GhConfigDir": "",
      "CredentialFallbackRootDir": "/data/services/den-mcp/pi-credential-fallbacks",
      "HostCallbackBindAddress": "127.0.0.1",
      "ScrubProviderEnvironmentVariables": true,
      "ProviderSecretEnvironmentVariables": [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "OPENAI_API_KEY",
        "OPENAI_ORG_ID",
        "OPENAI_PROJECT_ID",
        "GEMINI_API_KEY",
        "GOOGLE_API_KEY",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "MISTRAL_API_KEY",
        "GROQ_API_KEY",
        "OPENROUTER_API_KEY",
        "AWS_PROFILE",
        "AWS_REGION",
        "AWS_DEFAULT_REGION",
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AZURE_OPENAI_API_KEY",
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_API_KEY",
        "COHERE_API_KEY",
        "TOGETHER_API_KEY",
        "XAI_API_KEY",
        "DEEPSEEK_API_KEY",
        "PERPLEXITY_API_KEY"
      ],
      "RequiredPiStatePaths": [
        "agent/settings.json"
      ]
    }
  }
}
```

Key fields:

- `HostId`: stable id recorded on Den Pi session records; den-srv uses explicit `den-srv` in the deployed config.
- `TmuxExecutable`, `DockerExecutable`: explicit executable names or absolute paths used by the lifecycle host; den-srv uses `/usr/bin/tmux` and `/usr/bin/docker`.
- `DockerHost`: optional explicit Docker daemon endpoint mapped to `DOCKER_HOST` for launch and cleanup. den-srv uses `unix:///run/den-mcp/docker-rt/docker.sock`, a dedicated rootless Docker daemon owned by `docker-rt`; leaving this empty lets the Docker CLI fall back to its default socket and is not the intended live Den-owned Pi configuration.
- `TmuxShellCommand`: explicit tmux pane shell argv, default `["/bin/sh", "-i"]`; set to an installed interactive shell (for example `["/bin/bash", "-i"]`) so service users with `/usr/sbin/nologin` passwd shells still get a stable pane.
- `ComposeFile`: base pi-docker compose file, default `/data/services/den-mcp/pi-docker/compose.yaml` for the den-srv service user.
- `Service`: compose service to run, default `pi`.
- `DevDir`: broad host development root mounted read-write at `/home/pi/dev`.
- `PiStateRootDir`: root for per-session `PI_STATE_DIR` directories mounted read-write at `/home/pi/.pi`.
- `Image`, `PiVersion`, `NodeVersion`: image/build version inputs passed through compose environment.
- `SandboxUid`, `SandboxGid`: container user id/group id build args.
- `GitConfigPath`, `SshDir`, `GhConfigDir`: optional read-only credential mount sources.
- `CredentialFallbackRootDir`: empty fallback mount roots used when a credential path is intentionally not configured.
- `HostCallbackBindAddress`: fixed to host loopback (`127.0.0.1`).
- `ScrubProviderEnvironmentVariables` and `ProviderSecretEnvironmentVariables`: blank provider/model credential variables before Docker Compose interpolation and again in the shell command path.
- `RequiredPiStatePaths`: relative paths under `PI_STATE_DIR` that must exist before launch; defaults to `agent/settings.json` so missing Pi settings surface immediately instead of being hidden by provider environment fallback.

The pi-docker callback container ports are currently `1455`, `53692`, `8085`, and `51121`, but they are not applied as launch defaults. Each launch must provide explicit host/container callback mappings.

## den-srv deployment paths

The live den-srv systemd service runs as Unix user `den-mcp` with HOME under `/data/services/den-mcp/server`. Do not point the deployed `PiSessionHost` at `/home/patch` or `~` paths: `/home/patch` is not traversable by the service user, and `~` expands to the service HOME.

Docker access is intentionally through a dedicated rootless runtime user, not through `/var/run/docker.sock` and not through the rootful `docker` group. The recommended live shape is:

- Unix user: `docker-rt` (service/runtime account only).
- Docker data root: `/data/docker-rt`, owned by `docker-rt` and not writable by `den-mcp`.
- Rootless daemon: managed by a durable systemd user service for `docker-rt` with linger enabled, for example an override that starts rootless dockerd with `--data-root /data/docker-rt -H unix:///run/den-mcp/docker-rt/docker.sock`.
- Socket access: the socket directory is a deliberate shared runtime path such as `/run/den-mcp/docker-rt`, with group ownership/mode or ACLs granting `den-mcp` access to that rootless daemon socket only. A group used for this must be a Den/rootless-runtime group (for example `den-pi-docker`), not the rootful host `docker` group.
- Den config: `DenMcp:PiSessionHost:DockerHost` is set to the exact socket endpoint (`unix:///run/den-mcp/docker-rt/docker.sock`) so tmux launches and cleanup both target the same daemon without relying on any `patch` login state.

With that config, the rendered launch profile includes `DOCKER_HOST`; tmux receives it via `tmux new-session -e`, the recorded shell command prefixes it with `env DOCKER_HOST=...`, and cleanup passes it through the bounded process runner when invoking `docker compose down`.

Use `scripts/deploy-live-server.sh` to deploy compose assets along with the server publish output. The script copies the local pi-docker checkout from `PI_DOCKER_SOURCE` (or from a detected sibling checkout at `../pi-docker` or `../linux/pi-docker`) to `REMOTE_PI_DOCKER_DIR` (default `/data/services/den-mcp/pi-docker`) with `.env` excluded and removed on the remote side. It also creates:

- `/data/services/den-mcp/pi-sessions` for per-session `PI_STATE_DIR` roots;
- `/data/services/den-mcp/pi-credential-fallbacks/gitconfig` as an empty read-only gitconfig fallback;
- `/data/services/den-mcp/pi-credential-fallbacks/ssh` and `/data/services/den-mcp/pi-credential-fallbacks/gh` as empty credential fallback directories;
- `/data/dev` as the service-traversable development root mounted at `/home/pi/dev`.

The live deploy script intentionally preserves `/data/services/den-mcp/server/appsettings.json`. Before restarting, `scripts/deploy-live-server.sh` now preflights the preserved live `DenMcp:PiSessionHost` section and fails if deployed runtime paths still point at `/home/patch`, another `/home/<user>` tree, or an unexpanded `~`. The same preflight verifies the den-srv path conventions it will deploy: `ComposeFile` under `/data/services/den-mcp/pi-docker`, `DevDir` at `/data/dev`, `PiStateRootDir` under `/data/services/den-mcp/pi-sessions`, and `CredentialFallbackRootDir` under `/data/services/den-mcp/pi-credential-fallbacks` unless the deploy is run with matching `REMOTE_*` overrides.

Manual migration step for existing live installs: edit the preserved deployed config (for example `ssh den-srv` then `sudoedit /data/services/den-mcp/server/appsettings.json`) and apply the explicit JSON above before rerunning the deploy/restart. Also validate the same environment the service will use before launching a session:

```bash
sudo -u den-mcp env DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock docker version
sudo -u den-mcp env DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock docker compose version
sudo -u den-mcp env DOCKER_HOST=unix:///run/den-mcp/docker-rt/docker.sock docker ps
```

Ensure `pi-sandbox:latest` exists in the dedicated rootless daemon's image store; rootful images and images under another rootless user are separate and must be rebuilt, loaded, or imported for `docker-rt` if missing. After deploying task #1214's tmux shell fix and these paths, rerun render plus lifecycle cleanup/launch smoke checks against the live service.

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
- environment values (`DEV_DIR`, `PI_STATE_DIR`, image/version, credential paths, `DOCKER_HOST` when configured, and scrubbed provider variables blanked to empty strings)
- `scrubbed_environment_variables`, the exact provider/model env names Den will blank for launch
- effective volume mounts and read-only/read-write posture
- `docker compose ... config`, `build`, and `run` argument vectors
- loopback callback port mappings
- cache volume names
- warnings and known limitations

## Concurrency posture

The renderer avoids accidental state/cache collisions by deriving a unique Compose project name and default `PI_STATE_DIR` from `session_id`. Compose named cache volumes are therefore session-scoped. `session_id` must be unique for each live launch; reusing a session id intentionally reuses Compose names and state paths.

Callback ports are intentionally not defaulted from the static pi-docker compose ports. A caller must provide explicit host ports for each launch; the renderer validates loopback binding and duplicate ports within one profile. It does not probe or reserve host ports across active sessions. The lifecycle API should allocate and reserve unique host callback ports before launch.

## Provider credential posture

Den-owned Pi sessions are intended to use credentials/config from the mounted `PI_STATE_DIR` (`/home/pi/.pi`) only. The Den server may itself have provider secrets in its process environment, but launch rendering blanks the configured provider/model variables in the tmux session environment and prefixes the Docker Compose command with empty assignments such as `OPENAI_API_KEY=`. This prevents Compose variable interpolation from quietly copying server-owned provider keys into the Pi container.

If `PI_STATE_DIR` is missing or does not contain the configured `RequiredPiStatePaths`, the rendered profile warns and the lifecycle host fails before creating the tmux session. Operators should populate the configured Pi state root (or override `pi_state_dir` for a launch) with the Pi settings/auth state they intend the container to use.

## Security posture in this first version

The profile intentionally preserves broad read-write `DEV_DIR` access and does not add per-repository restrictions. Optional git/SSH/GH credential mounts remain read-only. Callback ports are bound only to `127.0.0.1`.
