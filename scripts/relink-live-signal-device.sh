#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=/data/dev/den-mcp
SIGNAL_BIN="$LIVE_ROOT/server/.local/bin/signal-cli"
SIGNAL_SHARE_PARENT="$LIVE_ROOT/server/.local/share"
SIGNAL_SHARE="$SIGNAL_SHARE_PARENT/signal-cli"
LINK_URI_FILE="$LIVE_ROOT/repo/live-signal-link-uri.txt"
LOG_FILE="$LIVE_ROOT/repo/live-signal-link.log"

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

main() {
  require_root

  echo "Stopping signal-cli-den.service ..."
  systemctl stop signal-cli-den.service 2>/dev/null || true

  echo "Resetting live signal-cli data dir ..."
  rm -rf "$SIGNAL_SHARE"
  mkdir -p "$SIGNAL_SHARE_PARENT"
  chown -R patch:patch "$LIVE_ROOT/server/.local"
  chmod 700 "$SIGNAL_SHARE_PARENT"

  rm -f "$LINK_URI_FILE" "$LOG_FILE"

  echo "Starting link flow as patch ..."
  echo "The generated URI will also be written to: $LINK_URI_FILE"
  echo

  set +e
  as_patch "$SIGNAL_BIN" link -n den-mcp-server | tee "$LINK_URI_FILE" "$LOG_FILE"
  status=$?
  set -e

  echo
  echo "link_exit_status=$status"

  if [[ $status -ne 0 ]]; then
    echo "Link flow did not complete successfully." >&2
    exit $status
  fi

  echo
  echo "Keeping Signal data owned by patch for the signal-cli-den service ..."
  chown -R patch:patch "$SIGNAL_SHARE_PARENT"

  echo
  echo "Linked accounts now visible to patch:"
  as_patch "$SIGNAL_BIN" listAccounts || true

  echo
  echo "Restarting services ..."
  systemctl restart signal-cli-den.service
  systemctl restart den-mcp.service
  systemctl --no-pager --full status signal-cli-den.service den-mcp.service --lines=20 || true
}

main "$@"
