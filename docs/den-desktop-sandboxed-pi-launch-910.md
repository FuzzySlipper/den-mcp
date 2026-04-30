# Den Desktop Sandboxed Pi Launch Research (Task 910)

Task #910 investigated how Den Desktop should eventually launch long-running Pi/agent sessions in a bounded sandbox while preserving the Electron/TypeScript + .NET app-core bridge architecture.

## Summary decision

The first practical sandbox path should be a **Docker Compose Pi sandbox launched as a tmux-backed `OperatorSession`**, not a renderer-owned terminal command and not an SDK integration that bypasses app-core authority.

Recommended v1 shape:

```text
Renderer action
  -> typed preload API: start sandboxed Pi session
  -> typed bridge command
  -> .NET app-core validates project/task/cwd/capabilities
  -> .NET app-core asks Electron/main or a local process backend to start/ensure the Docker sandbox
  -> .NET app-core creates/rediscover tmux-backed OperatorSession metadata
  -> Den receives structured session snapshots/events only
```

The launched Pi process can initially be a normal interactive CLI in a container tmux session. RPC/SDK integration remains useful later for richer app-agent interaction, but containerized CLI-in-tmux best matches the current OperatorSession/tmux persistence model and the existing `pi-docker` experiment.

## Inputs surveyed

### Existing `pi-docker` experiment

Location: `/home/patch/dev/linux/pi-docker/`.

Key files:

- `README.md`
- `compose.yaml`
- `Dockerfile`
- `Makefile`
- `scripts/init-env.sh`
- host launcher: `/home/patch/.local/bin/pid`

Important observed properties:

- Base image is `node:<version>-bookworm`; Pi is installed via `npm install -g @mariozechner/pi-coding-agent@${PI_VERSION}`.
- Container user is built with host UID/GID so writes to bind-mounted trees are not root-owned.
- `DEV_DIR` is bind-mounted read/write to `/home/pi/dev`.
- Host `~/.pi` is bind-mounted read/write to `/home/pi/.pi`, so OAuth tokens, settings, extensions, sessions, and auth state carry into the sandbox.
- `~/.gitconfig`, `~/.ssh`, and `~/.config/gh` are mounted read-only.
- Network is unrestricted by default.
- `sudo` is passwordless inside the container.
- OAuth callback ports are published to host loopback for subscription login flows.
- The host `pid` launcher starts a long-running `sandbox` service, creates a tmux session inside the container, runs `pi`, and attaches/detaches with `Ctrl+Z`.
- `pid` scopes sessions by target git repo/path and rejects targets outside `DEV_DIR`.

Security assessment: this is useful **accidental-damage friction**, not strong isolation. The agent can fully mutate `DEV_DIR` and host Pi auth/config, can read Git/SSH/GH credentials, has unrestricted network, and has passwordless sudo inside the container.

### Current Pi launch options

From the installed Pi docs (`0.70.6` locally):

1. **Interactive CLI**: `pi [options] [messages...]`
   - Best for human/terminal control and current Den Desktop `OperatorSession` terminal/tmux model.
   - Supports `--model`, `--provider`, `--tools`, `--no-builtin-tools`, `--no-tools`, `--session`, `--fork`, `--session-dir`, `--no-session`, extension/skill/resource controls.
2. **Print / JSON mode**: `pi -p` and `pi --mode json`
   - Best for bounded one-shot prompts, not long-running interactive sessions.
3. **RPC mode**: `pi --mode rpc`
   - JSONL over stdin/stdout, with prompt/steer/follow-up/abort/session/model/bash commands and streamed events.
   - Better for future app-agent control when Den Desktop wants structured model/tool events without terminal emulation.
4. **SDK**: `@mariozechner/pi-coding-agent` APIs such as `createAgentSession()` / `createAgentSessionRuntime()`.
   - Best for a Node-hosted adapter with direct event subscriptions and custom tools/resource loaders.
   - Less convenient for .NET app-core ownership unless hosted behind a separate Node adapter process.

## Sandbox requirements

### Filesystem and worktree access

Minimum v1:

- Require an explicit project/worktree root from Den Desktop selection.
- Reject launch paths outside configured `DEV_DIR` / allowed workspace roots.
- Prefer a task worktree path over broad project root when available.
- Mount the chosen dev root read/write only as needed for coding tasks.
- Mount host credentials read-only where possible.
- Do not mount host root, Docker socket, browser profile, arbitrary home directories, or Den Desktop app config.

Follow-up hardening:

