#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=/data/dev/den-mcp
UNIT_SOURCE="$LIVE_ROOT/repo/deploy/signal-cli-den.service"
UNIT_TARGET=/etc/systemd/system/signal-cli-den.service
ENV_FILE="$LIVE_ROOT/server/env/server.env"

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
  fi
}

as_den_mcp() {
  sudo -u den-mcp -- env HOME="$LIVE_ROOT/server" "$@"
}

as_patch() {
  sudo -u patch -- env HOME="$LIVE_ROOT/server" "$@"
}

wait_for_http() {
  local url=$1
  local label=$2
  local attempts=${3:-45}

  for ((i=1; i<=attempts; i++)); do
    if curl -fsS "$url" >/dev/null 2>&1; then
      echo "$label ready."
      return 0
    fi
    sleep 1
  done

  echo "$label did not become ready in time." >&2
  return 1
}

main() {
  require_root

  echo "Installing updated signal-cli-den.service ..."
  install -m 0644 "$UNIT_SOURCE" "$UNIT_TARGET"

  echo "Reloading systemd ..."
  systemctl daemon-reload

  echo "Checking linked Signal accounts as patch ..."
  as_patch "$LIVE_ROOT/server/.local/bin/signal-cli" listAccounts || true

  echo
  echo "Restarting signal-cli-den and den-mcp ..."
  systemctl restart signal-cli-den.service
  systemctl restart den-mcp.service

  echo
  systemctl --no-pager --full status signal-cli-den.service den-mcp.service --lines=25 || true

  echo
  if grep -q '^DenMcp__Signal__HttpPort=' "$ENV_FILE"; then
    SIGNAL_PORT=$(sed -n 's/^DenMcp__Signal__HttpPort=//p' "$ENV_FILE" | tail -n 1)
  else
    SIGNAL_PORT=12081
  fi

  wait_for_http "http://127.0.0.1:${SIGNAL_PORT}/api/v1/check" "Signal daemon" 45

  wait_for_http 'http://127.0.0.1:5199/health' 'den-mcp' 45
  curl -fsS 'http://127.0.0.1:5199/health'
  echo
}

main "$@"
