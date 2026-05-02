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
- Preserve current Den orchestrator workflow; sandboxed sessions are operator-controlled sessions, not automatic task execution.

Follow-up hardening:

- Dedicated Pi extension/resource profile per sandbox run.
- Read-only tool profile for review/analysis, coding profile for implementation, and no-tool profile for planning.
- Explicit allow/deny metadata in `OperatorSession.Capabilities.Constraints`.

## Bounded launch prototype path

The practical prototype is to wrap the existing `pid` behavior behind a typed Den Desktop launch profile rather than exposing arbitrary shell.

### Launch profile DTO — hardened design (#1073)

The original #910 DTO sketch defaulted `pi_config` to `host_bind_rw`. Task #1073 hardened this:

- **R910-1 resolved:** `pi_config_strategy` now defaults to `dedicated_per_run`. A fresh per-run Pi config directory is the default sandbox auth strategy. `host_bind_rw` is only permitted with explicit capability warnings and debt metadata explaining why the non-preferred strategy was selected.
- **R910-2 resolved:** OAuth callback ports are now represented by a typed `SandboxedPiOAuthPortConfig` with an allow-listed strategy (`allow_listed`, `manual_fallback`, or `disabled`), specific port numbers, and a mandatory `127.0.0.1` loopback-only bind address.
- **Tool profiles** are allow-listed (`coding`, `read_only`, `no_tools`), not arbitrary user-supplied args.
- **Credential mounts** are allow-listed (`gitconfig`, `ssh`, `gh`), all read-only in v1.
- No renderer shell strings or generic dispatch.

Hardened DTO example (app-core `SandboxedPiLaunchProfile` record):

```json
{
  "profile_id": "sandbox-pi:den-mcp:abc123",
  "project_id": "den-mcp",
  "task_id": 910,
  "workspace_id": null,
  "title": "Pi sandbox — task 910",
  "sandbox_kind": "docker_compose_pi",
  "compose_file": "/home/patch/dev/linux/pi-docker/compose.yaml",
  "service": "sandbox",
  "dev_dir": "/home/patch/dev",
  "container_workdir": "/home/pi/dev/den-mcp",
  "session_prefix": "den-pi",
  "pi_config_strategy": "dedicated_per_run",
  "pi_config_dir": "/tmp/den-sandbox-pi/run-abc",
  "capability_warnings": ["Network is unrestricted — sandbox has full network access."],
  "debt_metadata": null,
  "oauth_port_config": {
    "strategy": "allow_listed",
    "allowed_ports": [3000, 3001, 8080],
    "bind_address": "127.0.0.1",
    "manual_fallback_instructions": null
  },
  "credential_mounts": ["gitconfig", "ssh", "gh"],
  "pi_launch_mode": "interactive_cli",
  "tool_profile": "coding",
  "model": null,
  "provider": null,
  "network_profile": "unrestricted"
}
```

#### Host bind-rw DTO (debt variant)

When `host_bind_rw` is selected, capability warnings and debt metadata are required:

```json
{
  "pi_config_strategy": "host_bind_rw",
  "pi_config_dir": null,
  "capability_warnings": [
    "Host ~/.pi exposed read-write to sandbox",
    "Network is unrestricted — sandbox has full network access."
  ],
  "debt_metadata": {
    "reason": "Per-run auth not yet implemented",
    "tracking_task_id": 1073,
    "accepted_at": "2026-05-01T00:00:00Z"
  }
}
```

### App-core implementation (#1073)

The typed launch profile is implemented in `src/DenMcp.Desktop.Sidecar/AppCore/Sandbox/`:

- **`SandboxedPiLaunchProfile`** — Immutable record with all profile fields, JSON-serializable. Contains constants for strategies, profiles, and defaults.
- **`SandboxedPiLaunchProfileBuilder`** — Builder with validation. Enforces allow-lists for compose files, services, tool profiles, credential mounts, and network profiles. Enforces R910-1 constraints (debt metadata + warnings for `host_bind_rw`) and R910-2 constraints (loopback-only OAuth ports, valid port ranges, at least one port for `allow_listed` strategy).
- **`SandboxedPiCommandBuilder`** — Produces argument vectors (not shell strings) for Docker Compose up, tmux exec, OAuth port publishes, and Pi config volume mounts. All arguments are derived from the validated profile. Includes `BuildSessionConstraints` for `OperatorSession.Capabilities.Constraints` JSON.

