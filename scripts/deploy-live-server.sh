#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-}"
SSH_TARGET="${SSH_TARGET:-patch@192.168.1.10}"
SERVICE_NAME="${SERVICE_NAME:-den-mcp.service}"
REMOTE_SERVER_ROOT="${REMOTE_SERVER_ROOT:-/data/services/den-mcp/server}"
REMOTE_STAGE_DIR="${REMOTE_STAGE_DIR:-/tmp/den-mcp-live-publish}"
PI_DOCKER_SOURCE="${PI_DOCKER_SOURCE:-/home/patch/dev/linux/pi-docker}"
REMOTE_PI_DOCKER_DIR="${REMOTE_PI_DOCKER_DIR:-/data/services/den-mcp/pi-docker}"
REMOTE_PI_STATE_ROOT="${REMOTE_PI_STATE_ROOT:-/data/services/den-mcp/pi-sessions}"
REMOTE_PI_CREDENTIAL_FALLBACK_ROOT="${REMOTE_PI_CREDENTIAL_FALLBACK_ROOT:-/data/services/den-mcp/pi-credential-fallbacks}"
REMOTE_DEV_ROOT="${REMOTE_DEV_ROOT:-/data/dev}"
SKIP_RESTART=0
SKIP_PI_DOCKER_ASSETS=0
TEMP_PUBLISH_DIR_CREATED=0

usage() {
  cat <<'EOF'
Usage: scripts/deploy-live-server.sh [options]

Build and publish DenMcp.Server into the live server tree while preserving
runtime state, then restart the live systemd service on den-srv.

Run this as your normal user, not with local sudo. The script uses your SSH
config/keys locally and remote sudo on the server.

Options:
  --skip-restart            Publish and sync only; do not restart services
  --skip-pi-docker-assets   Do not deploy pi-docker compose assets/state roots
  -h, --help                Show this help

Environment overrides:
  PUBLISH_DIR, SSH_TARGET, SERVICE_NAME, REMOTE_SERVER_ROOT, REMOTE_STAGE_DIR,
  PI_DOCKER_SOURCE, REMOTE_PI_DOCKER_DIR, REMOTE_PI_STATE_ROOT,
  REMOTE_PI_CREDENTIAL_FALLBACK_ROOT, REMOTE_DEV_ROOT
EOF
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --skip-restart)
        SKIP_RESTART=1
        ;;
      --skip-pi-docker-assets)
        SKIP_PI_DOCKER_ASSETS=1
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

sync_pi_docker_assets() {
  if [[ "$SKIP_PI_DOCKER_ASSETS" -eq 1 ]]; then
    echo "Skipping pi-docker asset deployment."
    return
  fi

  if [[ ! -d "$PI_DOCKER_SOURCE" ]]; then
    cat >&2 <<EOF
pi-docker source not found: $PI_DOCKER_SOURCE

Set PI_DOCKER_SOURCE to the local pi-docker checkout, or pass
--skip-pi-docker-assets if this deploy intentionally should not update Den-owned
Pi session compose assets.
EOF
    exit 1
  fi

  if [[ ! -f "$PI_DOCKER_SOURCE/compose.yaml" ]]; then
    echo "pi-docker source is missing compose.yaml: $PI_DOCKER_SOURCE" >&2
    exit 1
  fi

  local remote_asset_stage="$REMOTE_STAGE_DIR/pi-docker"
  echo "Uploading pi-docker assets from $PI_DOCKER_SOURCE to $SSH_TARGET:$remote_asset_stage ..."
  ssh "$SSH_TARGET" "rm -rf '$remote_asset_stage' && mkdir -p '$remote_asset_stage'"
  rsync -a --delete --exclude '.env' "$PI_DOCKER_SOURCE/" "$SSH_TARGET:$remote_asset_stage/"

  echo "Installing pi-docker assets and service-accessible Pi session paths on $SSH_TARGET ..."
  ssh -t "$SSH_TARGET" "
    sudo install -d -o den-mcp -g den-mcp -m 0755 '$REMOTE_PI_DOCKER_DIR' &&
    sudo rsync -a --delete --chown=den-mcp:den-mcp --exclude '.env' \
      '$remote_asset_stage/' '$REMOTE_PI_DOCKER_DIR/' &&
    sudo rm -f '$REMOTE_PI_DOCKER_DIR/.env' &&
    sudo chmod -R u=rwX,go=rX '$REMOTE_PI_DOCKER_DIR' &&
    sudo install -d -o den-mcp -g den-mcp -m 0755 \
      '$REMOTE_PI_STATE_ROOT' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/ssh' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gh' &&
    sudo install -o den-mcp -g den-mcp -m 0644 /dev/null '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gitconfig' &&
    sudo install -d -m 0755 '$REMOTE_DEV_ROOT' &&
    rm -rf '$remote_asset_stage'
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
  sync_pi_docker_assets
  restart_remote
  echo "Deploy complete."
  echo "Reminder: live appsettings.json is preserved; ensure DenMcp:PiSessionHost uses $REMOTE_PI_DOCKER_DIR/compose.yaml, $REMOTE_DEV_ROOT, $REMOTE_PI_STATE_ROOT, and $REMOTE_PI_CREDENTIAL_FALLBACK_ROOT."
}

main "$@"
