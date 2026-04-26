#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-}"
SSH_TARGET="${SSH_TARGET:-patch@192.168.1.10}"
SERVICE_NAME="${SERVICE_NAME:-den-mcp.service}"
REMOTE_SERVER_ROOT="${REMOTE_SERVER_ROOT:-/data/dev/den-mcp/server}"
REMOTE_STAGE_DIR="${REMOTE_STAGE_DIR:-/tmp/den-mcp-live-publish}"
SKIP_RESTART=0
TEMP_PUBLISH_DIR_CREATED=0

usage() {
  cat <<'EOF'
Usage: scripts/deploy-live-server.sh [options]

Build and publish DenMcp.Server into the live server tree while preserving
runtime state, then restart the live systemd service on den-srv.

Run this as your normal user, not with local sudo. The script uses your SSH
config/keys locally and remote sudo on the server.

Options:
  --skip-restart     Publish and sync only; do not restart services
  -h, --help         Show this help

Environment overrides:
  PUBLISH_DIR, SSH_TARGET, SERVICE_NAME, REMOTE_SERVER_ROOT, REMOTE_STAGE_DIR
EOF
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --skip-restart)
        SKIP_RESTART=1
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        echo "Unknown argument: $1" >&2
        usage >&2
        exit 1
        ;;
    esac
    shift
  done
}

require_non_root() {
  if [[ ${EUID:-$(id -u)} -eq 0 ]]; then
    echo "Run this script as your normal user, not with sudo." >&2
    echo "It uses local SSH auth and performs sudo only on the remote server." >&2
    exit 1
  fi
}

preflight_workspace() {
  local client_app="$REPO_ROOT/src/DenMcp.Server/ClientApp"
  local first_offender=""

  if [[ -d "$client_app/node_modules" ]]; then
    first_offender="$(find "$client_app/node_modules" -mindepth 1 \( -user root -o -group root \) -print -quit 2>/dev/null || true)"
    if [[ -n "$first_offender" ]]; then
      cat >&2 <<EOF
Deploy preflight failed: frontend dependencies under ClientApp/node_modules are root-owned.

Example offending path:
  $first_offender

This usually happens after an earlier local sudo build/publish. The frontend build writes
incremental artifacts into ClientApp/node_modules/.tmp, so ownership drift there breaks
dotnet publish.

One-time fix:
  sudo chown -R $(id -un):$(id -gn) "$client_app/node_modules"

After that, rerun:
  ./deploy.sh
EOF
      exit 1
    fi
  fi
}

initialize_publish_dir() {
  if [[ -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
    return
  fi

  PUBLISH_DIR="$(mktemp -d /tmp/den-mcp-live-publish.XXXXXX)"
  TEMP_PUBLISH_DIR_CREATED=1
}

cleanup() {
  if [[ "$TEMP_PUBLISH_DIR_CREATED" -eq 1 && -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
  fi
}

publish_server() {
  echo "Publishing DenMcp.Server ..."
  dotnet publish "$REPO_ROOT/src/DenMcp.Server/DenMcp.Server.csproj" \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUBLISH_DIR/"
}

sync_server_tree() {
  echo "Uploading publish output to $SSH_TARGET:$REMOTE_STAGE_DIR ..."
  ssh "$SSH_TARGET" "rm -rf '$REMOTE_STAGE_DIR' && mkdir -p '$REMOTE_STAGE_DIR'"
  rsync -a --delete "$PUBLISH_DIR/" "$SSH_TARGET:$REMOTE_STAGE_DIR/"

  echo "Applying publish output on $SSH_TARGET:$REMOTE_SERVER_ROOT ..."
  ssh -t "$SSH_TARGET" "
    sudo mkdir -p '$REMOTE_SERVER_ROOT' &&
    sudo rsync -a --delete --chown=den-mcp:den-mcp \
      --exclude '.den-mcp/' \
      --exclude 'env/' \
      --exclude '.local/' \
      --exclude '.net/' \
      --exclude 'appsettings.json' \
      --exclude 'appsettings.Development.json' \
      '$REMOTE_STAGE_DIR/' '$REMOTE_SERVER_ROOT/' &&
    rm -rf '$REMOTE_STAGE_DIR'
  "
}

restart_remote() {
  if [[ "$SKIP_RESTART" -eq 1 ]]; then
    echo "Skipping remote service restart."
    return
  fi

  echo "Restarting live service on $SSH_TARGET ..."
  ssh -t "$SSH_TARGET" "sudo systemctl restart $SERVICE_NAME && sudo systemctl --no-pager --full status $SERVICE_NAME --lines=20"
}

main() {
  require_non_root
  parse_args "$@"
  preflight_workspace
  initialize_publish_dir
  trap cleanup EXIT
  publish_server
  sync_server_tree
  restart_remote
  echo "Deploy complete."
}

main "$@"
