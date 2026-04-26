#!/usr/bin/env bash
set -euo pipefail

NEW_ROOT=/data/dev/den-mcp-new
FINAL_ROOT=/data/dev/den-mcp
OLD_ROOT=/data/dev/den-mcp
SERVER_USER=den-mcp
SERVER_GROUP=den-mcp

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
  fi
}

copy_live_database() {
  local old_db="$OLD_ROOT/server/.den-mcp/den.db"
  local new_state_dir="$NEW_ROOT/server/.den-mcp"
  local new_db="$new_state_dir/den.db"

  mkdir -p "$new_state_dir"
  rm -f "$new_db" "$new_db-shm" "$new_db-wal"

  if [[ ! -f "$old_db" ]]; then
    echo "No live den.db found at $old_db; leaving staged database as-is."
    return
  fi

  if command -v sqlite3 >/dev/null 2>&1; then
    echo "Refreshing staged database from live install via sqlite backup..."
    sqlite3 "$old_db" ".backup '$new_db'"
  else
    echo "sqlite3 not found; copying live .den-mcp directory directly..."
    rm -rf "$new_state_dir"
    mkdir -p "$new_state_dir"
    cp -a "$OLD_ROOT/server/.den-mcp/." "$new_state_dir/"
  fi
}

install_units() {
  install -m 0644 "$FINAL_ROOT/repo/deploy/den-mcp.service" /etc/systemd/system/den-mcp.service
}

fix_permissions() {
  chown -R patch:patch "$FINAL_ROOT/repo"
  chown -R "$SERVER_USER:$SERVER_GROUP" "$FINAL_ROOT/server"
  chmod 755 "$FINAL_ROOT" "$FINAL_ROOT/server" "$FINAL_ROOT/repo"
  chmod 700 "$FINAL_ROOT/server/.den-mcp" "$FINAL_ROOT/server/env"
  chmod 600 "$FINAL_ROOT/server/env/server.env"
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

  if [[ ! -d "$NEW_ROOT/server" || ! -d "$NEW_ROOT/repo" ]]; then
    echo "Expected staged tree at $NEW_ROOT with server/ and repo/." >&2
    exit 1
  fi

  echo "Stopping existing service..."
  systemctl stop den-mcp.service 2>/dev/null || true

  copy_live_database

  echo "Replacing live tree..."
  rm -rf "$FINAL_ROOT"
  mv "$NEW_ROOT" "$FINAL_ROOT"

  install_units
  fix_permissions

  echo "Reloading and starting service..."
  systemctl daemon-reload
  systemctl enable den-mcp.service >/dev/null
  systemctl restart den-mcp.service

  wait_for_http "http://127.0.0.1:5199/health" "den-mcp" 45

  echo "Service status:"
  systemctl --no-pager --full status den-mcp.service --lines=20 || true

  echo "Health check:"
  curl -fsS http://127.0.0.1:5199/health
  echo
}

main "$@"
