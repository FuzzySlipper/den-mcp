# Den-owned Pi Host Security Posture

This document describes the first Den-owned Pi session host model delivered under task #1185. It is operator-facing guidance for running sandboxed Pi sessions through Den.

## Ownership model

- **Den server is the default execution host/control plane.** Operator clients, including Hermes, request lifecycle actions through Den server APIs. They should not run host shell commands, own tmux sessions, or directly control Docker.
- **The first implementation runs on the Den server host.** Future remote worker hosts are possible, but they are out of scope for this initial model.
- **Den records lifecycle and observability state.** Pi session records include task/run ownership, host id, tmux/container identifiers, launch profile/argv metadata, state timestamps, last activity, bounded output tail, and attention status.

## What Docker provides — and what it does not

Pi runs inside Docker to provide containment, repeatability, and operational friction against accidental host changes. On den-srv, Den-owned Pi sessions are intended to use a dedicated rootless Docker daemon owned by a runtime user such as `docker-rt`, reached through an explicit `DOCKER_HOST` socket path. This is **not an airtight security sandbox**.

Assume a Pi session may read or modify anything mounted into the container. A malicious or compromised process inside the container may also attempt container breakout or abuse network access. Do not treat this model as a boundary for hostile code.

Use this model for trusted agent work where the goal is controlled process ownership, reproducible environment setup, and clearer operator visibility — not for executing untrusted workloads.

## Docker daemon ownership and socket access

Do **not** give the `den-mcp` service user access to the rootful `/var/run/docker.sock` or add it to the host `docker` group for Den-owned Pi sessions. The rootful Docker group is effectively root-equivalent on the host.

The den-srv target model is instead:

- A dedicated Unix runtime user, for example `docker-rt`, owns the rootless Docker daemon.
- Rootless Docker data lives under `/data/docker-rt` or a similar `/data/...` directory owned by that runtime user.
- The daemon is started durably by systemd, typically a `docker-rt` user service with linger enabled, not by an interactive `patch` login shell.
- Den remains the control plane as `den-mcp` and reaches only that daemon through `DenMcp:PiSessionHost:DockerHost`, for example `unix:///run/den-mcp/docker-rt/docker.sock`.
- Socket access is explicit: use a dedicated Den/rootless-runtime group or ACLs on the configured socket path. That permission grants control over containers/images/volumes owned by `docker-rt`; it is narrower than rootful Docker access but still powerful and should not be granted casually.

Rootless limitations still apply. Rootless containers do not provide the same networking, privileged container, low-port binding, overlay/storage-driver, cgroup, or host-device behavior as rootful Docker. If a workload depends on rootful features, treat that as a deployment decision rather than silently falling back to `/var/run/docker.sock`.

## Filesystem access

The initial profile intentionally preserves broad development access:

- Container `/home/pi/dev` maps to configured `DEV_DIR` with **read-write** access.
- Pi state maps to `PI_STATE_DIR` / per-session state roots with **read-write** access.
- Cache volumes are writable and may be session-scoped to reduce collisions.
- The first version does **not** enforce per-repository file restrictions.

Operational implication: a Pi session can edit any project reachable through the mounted dev directory. Continue using git review, Den task state, and branch discipline as the primary guardrails.

## Credential and provider-secret posture

Pi model/provider credentials must come from the mounted Pi state/config path (`PI_STATE_DIR` mounted at `/home/pi/.pi`) rather than from the Den server process environment. Den-owned launches blank configured provider/model environment variables before Docker Compose interpolation and in the rendered shell command path, including the pi-docker compose variables `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`, `MISTRAL_API_KEY`, `GROQ_API_KEY`, `OPENROUTER_API_KEY`, `AWS_PROFILE`, and `AWS_REGION` plus related provider secret names configured under `ProviderSecretEnvironmentVariables`.

Operational implications:

- Do not rely on `export OPENAI_API_KEY=... dotnet run ...` or service-manager environment snippets to credential Den-owned Pi sessions.
- Populate the configured `PI_STATE_DIR`/`PiStateRootDir` with the Pi settings/auth state the container should use.
- Keep `ScrubProviderEnvironmentVariables` enabled unless deliberately debugging an isolated local setup.
- Keep `RequiredPiStatePaths` populated (default `agent/settings.json`) so missing Pi settings fail before tmux/Docker launch instead of falling through to server environment secrets.
- Treat logs, output tails, and task notes carefully; do not paste secrets into terminal output or Den messages.

