#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=/data/dev/den-mcp
ENV_FILE="$LIVE_ROOT/server/env/server.env"
SIGNAL_BIN="$LIVE_ROOT/server/.local/bin/signal-cli"
SIGNAL_SHARE="$LIVE_ROOT/server/.local/share/signal-cli"
SIGNAL_DATA="$SIGNAL_SHARE/data"
SERVICE_HOME="$LIVE_ROOT/server"
LOG_FILE="$LIVE_ROOT/repo/debug-signal-cli.log"

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
  fi
}

extract_var() {
  local key=$1
  sed -n "s/^${key}=//p" "$ENV_FILE" | tail -n 1
}

as_den_mcp() {
  sudo -u den-mcp -- env HOME="$SERVICE_HOME" "$@"
}

as_patch() {
  sudo -u patch -- env HOME="$SERVICE_HOME" "$@"
}

timeout_as_den_mcp() {
  sudo -u den-mcp -- env HOME="$SERVICE_HOME" timeout 10s "$@"
}

timeout_as_patch() {
  sudo -u patch -- env HOME="$SERVICE_HOME" timeout 10s "$@"
}

main() {
  require_root

  local account port host
  account=$(extract_var 'DenMcp__Signal__Account')
  host=$(extract_var 'DenMcp__Signal__HttpHost')
  port=$(extract_var 'DenMcp__Signal__HttpPort')

  : "${host:=127.0.0.1}"
  : "${port:=12081}"

  {
    echo "== binary =="
    ls -l "$SIGNAL_BIN"
    "$SIGNAL_BIN" --version

    echo
    echo "== signal data tree =="
    if [[ -d "$SIGNAL_SHARE" ]]; then
      find "$SIGNAL_SHARE" -maxdepth 3 -mindepth 1 -printf "%M %u %g %p\n" | sed -n '1,200p'
    else
      echo "missing: $SIGNAL_SHARE"
    fi

    echo
    echo "== accounts.json =="
    if [[ -f "$SIGNAL_DATA/accounts.json" ]]; then
      sed -n '1,220p' "$SIGNAL_DATA/accounts.json"
    else
      echo "missing: $SIGNAL_DATA/accounts.json"
    fi

    echo
    echo "== account records =="
    if [[ -d "$SIGNAL_DATA" ]]; then
      find "$SIGNAL_DATA" -maxdepth 2 -mindepth 1 -printf "%M %u %g %p\n" | sed -n '1,200p'
      if [[ -f "$SIGNAL_DATA/997053" ]]; then
        echo
        echo "-- data/997053 --"
        sed -n '1,220p' "$SIGNAL_DATA/997053"
      fi
    fi

    echo
    echo "== listAccounts as patch =="
    set +e
    as_patch "$SIGNAL_BIN" listAccounts
    patch_status=$?
    set -e
    echo "exit_status=$patch_status"

    echo
    echo "== listAccounts as den-mcp =="
    set +e
    as_den_mcp "$SIGNAL_BIN" listAccounts
    list_status=$?
    set -e
    echo "exit_status=$list_status"

    echo
    echo "== daemon dry run as patch =="
    set +e
    timeout_as_patch \
      "$SIGNAL_BIN" \
      -v \
      -a "$account" \
      daemon --http="${host}:${port}"
    patch_daemon_status=$?
    set -e
    echo "exit_status=$patch_daemon_status"
    if [[ $patch_daemon_status -eq 124 ]]; then
      echo "daemon stayed up for 10s; startup path looks healthy"
    fi

    echo
    echo "== daemon dry run as den-mcp =="
    set +e
    timeout_as_den_mcp \
      "$SIGNAL_BIN" \
      -v \
      -a "$account" \
      daemon --http="${host}:${port}"
    status=$?
    set -e
    echo "exit_status=$status"
    if [[ $status -eq 124 ]]; then
      echo "daemon stayed up for 10s; startup path looks healthy"
    fi
  } 2>&1 | tee "$LOG_FILE"

  echo
  echo "Debug log written to $LOG_FILE"
}

main "$@"
