# Den-owned Pi Host Security Posture

This document describes the first Den-owned Pi session host model delivered under task #1185. It is operator-facing guidance for running sandboxed Pi sessions through Den.

## Ownership model

- **Den server is the default execution host/control plane.** Operator clients, including Hermes, request lifecycle actions through Den server APIs. They should not run host shell commands, own tmux sessions, or directly control Docker.
- **The first implementation runs on the Den server host.** Future remote worker hosts are possible, but they are out of scope for this initial model.
- **Den records lifecycle and observability state.** Pi session records include task/run ownership, host id, tmux/container identifiers, launch profile/argv metadata, state timestamps, last activity, bounded output tail, and attention status.

## What Docker provides — and what it does not

Pi runs inside Docker to provide containment, repeatability, and operational friction against accidental host changes. This is **not an airtight security sandbox**.

Assume a Pi session may read or modify anything mounted into the container. A malicious or compromised process inside the container may also attempt container breakout or abuse network access. Do not treat this model as a boundary for hostile code.

Use this model for trusted agent work where the goal is controlled process ownership, reproducible environment setup, and clearer operator visibility — not for executing untrusted workloads.

## Filesystem access

The initial profile intentionally preserves broad development access:

- Container `/home/pi/dev` maps to configured `DEV_DIR` with **read-write** access.
- Pi state maps to `PI_STATE_DIR` / per-session state roots with **read-write** access.
- Cache volumes are writable and may be session-scoped to reduce collisions.
- The first version does **not** enforce per-repository file restrictions.

Operational implication: a Pi session can edit any project reachable through the mounted dev directory. Continue using git review, Den task state, and branch discipline as the primary guardrails.

## Credential mounts

Git, SSH, and GitHub CLI credentials may be mounted read-only when configured. Read-only is a write-protection measure, not a secrecy measure.

- Mounted credentials are still readable by processes inside the container.
- Only mount credentials needed for the intended work.
- Prefer fallback/empty credential mounts when a session does not require repository/network authentication.
- Treat logs, output tails, and task notes carefully; do not paste secrets into terminal output or Den messages.

## Network and OAuth callback ports

The launch profile keeps callback ports bound to host loopback (`127.0.0.1`) by default. This avoids exposing callback listeners on external interfaces, but it does not prevent local host processes from connecting.

For multiple concurrent sessions:

- Use unique Compose project names/session ids.
- Use per-session `PI_STATE_DIR` and cache volume names.
- Allocate unique host callback ports per active session.
- The launch profile renderer validates per-profile duplicate callback ports, but the lifecycle host/operator must avoid collisions across already-running sessions.

If OAuth or provider callback behavior changes, verify that loopback binding and callback URI settings still match provider requirements.

## Setup/configuration notes

The Den server configuration under `DenMcp:PiSessionHost` / the Pi launch profile should make these points explicit:

- `DEV_DIR`: host dev root mounted as `/home/pi/dev` read-write.
- `PI_STATE_DIR` or state root: host/session state mounted read-write.
- Image name/version and Compose file/service references.
- Optional credential paths for git config, SSH, and GH config.
- Callback container ports and per-session host port assignments.
- Host id plus `tmux` and `docker` executable paths used by the lifecycle host.

Operators should validate these paths on the Den server host before enabling launch APIs in a shared environment.

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