- Task-scoped worktrees by default.
- Optional read-only project mount plus separate writable worktree/output mount for review-only or docs-only runs.
- Dedicated sandbox Pi config/auth directory instead of direct host `~/.pi` when auth friction is acceptable.

### Git and credentials

Minimum v1:

- Allow git identity/config and SSH/GH credentials read-only, matching the experiment.
- Record in `OperatorSession.Capabilities.Constraints` whether credentials are mounted and at what access level.
- Treat commits/pushes as normal in-sandbox agent actions, not Den Desktop shell commands.

Follow-up hardening:

- Prefer SSH agent forwarding or short-lived credentials over mounting private keys.
- Consider per-project deploy keys or restricted GitHub tokens for high-risk projects.

### Network policy

Minimum v1:

- Acknowledge unrestricted network as a capability warning.
- Bind OAuth callback ports to `127.0.0.1` only.
- Do not expose sidecar bridge tokens or loopback sidecar URLs inside the sandbox.

Follow-up hardening:

- Add Docker network profiles: unrestricted, Den-only + provider endpoints, and offline/read-only.
- Route Den access through explicit MCP/API configuration rather than sharing the desktop sidecar authority.

### Den access

Minimum v1:

- Pi inside the sandbox may access Den through normal CLI/MCP config in `~/.pi`, but Den Desktop remains the authority for local session controls.
- Den Desktop publishes structured snapshots/events and task handoff packets; it does not publish raw terminal bytes.
- Do not give the renderer sidecar tokens or generic command dispatch to operate the sandbox.

Follow-up hardening:

- Generate a scoped Den token/config per sandbox run when Den supports it.
- Store sandbox run id / trace id in Den agent-stream ops and task-thread packets.

### Session artifact capture

Minimum v1:

- Register sandbox launches as `OperatorSession` records with `kind=agent`, `backend=tmux` initially, and constraints such as `sandbox_kind=docker_compose_pi`.
- Capture bounded recent activity summaries and lifecycle/control events.
- Keep raw terminal stream local to the bridge/terminal protocol.
- Link session id, project id, task id, cwd, sandbox profile, image/version, container/service/session name, and Pi session file when known.

Follow-up hardening:

- Add a Pi artifact observer inside the container or host-side mapping for `/home/pi/.pi/agent/sessions`.
- Hash and record context packets/session summaries without copying secrets.

### Model/tool restrictions

Minimum v1:

- Launch Pi with explicit CLI flags derived from typed policy: `--model`/`--provider` when selected, and `--tools` or `--no-tools`/`--no-builtin-tools` for constrained runs.
- Do not rely on renderer-selected buttons as authority; app-core recomputes allowed launch profile.
- Preserve current Den conductor workflow; sandboxed sessions are operator-controlled sessions, not automatic task execution.

Follow-up hardening:

- Dedicated Pi extension/resource profile per sandbox run.
- Read-only tool profile for review/analysis, coding profile for implementation, and no-tool profile for planning.
- Explicit allow/deny metadata in `OperatorSession.Capabilities.Constraints`.

## Bounded launch prototype path

The practical prototype is to wrap the existing `pid` behavior behind a typed Den Desktop launch profile rather than exposing arbitrary shell.

### Launch profile DTO sketch

```json
{
  "project_id": "den-mcp",
  "task_id": 910,
  "cwd": "/home/patch/dev/den-mcp",
  "title": "Pi sandbox — task 910",
  "sandbox": {
    "kind": "docker_compose_pi",
    "compose_file": "/home/patch/dev/linux/pi-docker/compose.yaml",
    "service": "sandbox",
    "dev_dir": "/home/patch/dev",
    "container_workdir": "/home/pi/dev/den-mcp",
    "session_prefix": "den-pi",
    "network_profile": "unrestricted",
    "pi_config": "host_bind_rw",
    "credential_mounts": ["gitconfig:ro", "ssh:ro", "gh:ro"]
  },
  "pi": {
    "command": "pi",
    "mode": "interactive_cli",
    "args": ["--tools", "read,bash,edit,write"],
    "model": null,
    "session_dir": null
  }
}
```

### Host/container command sequence

This is intentionally a fixed command template with validated arguments, not an operator-supplied shell string:

```bash
# 1. Ensure sandbox container exists and keeps running.
docker compose -f /home/patch/dev/linux/pi-docker/compose.yaml up -d sandbox

# 2. Create a deterministic tmux session inside the container if missing.
docker compose -f /home/patch/dev/linux/pi-docker/compose.yaml exec -T \
  -e TERM=xterm-256color \
  --workdir /home/pi/dev/den-mcp \
  sandbox \
  tmux new-session -d -s den-pi-den-mcp-task-910-<hash> -c /home/pi/dev/den-mcp \
  pi --tools read,bash,edit,write

# 3. Attach/observe through the existing tmux-backed OperatorSession controls.
```

