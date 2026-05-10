#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${PUBLISH_DIR:-}"
DEPLOY_MODE="${DEPLOY_MODE:-auto}"
SSH_TARGET="${SSH_TARGET:-patch@192.168.1.10}"
SERVICE_NAME="${SERVICE_NAME:-den-mcp.service}"
REMOTE_SERVER_ROOT="${REMOTE_SERVER_ROOT:-/data/services/den-mcp/server}"
REMOTE_STAGE_DIR="${REMOTE_STAGE_DIR:-/tmp/den-mcp-live-publish}"
DEFAULT_PI_DOCKER_SOURCE=""
for candidate in "$REPO_ROOT/../pi-docker" "$REPO_ROOT/../linux/pi-docker"; do
  if [[ -f "$candidate/compose.yaml" ]]; then
    DEFAULT_PI_DOCKER_SOURCE="$(CDPATH='' cd -- "$candidate" && pwd)"
    break
  fi
done
PI_DOCKER_SOURCE="${PI_DOCKER_SOURCE:-$DEFAULT_PI_DOCKER_SOURCE}"
REMOTE_PI_DOCKER_DIR="${REMOTE_PI_DOCKER_DIR:-/data/services/den-mcp/pi-docker}"
REMOTE_PI_STATE_ROOT="${REMOTE_PI_STATE_ROOT:-/data/services/den-mcp/pi-sessions}"
REMOTE_PI_CREDENTIAL_FALLBACK_ROOT="${REMOTE_PI_CREDENTIAL_FALLBACK_ROOT:-/data/services/den-mcp/pi-credential-fallbacks}"
REMOTE_DEV_ROOT="${REMOTE_DEV_ROOT:-/data/dev}"
SKIP_RESTART=0
SKIP_PI_DOCKER_ASSETS=0
TEMP_PUBLISH_DIR_CREATED=0

usage() {
  cat <<'EOF_USAGE'
Usage: scripts/deploy-live-server.sh [options]

Build and publish DenMcp.Server into the live server tree while preserving
runtime state, then optionally restart the live systemd service.

Modes:
  local   Run on den-srv from /data/dev/den-mcp and install directly into
          /data/services/den-mcp. Preferred for agent/orchestrator deployment.
  remote  Run from a workstation and upload to SSH_TARGET first.

DEPLOY_MODE defaults to auto. Auto selects local when the script appears to be
running from den-srv's /data/dev workspace, otherwise remote.

Do not run the script itself with sudo. In local mode it uses non-interactive
sudo internally for privileged install/restart steps. In remote mode it preserves
the older SSH plus remote sudo workflow.

Options:
  --local                   Force local den-srv deployment mode
  --remote                  Force remote SSH deployment mode
  --skip-restart            Publish and sync only; do not restart services
  --skip-pi-docker-assets   Do not deploy pi-docker compose assets/state roots
  -h, --help                Show this help

Environment overrides:
  DEPLOY_MODE, PUBLISH_DIR, SSH_TARGET, SERVICE_NAME, REMOTE_SERVER_ROOT,
  REMOTE_STAGE_DIR, PI_DOCKER_SOURCE, REMOTE_PI_DOCKER_DIR,
  REMOTE_PI_STATE_ROOT, REMOTE_PI_CREDENTIAL_FALLBACK_ROOT, REMOTE_DEV_ROOT

PI_DOCKER_SOURCE defaults to a sibling pi-docker checkout when one is found at
../pi-docker or ../linux/pi-docker relative to this repository. Set it explicitly
when using another checkout layout, or pass --skip-pi-docker-assets.
EOF_USAGE
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --local)
        DEPLOY_MODE=local
        ;;
      --remote)
        DEPLOY_MODE=remote
        ;;
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
    echo "The script performs privileged install/restart steps internally." >&2
    exit 1
  fi
}

