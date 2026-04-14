# Live Signal And Dispatch Ops Runbook

This is the operational reference for the live `den-mcp` + Signal integration on `den-srv`. It turns the one-off validation work into a repeatable smoke check and documents the ownership / path assumptions that matter when restarting, relinking, updating, or migrating the service.

## Quick Smoke Check

Run the full live smoke check as root:

```bash
sudo ./scripts/smoke-live-dispatch-signal.sh --wait-for-reaction 120
```

What it validates:

- `signal-cli-den.service` is active and running as `patch`
- `den-mcp.service` is active and running as `den-mcp`
- `http://127.0.0.1:<signal-port>/api/v1/check` is healthy
- `http://127.0.0.1:<den-port>/health` is healthy
- the configured Signal account is visible to the actual Signal service user (`patch`)
- direct JSON-RPC send through `signal-cli` succeeds to the configured recipient
- a message with explicit recipient metadata creates a Den dispatch
- the resulting Signal notification can be approved/rejected by reaction

The script defaults to:

- project: `den-mcp`
- message sender: `ops-smoke`
- message metadata: `{"type":"review_request","recipient":"claude-code"}`

That metadata choice is deliberate: it exercises the explicit handoff path from `AGENTS.md` instead of relying on fallback reviewer-role routing.

Useful variants:

```bash
sudo ./scripts/smoke-live-dispatch-signal.sh --skip-dispatch
sudo ./scripts/smoke-live-dispatch-signal.sh --skip-direct-send
sudo ./scripts/smoke-live-dispatch-signal.sh --target-agent codex --wait-for-reaction 60
```

If you only want dispatch approvals during bring-up, set `DenMcp__Signal__NotifyOnAgentStatus=false` before restarting the server. The default `true` setting also emits agent check-in / checkout and task-status activity (`in_progress`, `review`, `done`), which is useful once the workflow is trusted but can feel noisy during early deploy validation.

## Service Model

The live deployment is intentionally split across two Unix users:

| Component | systemd unit | Unix user | Why |
| --- | --- | --- | --- |
| Den server | `den-mcp.service` | `den-mcp` | Owns the SQLite DB, env file, and published server binary |
| Signal daemon | `signal-cli-den.service` | `patch` | Owns linked Signal device state under `.local/share/signal-cli` |

This split is important. If `signal-cli` state gets re-owned by `den-mcp`, the daemon may still start but `listAccounts` / sends / relink flows can break in confusing ways. If the whole `server/` tree gets re-owned by `patch`, the Den process loses the cleaner service boundary around `.den-mcp` and `env/`.

## Expected On-Disk State

Live root:

- repo: `/data/dev/den-mcp/repo`
- server: `/data/dev/den-mcp/server`

Expected ownership / permissions:

| Path | Expected owner | Notes |
| --- | --- | --- |
| `/data/dev/den-mcp/repo` | `patch:patch` | Working tree, deploy scripts, runbooks |
| `/data/dev/den-mcp/server` | `den-mcp:den-mcp` | Published server payload and service HOME |
| `/data/dev/den-mcp/server/.den-mcp` | `den-mcp:den-mcp` | SQLite DB directory; keep `700` |
| `/data/dev/den-mcp/server/env` | `den-mcp:den-mcp` | Environment directory; keep `700` |
| `/data/dev/den-mcp/server/env/server.env` | `den-mcp:den-mcp` | Runtime config; keep `600` |
| `/data/dev/den-mcp/server/.local` | `patch:patch` | Signal runtime subtree |
| `/data/dev/den-mcp/server/.local/bin/signal-cli` | `patch:patch` | Symlink to the active Signal binary |
| `/data/dev/den-mcp/server/.local/opt/signal-cli-*` | `patch:patch` | Installed Signal versions |
| `/data/dev/den-mcp/server/.local/share/signal-cli` | `patch:patch` | Linked device state; keep `700` |

Important files and settings:

- Den env file: `server/env/server.env`
- Den listen URL: `DenMcp__ListenUrl`
- Den database path: `DenMcp__DatabasePath`
- Signal account: `DenMcp__Signal__Account`
- Signal recipient: `DenMcp__Signal__Recipient` (or legacy `RecipientNumber`)
- Signal daemon host/port: `DenMcp__Signal__HttpHost` / `DenMcp__Signal__HttpPort`

Current expected Signal daemon port is `12081`. Earlier live cutovers used `8081`, so port drift is a real failure mode to check for.

