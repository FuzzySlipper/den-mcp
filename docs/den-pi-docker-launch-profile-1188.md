# Den-managed Pi Docker launch profile (#1188)

This slice adapts the existing `/home/patch/dev/linux/pi-docker` Compose setup into a Den-rendered launch profile. It does not start or supervise sessions; the lifecycle API is a later slice.

## Configuration points

Server config lives under `DenMcp:PiSessionHost` in the Den server config file (for example `src/DenMcp.Server/appsettings.json` or the deployed appsettings override). Prefer durable config-file settings over one-off shell environment snippets so operators can inspect the effective deployment later:

```json
{
  "DenMcp": {
    "PiSessionHost": {
      "HostId": "den-pi-host-1",
      "TmuxExecutable": "tmux",
      "TmuxShellCommand": [
        "/bin/sh",
        "-i"
      ],
      "DockerExecutable": "docker",
      "ComposeFile": "/home/patch/dev/linux/pi-docker/compose.yaml",
      "Service": "pi",
      "DevDir": "/srv/dev",
      "PiStateRootDir": "/var/lib/den-mcp/pi-sessions",
      "Image": "pi-sandbox:latest",
      "PiVersion": "0.71.0",
      "NodeVersion": "22",
      "SandboxUid": 1000,
      "SandboxGid": 1000,
      "GitConfigPath": "",
      "SshDir": "",
      "GhConfigDir": "",
      "CredentialFallbackRootDir": "/var/lib/den-mcp/pi-credential-fallbacks",
      "HostCallbackBindAddress": "127.0.0.1",
      "ScrubProviderEnvironmentVariables": true,
      "ProviderSecretEnvironmentVariables": [
        "ANTHROPIC_API_KEY",
        "OPENAI_API_KEY",
        "GEMINI_API_KEY",
        "MISTRAL_API_KEY",
        "GROQ_API_KEY",
        "OPENROUTER_API_KEY",
        "AWS_PROFILE",
        "AWS_REGION"
      ],
      "RequiredPiStatePaths": [
        "agent/settings.json"
      ]
    }
  }
}
```

Key fields:

- `HostId`: stable id recorded on Den Pi session records; defaults to the server machine name when empty.
- `TmuxExecutable`, `DockerExecutable`: explicit executable names or absolute paths used by the lifecycle host.
- `TmuxShellCommand`: explicit tmux pane shell argv, default `["/bin/sh", "-i"]`; set to an installed interactive shell (for example `["/bin/bash", "-i"]`) so service users with `/usr/sbin/nologin` passwd shells still get a stable pane.
- `ComposeFile`: base pi-docker compose file, default `/home/patch/dev/linux/pi-docker/compose.yaml`.
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
- environment values (`DEV_DIR`, `PI_STATE_DIR`, image/version, credential paths, and scrubbed provider variables blanked to empty strings)
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