resolve_deploy_mode() {
  case "$DEPLOY_MODE" in
    local|remote)
      ;;
    auto)
      if [[ "$REPO_ROOT" == /data/dev/den-mcp* ]] && [[ -d /data/services/den-mcp/server ]]; then
        DEPLOY_MODE=local
      else
        DEPLOY_MODE=remote
      fi
      ;;
    *)
      echo "Invalid DEPLOY_MODE: $DEPLOY_MODE (expected auto, local, or remote)" >&2
      exit 1
      ;;
  esac

  echo "Deploy mode: $DEPLOY_MODE"
}

preflight_privilege() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    if ! sudo -n true 2>/dev/null; then
      cat >&2 <<EOF
Deploy preflight failed: local mode requires non-interactive sudo for install and restart steps.

This script can be kicked off by an agent, but that agent account needs a narrow
sudoers rule or equivalent helper allowing the privileged deploy actions. Run
with --skip-restart only avoids the systemctl restart, not the privileged install
into $REMOTE_SERVER_ROOT.
EOF
      exit 1
    fi
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
  scripts/deploy-live-server.sh
EOF
      exit 1
    fi
  fi
}

initialize_publish_dir() {
  if [[ -n "$PUBLISH_DIR" ]]; then
    rm -rf "$PUBLISH_DIR"
    mkdir -p "$PUBLISH_DIR"
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

sudo_local() {
  sudo -n "$@"
}

sync_server_tree_local() {
  echo "Applying publish output locally to $REMOTE_SERVER_ROOT ..."
  sudo_local mkdir -p "$REMOTE_SERVER_ROOT"
  sudo_local rsync -a --delete --chown=den-mcp:den-mcp \
    --exclude '.den-mcp/' \
    --exclude 'env/' \
    --exclude '.local/' \
    --exclude '.net/' \
    --exclude 'appsettings.json' \
    --exclude 'appsettings.Development.json' \
    "$PUBLISH_DIR/" "$REMOTE_SERVER_ROOT/"
}

sync_server_tree_remote() {
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

sync_server_tree() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    sync_server_tree_local
  else
    sync_server_tree_remote
  fi
}

sync_pi_docker_assets_local() {
  echo "Installing pi-docker assets from $PI_DOCKER_SOURCE locally to $REMOTE_PI_DOCKER_DIR ..."
  sudo_local install -d -o den-mcp -g den-mcp -m 0755 "$REMOTE_PI_DOCKER_DIR"
  sudo_local rsync -a --delete --chown=den-mcp:den-mcp --exclude '.env' \
    "$PI_DOCKER_SOURCE/" "$REMOTE_PI_DOCKER_DIR/"
  sudo_local rm -f "$REMOTE_PI_DOCKER_DIR/.env"
  sudo_local chmod -R u=rwX,go=rX "$REMOTE_PI_DOCKER_DIR"
  sudo_local install -d -o den-mcp -g 166535 -m 2771 \
    "$REMOTE_PI_STATE_ROOT" \
    "$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT" \
    "$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/ssh" \
    "$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gh"
  sudo_local install -o den-mcp -g 166535 -m 0660 /dev/null "$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gitconfig"
  sudo_local install -d -m 0755 "$REMOTE_DEV_ROOT"
}

sync_pi_docker_assets_remote() {
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
    sudo install -d -o den-mcp -g 166535 -m 2771 \
      '$REMOTE_PI_STATE_ROOT' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/ssh' \
      '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gh' &&
    sudo install -o den-mcp -g 166535 -m 0660 /dev/null '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT/gitconfig' &&
    sudo install -d -m 0755 '$REMOTE_DEV_ROOT' &&
    rm -rf '$remote_asset_stage'
  "
}

sync_pi_docker_assets() {
  if [[ "$SKIP_PI_DOCKER_ASSETS" -eq 1 ]]; then
    echo "Skipping pi-docker asset deployment."
    return
  fi

  if [[ -z "$PI_DOCKER_SOURCE" ]]; then
    cat >&2 <<EOF
pi-docker source was not provided and no sibling checkout was found.

Set PI_DOCKER_SOURCE to the local pi-docker checkout, or pass
--skip-pi-docker-assets if this deploy intentionally should not update Den-owned
Pi session compose assets. The automatic lookup checks ../pi-docker and
../linux/pi-docker relative to this repository.
EOF
    exit 1
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

  if [[ "$DEPLOY_MODE" == "local" ]]; then
    sync_pi_docker_assets_local
  else
    sync_pi_docker_assets_remote
  fi
}

validate_pi_session_host_config_local() {
  local appsettings="$REMOTE_SERVER_ROOT/appsettings.json"
  local expected_compose_file="$REMOTE_PI_DOCKER_DIR/compose.yaml"

  echo "Validating live PiSessionHost appsettings locally before restart ..."
  local python3_path
  python3_path="$(command -v python3)" || {
    echo 'Deploy preflight failed: python3 is required to validate live appsettings.' >&2
    exit 1
  }
  sudo_local "$python3_path" "$REPO_ROOT/scripts/validate-pi-session-host-appsettings.py" \
    "$appsettings" \
    --expected-compose-file "$expected_compose_file" \
    --expected-dev-dir "$REMOTE_DEV_ROOT" \
    --expected-pi-state-root-dir "$REMOTE_PI_STATE_ROOT" \
    --expected-credential-fallback-root-dir "$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT"
}

validate_pi_session_host_config_remote() {
  local remote_appsettings="$REMOTE_SERVER_ROOT/appsettings.json"
  local expected_compose_file="$REMOTE_PI_DOCKER_DIR/compose.yaml"

  echo "Validating live PiSessionHost appsettings on $SSH_TARGET before restart ..."
  ssh "$SSH_TARGET" "python3_path=\$(command -v python3) || { echo 'Deploy preflight failed: python3 is required on the remote host to validate live appsettings.' >&2; exit 1; }; sudo \"\$python3_path\" - '$remote_appsettings' --expected-compose-file '$expected_compose_file' --expected-dev-dir '$REMOTE_DEV_ROOT' --expected-pi-state-root-dir '$REMOTE_PI_STATE_ROOT' --expected-credential-fallback-root-dir '$REMOTE_PI_CREDENTIAL_FALLBACK_ROOT'" < "$REPO_ROOT/scripts/validate-pi-session-host-appsettings.py"
}

validate_pi_session_host_config() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    validate_pi_session_host_config_local
  else
    validate_pi_session_host_config_remote
  fi
}

