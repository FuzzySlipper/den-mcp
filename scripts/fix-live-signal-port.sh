#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=/data/dev/den-mcp
ENV_FILE="$LIVE_ROOT/server/env/server.env"
NEW_PORT=12081

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
  fi
}

set_signal_port() {
  if [[ ! -f "$ENV_FILE" ]]; then
    echo "Expected env file at $ENV_FILE" >&2
    exit 1
  fi

  if grep -q '^DenMcp__Signal__HttpPort=' "$ENV_FILE"; then
    sed -i "s/^DenMcp__Signal__HttpPort=.*/DenMcp__Signal__HttpPort=$NEW_PORT/" "$ENV_FILE"
  else
    printf '\nDenMcp__Signal__HttpPort=%s\n' "$NEW_PORT" >> "$ENV_FILE"
  fi
}

wait_for_http() {
  local url=$1
  local label=$2
  local attempts=${3:-30}

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

  echo "Updating Signal daemon port to $NEW_PORT in $ENV_FILE ..."
  set_signal_port

  echo "Restarting services..."
  systemctl restart signal-cli-den.service
  systemctl restart den-mcp.service

  echo
  systemctl --no-pager --full status signal-cli-den.service den-mcp.service --lines=20 || true

  echo
  wait_for_http "http://127.0.0.1:$NEW_PORT/api/v1/check" "Signal daemon" 45

  wait_for_http "http://127.0.0.1:5199/health" "den-mcp" 45
  curl -fsS "http://127.0.0.1:5199/health"
  echo
}

main "$@"
