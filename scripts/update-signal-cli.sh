#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=${LIVE_ROOT:-/data/dev/den-mcp}
SERVER_ROOT=${SERVER_ROOT:-$LIVE_ROOT/server}
REPO_ROOT=${REPO_ROOT:-$LIVE_ROOT/repo}
INSTALL_BASE=${INSTALL_BASE:-$SERVER_ROOT/.local/opt}
BIN_DIR=${BIN_DIR:-$SERVER_ROOT/.local/bin}
BIN_LINK=${BIN_LINK:-$BIN_DIR/signal-cli}
SERVICE_HOME=${SERVICE_HOME:-$SERVER_ROOT}
ENV_FILE=${ENV_FILE:-$SERVER_ROOT/env/server.env}
KEEP_VERSIONS=${KEEP_VERSIONS:-2}
GITHUB_API=${GITHUB_API:-https://api.github.com/repos/AsamK/signal-cli/releases/latest}
CHECK_ONLY=false
APPLY=false

usage() {
  cat <<'EOF'
Usage: update-signal-cli.sh [--check-only | --apply]

Options:
  --check-only   Print current/latest version info without changing anything.
  --apply        Download, install, smoke-test, and switch to the latest version.

Environment overrides:
  LIVE_ROOT, SERVER_ROOT, REPO_ROOT, INSTALL_BASE, BIN_DIR, BIN_LINK,
  SERVICE_HOME, ENV_FILE, KEEP_VERSIONS, GITHUB_API
EOF
}

require_root_for_apply() {
  if [[ $EUID -ne 0 ]]; then
    echo "--apply requires sudo/root." >&2
    exit 1
  fi
}

run_as_patch() {
  if [[ $EUID -eq 0 ]]; then
    sudo -u patch -- env HOME="$SERVICE_HOME" "$@"
  else
    env HOME="$SERVICE_HOME" "$@"
  fi
}

get_current_version() {
  if [[ ! -L "$BIN_LINK" && ! -x "$BIN_LINK" ]]; then
    return 0
  fi

  local resolved
  resolved=$(readlink -f "$BIN_LINK" 2>/dev/null || true)
  if [[ -z "$resolved" ]]; then
    return 0
  fi

  basename "$(dirname "$resolved")" | sed -n 's/^signal-cli-//p'
}

fetch_release_metadata() {
  curl -fsSL "$GITHUB_API"
}

parse_release_field() {
  local field=$1
  python3 -c '
import json
import sys

field = sys.argv[1]
data = json.load(sys.stdin)

if field == "version":
    tag = data["tag_name"]
    print(tag[1:] if tag.startswith("v") else tag)
elif field == "asset_url":
    for asset in data.get("assets", []):
        name = asset.get("name", "")
        if name.endswith("-Linux-native.tar.gz"):
            print(asset["browser_download_url"])
            break
    else:
        raise SystemExit("No Linux-native asset found in latest release metadata.")
else:
    raise SystemExit(f"Unknown field: {field}")
' "$field"
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

get_signal_port() {
  if [[ -f "$ENV_FILE" ]]; then
    local configured
    configured=$(sed -n 's/^DenMcp__Signal__HttpPort=//p' "$ENV_FILE" | tail -n 1)
    if [[ -n "$configured" ]]; then
      echo "$configured"
      return
    fi
  fi

  echo "12081"
}

prune_old_versions() {
  mapfile -t dirs < <(find "$INSTALL_BASE" -maxdepth 1 -mindepth 1 -type d -name 'signal-cli-*' -printf '%f\n' | sort -V)
  local count=${#dirs[@]}
  if (( count <= KEEP_VERSIONS )); then
    return
  fi

  local remove_count=$((count - KEEP_VERSIONS))
  for ((i=0; i<remove_count; i++)); do
    rm -rf "$INSTALL_BASE/${dirs[$i]}"
  done
}

main() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --check-only)
        CHECK_ONLY=true
        ;;
      --apply)
        APPLY=true
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

  if [[ $CHECK_ONLY == false && $APPLY == false ]]; then
    usage >&2
    exit 1
  fi

  if [[ $APPLY == true ]]; then
    require_root_for_apply
  fi

  local release_json current_version latest_version asset_url
  release_json=$(fetch_release_metadata)
  current_version=$(get_current_version || true)
  latest_version=$(printf '%s' "$release_json" | parse_release_field version)
  asset_url=$(printf '%s' "$release_json" | parse_release_field asset_url)

  echo "current_version=${current_version:-none}"
  echo "latest_version=$latest_version"
  echo "asset_url=$asset_url"

  if [[ $CHECK_ONLY == true ]]; then
    exit 0
  fi

  if [[ "$current_version" == "$latest_version" ]]; then
    echo "signal-cli is already up to date."
    exit 0
  fi

  local target_dir archive tmp_dir signal_port
  target_dir="$INSTALL_BASE/signal-cli-$latest_version"
  signal_port=$(get_signal_port)

  mkdir -p "$INSTALL_BASE" "$BIN_DIR"
  tmp_dir=$(mktemp -d)
  archive="$tmp_dir/signal-cli-$latest_version-Linux-native.tar.gz"

  trap 'rm -rf "$tmp_dir"' EXIT

  echo "Downloading signal-cli $latest_version ..."
  curl -fsSL "$asset_url" -o "$archive"

  rm -rf "$target_dir"
  mkdir -p "$target_dir"
  tar -xzf "$archive" -C "$target_dir"

  if [[ ! -x "$target_dir/signal-cli" ]]; then
    echo "Downloaded archive did not contain an executable signal-cli binary." >&2
    exit 1
  fi

  "$target_dir/signal-cli" --version
  echo "Running smoke test against the linked account ..."
  run_as_patch "$target_dir/signal-cli" listAccounts >/dev/null

  echo "Switching signal-cli symlink ..."
  ln -sfn "../opt/signal-cli-$latest_version/signal-cli" "$BIN_LINK"
  chown -R patch:patch "$target_dir" "$BIN_DIR"

  echo "Restarting signal-cli-den.service ..."
  systemctl restart signal-cli-den.service
  wait_for_http "http://127.0.0.1:${signal_port}/api/v1/check" "Signal daemon" 45

  prune_old_versions

  echo "signal-cli updated to $latest_version"
}

main "$@"