Tests are in `tests/DenMcp.Desktop.Sidecar.Tests/SandboxedPiLaunchProfileTests.cs` covering 52 test cases.

### Host/container command sequence

The `SandboxedPiCommandBuilder` produces argument vectors from the validated `SandboxedPiLaunchProfile`. These are designed for `ProcessStartInfo.ArgumentList` usage — each entry is a separate argument, not a shell-escaped string.

```text
# 1. Compose up (from BuildComposeUpArgs)
["compose", "-f", "<compose_file>", "up", "-d", "sandbox"]

# 2. OAuth port publishes (from BuildOAuthPortArgs, only for allow_listed strategy)
["--publish", "127.0.0.1:3000:3000", "--publish", "127.0.0.1:3001:3001"]

# 3. Pi config volume (from BuildPiConfigVolumeArgs)
["-v", "/tmp/den-sandbox-pi/run-abc:/home/pi/.pi"]

# 4. Tmux exec + Pi launch (from BuildTmuxExecArgs)
["compose", "-f", "<compose_file>", "exec", "-T",
 "-e", "TERM=xterm-256color",
 "--workdir", "/home/pi/dev/den-mcp",
 "sandbox",
 "tmux", "new-session", "-d", "-s", "<session_name>", "-c", "/home/pi/dev/den-mcp",
 "pi", "--tools", "read,bash,edit,write"]
```

Validation is enforced by the builder:

- `compose_file` must be in the configured allow-list.
- `service` is allow-listed (`sandbox` only in v1).
- Tool args come from allow-listed `ToolProfiles` constants, not user input.
- `tmux` session names are generated from bounded project/task/workspace inputs plus a hash.
- No renderer-supplied shell strings appear anywhere in the argument vectors.

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

### Implementation status (#1073)

Items completed:

1. ✅ `SandboxedPiLaunchProfile` record with allow-listed compose profile(s), typed Pi config strategy, typed OAuth port config, tool profiles, credential mounts, and network profiles.
2. ✅ `SandboxedPiLaunchProfileBuilder` with full validation: compose file allow-list, service allow-list, tool profile allow-list, credential mount allow-list, Pi config strategy constraints (R910-1), OAuth port loopback/validation (R910-2), and network profile validation.
3. ✅ `SandboxedPiCommandBuilder` with argument-vector methods: `BuildComposeUpArgs`, `BuildTmuxExecArgs`, `BuildPiConfigVolumeArgs`, `BuildOAuthPortArgs`, `BuildTmuxSessionName`, `BuildSessionConstraints`.
4. ✅ 52 test cases covering: builder validation, allow-list enforcement, R910-1 Pi config strategy (dedicated_per_run default, host_bind_rw requires warnings+debt), R910-2 OAuth ports (loopback-only, valid ranges, strategy variants), tool profiles, credential mounts, serialization roundtrip, command builder output, and session constraints JSON.

Remaining follow-up:

5. Wire `SandboxedPiSessionLauncher` with a process-runner seam (actual Docker process execution).
6. Add a bridge command such as `den_desktop.agent.start_sandboxed_pi_session` that accepts project/task/cwd/title/tool-profile, not raw command strings.
7. Register a tmux-backed `OperatorSession` with `kind=agent` and sandbox constraints on successful launch.
8. Add renderer button behind capability state.

## Open risks

- Direct host `~/.pi` bind mount gives the sandbox write access to Pi auth/settings/session state; safer per-run auth dirs need more login/token work.
- Read-only SSH/GH mounts still expose secrets to a compromised agent process; SSH-agent or scoped token design would be safer.
- Docker network is unrestricted in the experiment; network profiles are needed before claiming meaningful sandboxing.
- Passwordless sudo inside the container is convenient but weakens isolation inside the container boundary.
- Docker itself is not a perfect security boundary for malicious code; this design is intended for trusted internal coding agents and accidental-damage reduction, not hostile code execution.

## Recommendation

Proceed with a follow-up implementation slice for the typed Docker/tmux Pi launch wrapper when Den Desktop is ready to expose agent session launching. Keep #910 as a research/design baseline and do not change the normal Den orchestrator workflow yet.