restart_local() {
  if [[ "$SKIP_RESTART" -eq 1 ]]; then
    echo "Skipping service restart."
    return
  fi

  echo "Restarting live service locally ..."
  sudo_local systemctl restart "$SERVICE_NAME"
  sudo_local systemctl --no-pager --full status "$SERVICE_NAME" --lines=20
}

restart_remote() {
  if [[ "$SKIP_RESTART" -eq 1 ]]; then
    echo "Skipping remote service restart."
    return
  fi

  echo "Restarting live service on $SSH_TARGET ..."
  ssh -t "$SSH_TARGET" "sudo systemctl restart '$SERVICE_NAME' && sudo systemctl --no-pager --full status '$SERVICE_NAME' --lines=20"
}

restart_service() {
  if [[ "$DEPLOY_MODE" == "local" ]]; then
    restart_local
  else
    restart_remote
  fi
}

main() {
  require_non_root
  parse_args "$@"
  resolve_deploy_mode
  preflight_privilege
  preflight_workspace
  initialize_publish_dir
  trap cleanup EXIT
  publish_server
  sync_server_tree
  sync_pi_docker_assets
  validate_pi_session_host_config
  restart_service
  echo "Deploy complete."
  echo "Verified preserved live appsettings.json DenMcp:PiSessionHost uses service-accessible Pi paths for this deploy."
}

main "$@"