## Restart And Recovery

### Service restart

Fast path:

```bash
sudo systemctl restart signal-cli-den.service
sudo systemctl restart den-mcp.service
sudo systemctl --no-pager --full status signal-cli-den.service den-mcp.service --lines=25
curl -fsS http://127.0.0.1:12081/api/v1/check
curl -fsS http://127.0.0.1:5199/health
```

If you also updated the checked-in unit file, use the helper instead:

```bash
sudo bash scripts/repair-live-signal-service.sh
```

That script:

- reinstalls `deploy/signal-cli-den.service`
- reloads systemd
- restarts both services
- verifies both HTTP health endpoints
- checks linked accounts as `patch`

### Relink flow

When the Signal device needs to be relinked:

```bash
sudo ./scripts/relink-live-signal-device.sh
```

What it does:

- stops `signal-cli-den.service`
- clears `server/.local/share/signal-cli`
- starts `signal-cli link` as `patch`
- writes the generated link URI to `live-signal-link-uri.txt`
- keeps `.local/share/signal-cli` owned by `patch`
- restarts both services

Use this when:

- `listAccounts` is empty for `patch`
- direct sends fail even though the daemon is reachable
- the linked mobile device was removed or rotated

### signal-cli updater

Dry run:

```bash
sudo ./scripts/update-signal-cli.sh --check-only
```

Apply:

```bash
sudo ./scripts/update-signal-cli.sh --apply
```

What `--apply` does:

- fetches the latest upstream `signal-cli` release
- installs it under `server/.local/opt/signal-cli-<version>`
- smoke-checks `listAccounts` against the linked account
- repoints `server/.local/bin/signal-cli`
- restarts `signal-cli-den.service`
- waits for `/api/v1/check`
- prunes older versions, keeping the newest two by default

To install the weekly updater timer:

```bash
sudo ./scripts/install-signal-cli-updater.sh
```

Related systemd units:

- `signal-cli-update.service`
- `signal-cli-update.timer`

### Port / config mismatches

Symptoms:

- `signal-cli-den.service` looks healthy but Den cannot send notifications
- `curl /api/v1/check` only works on `8081` while the repo and env expect `12081`
- server logs show connect failures to the Signal daemon

Check:

```bash
grep '^DenMcp__Signal__HttpPort=' /data/dev/den-mcp/server/env/server.env
systemctl cat signal-cli-den.service
curl -fsS http://127.0.0.1:12081/api/v1/check
curl -fsS http://127.0.0.1:8081/api/v1/check
```

Normalize to the expected live port with:

```bash
sudo bash scripts/fix-live-signal-port.sh
```

That updates `DenMcp__Signal__HttpPort`, restarts both services, and rechecks health.

## Troubleshooting Notes

If the smoke check fails at different stages:

- Fails before `/api/v1/check`: focus on `signal-cli-den.service`, `server/env/server.env`, and the linked account state under `server/.local/share/signal-cli`.
- Fails at `listAccounts` for `patch`: fix ownership under `server/.local` or relink the device.
- Direct JSON-RPC send fails but `/api/v1/check` passes: inspect the configured recipient/account and the daemon logs before changing Den.
- Dispatch creation fails after message post: inspect Den logs and verify the project message included explicit recipient metadata.
- Dispatch is created but Signal approval never lands: verify the notification arrived to the configured recipient and that the reaction is on the dispatch message, not the earlier direct-send smoke message.

Useful inspection commands:

```bash
sudo systemctl --no-pager --full status signal-cli-den.service den-mcp.service --lines=50
sudo journalctl -u signal-cli-den.service -u den-mcp.service -n 200 --no-pager
sudo -u patch -- env HOME=/data/dev/den-mcp/server /data/dev/den-mcp/server/.local/bin/signal-cli listAccounts
```

## Migration / Cleanup Checklist

When staging a new live tree or cleaning up the existing one, preserve these pieces:

- `server/.den-mcp/den.db`
- `server/env/server.env`
- `server/.local/share/signal-cli`
- `server/.local/bin/signal-cli` and any installed versions under `.local/opt`

The cutover helper at `deploy/activate-den-mcp-new.sh` is the source of truth for the expected ownership reset:

- repo back to `patch:patch`
- server back to `den-mcp:den-mcp`
- `.local` back to `patch:patch`
- `.den-mcp` and `env` locked down for `den-mcp`

If a migration finishes and the smoke-check script passes, the live runtime assumptions are intact.