Git, SSH, and GitHub CLI credentials may still be mounted read-only when configured. Read-only is a write-protection measure, not a secrecy measure.

- Mounted credentials are still readable by processes inside the container.
- Only mount credentials needed for the intended work.
- Prefer fallback/empty credential mounts when a session does not require repository/network authentication.

## Network and OAuth callback ports

The launch profile keeps callback ports bound to host loopback (`127.0.0.1`) by default. This avoids exposing callback listeners on external interfaces, but it does not prevent local host processes from connecting.

For multiple concurrent sessions:

- Use unique Compose project names/session ids.
- Use per-session `PI_STATE_DIR` and cache volume names.
- Allocate unique host callback ports per active session.
- The launch profile renderer validates per-profile duplicate callback ports, but the lifecycle host/operator must avoid collisions across already-running sessions.

If OAuth or provider callback behavior changes, verify that loopback binding and callback URI settings still match provider requirements.

## Setup/configuration notes

The Den server configuration under `DenMcp:PiSessionHost` / the Pi launch profile should make these points explicit in the appsettings/deployment config file:

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

On den-srv, the pi-docker compose assets must be copied to `/data/services/den-mcp/pi-docker` with any local `.env` excluded or removed; Den renders all intended compose environment directly. The live deploy script preserves `/data/services/den-mcp/app/appsettings.json`, so existing installs must manually migrate that file (`sudoedit /data/services/den-mcp/app/appsettings.json`) to the `/data/...` `PiSessionHost` paths shown above. The deploy preflight checks the preserved live file before restart and fails if those runtime path settings still contain `/home/patch`, another `/home/<user>` host tree, or an unexpanded `~`. The `den-mcp` service user must be able to traverse/read that tree, traverse `/data/dev`, read/write the configured Pi state root, and connect to the configured rootless Docker socket. The credential fallback root should contain only an empty `gitconfig` file plus empty `ssh` and `gh` directories unless specific read-only credential paths are deliberately configured.

Operators should validate these paths on the Den server host before enabling launch APIs in a shared environment:

- `DEV_DIR`: host dev root mounted as `/home/pi/dev` read-write.
- `PI_STATE_DIR` or state root: host/session state mounted read-write and pre-populated with required Pi settings/auth state.
- Image name/version and Compose file/service references.
- Optional credential paths for git config, SSH, and GH config.
- Callback container ports and per-session host port assignments.
- Host id plus `tmux` and `docker` executable paths used by the lifecycle host.
- `DockerHost`: explicit rootless daemon socket endpoint; validate as `den-mcp` with `docker version`, `docker compose version`, and `docker ps` using the same `DOCKER_HOST`.
- `TmuxShellCommand`: an installed interactive shell argv used for the initial tmux pane; keep it explicit (for example `["/bin/sh", "-i"]` or `["/bin/bash", "-i"]`) for service accounts whose passwd shell is `/usr/sbin/nologin`.
- Provider-secret scrubbing names and required Pi state paths.

## Observability and attention

Den captures bounded session observability:

- session lifecycle state and timestamps;
- last host activity time;
- bounded output tail metadata, not an unbounded terminal transcript;
- attention state/reason such as waiting for direction, blocked, user input needed, or stalled.

Attention detection is heuristic. Operators should treat it as a prompt to inspect or intervene, not as a definitive semantic judgment.

## Client responsibilities

Clients such as Hermes should:

- call Den lifecycle APIs for launch/list/detail/attach-info/terminate/cleanup;
- display Den-owned status and attention fields;
- avoid direct host shell/tmux/Docker control;
- avoid storing or redistributing raw terminal output beyond Den's bounded fields unless a future task explicitly defines that behavior.

## Future hardening candidates

Possible future work:

- remote worker hosts with narrower blast radius;
- stronger credential brokering instead of host credential mounts;
- per-repository or per-task filesystem policies;
- network egress restrictions;
- deterministic host callback port allocation/reservation;
- richer policy for secrets in logs and terminal output;
- authenticated/operator-authorized lifecycle endpoints if Den is deployed in a multi-user trust boundary.
