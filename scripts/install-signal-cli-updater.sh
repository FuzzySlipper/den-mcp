#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=/data/dev/den-mcp

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
  fi
}

main() {
  require_root

  install -m 0644 "$LIVE_ROOT/repo/deploy/signal-cli-update.service" /etc/systemd/system/signal-cli-update.service
  install -m 0644 "$LIVE_ROOT/repo/deploy/signal-cli-update.timer" /etc/systemd/system/signal-cli-update.timer

  systemctl daemon-reload
  systemctl enable --now signal-cli-update.timer

  echo "Installed signal-cli-update.service and signal-cli-update.timer"
  echo
  systemctl --no-pager --full status signal-cli-update.timer --lines=20 || true
  echo
  systemctl list-timers signal-cli-update.timer --all --no-pager || true
}

main "$@"