The app-core implementation should use `ProcessStartInfo.ArgumentList` or an equivalent argument-vector runner. It must validate that:

- `compose_file` is one of configured sandbox profiles.
- `cwd` is inside configured `dev_dir` and maps to the expected container path.
- `service` is an allow-listed service name.
- `pi.command` is an allow-listed executable token (`pi` initially).
- `pi.args` are assembled from an allow-listed launch-policy model, not accepted as raw user input.
- `tmux` session names are generated from bounded project/task/workspace inputs plus a hash.

### OperatorSession mapping

Recommended session fields:

- `Kind = agent`
- `Backend = tmux` initially, with future `Backend = process` or `external` if a Docker-specific backend appears.
- `CurrentCommand = "pi sandbox"`
- `AgentIdentity = "pi"`
- `Role = "sandboxed-agent"` or selected role.
- `Cwd = host cwd`, with container cwd stored in constraints.
- `Capabilities`:
  - `can_attach`, `can_detach`, `can_send_input`, `can_resize`, `can_terminate`, `can_kill`, `can_reconnect`, `can_read_activity`, `can_stream_terminal`: true when the container/tmux lease is healthy.
  - `requires_confirmation`: true for terminate/kill.
  - `constraints`: JSON including sandbox kind, compose profile id, image version, dev mount, credential mounts, network profile, Pi launch mode, tool profile, and raw stream scope.

Den snapshots/events should expose only backend-neutral summaries plus `sandbox_kind`/`persistence_kind`/`ownership_kind` metadata. Docker container ids, exact local credential paths, and raw terminal output should stay local-only.

## Integration with existing Den Desktop pieces

- **OperatorSession (#907):** sandboxed Pi sessions fit the existing local-authoritative session model. The sandbox launch profile belongs in app-core capability/constraints, not React state.
- **tmux persistence (#909):** the first launch path should reuse tmux-backed create/attach/detach/terminate semantics, with an additional sandbox-aware launch step before tmux session registration.
- **Authority model (#915):** the app-agent can start with trusted observe/suggest behavior. Sandboxed Pi sessions should be explicit action tools (`start_session`) with app-core policy checks and audit records.
- **Bridge architecture (#903/#981):** expose typed launch/stop/read operations only. No renderer access to Docker commands, shell strings, sidecar token, or generic bridge dispatch.
- **Terminal protocol (#945):** raw terminal bytes remain local bridge events; Den receives structured snapshots/events and bounded activity summaries.

## Prototype feasibility and next implementation slice

A full implementation was not attempted in #910 because wiring Docker lifecycle, launch profiles, app-core DTOs, tests, and UI would be a separate implementation slice. The bounded launch path above is practical because it reuses the existing `pi-docker` and `pid` behavior, but it needs first-class typed app-core wrappers before it is safe to expose in Den Desktop.

Suggested follow-up implementation:

1. Add a `SandboxedPiLaunchProfile` settings model with allow-listed compose profile(s).
2. Add an app-core `SandboxedPiSessionLauncher` with a process-runner seam and argument-vector tests.
3. Add a bridge command such as `den_desktop.agent.start_sandboxed_pi_session` that accepts project/task/cwd/title/tool-profile, not raw command strings.
4. On success, register a tmux-backed `OperatorSession` with `kind=agent` and sandbox constraints.
5. Add tests for path containment, rejected arbitrary args, deterministic session names, no raw terminal publication, and Den event/snapshot metadata.
6. Only then add a renderer button behind capability state.

## Open risks

- Direct host `~/.pi` bind mount gives the sandbox write access to Pi auth/settings/session state; safer per-run auth dirs need more login/token work.
- Read-only SSH/GH mounts still expose secrets to a compromised agent process; SSH-agent or scoped token design would be safer.
- Docker network is unrestricted in the experiment; network profiles are needed before claiming meaningful sandboxing.
- Passwordless sudo inside the container is convenient but weakens isolation inside the container boundary.
- Docker itself is not a perfect security boundary for malicious code; this design is intended for trusted internal coding agents and accidental-damage reduction, not hostile code execution.

## Recommendation

Proceed with a follow-up implementation slice for the typed Docker/tmux Pi launch wrapper when Den Desktop is ready to expose agent session launching. Keep #910 as a research/design baseline and do not change the normal Den conductor workflow yet.
