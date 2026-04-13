#!/usr/bin/env bash
set -euo pipefail

LIVE_ROOT=${LIVE_ROOT:-/data/dev/den-mcp}
SERVER_ROOT=${SERVER_ROOT:-$LIVE_ROOT/server}
ENV_FILE=${ENV_FILE:-$SERVER_ROOT/env/server.env}
SIGNAL_BIN=${SIGNAL_BIN:-$SERVER_ROOT/.local/bin/signal-cli}
SERVICE_HOME=${SERVICE_HOME:-$SERVER_ROOT}
PROJECT_ID=${PROJECT_ID:-den-mcp}
TARGET_AGENT=${TARGET_AGENT:-claude-code}
MESSAGE_TYPE=${MESSAGE_TYPE:-review_request}
SMOKE_SENDER=${SMOKE_SENDER:-ops-smoke}
WAIT_FOR_REACTION_SECONDS=${WAIT_FOR_REACTION_SECONDS:-0}
SKIP_DIRECT_SEND=false
SKIP_DISPATCH=false

usage() {
  cat <<'EOF'
Usage: smoke-live-dispatch-signal.sh [options]

Repeatable live smoke check for the den-mcp + signal-cli integration.

Options:
  --project <id>                 Project to post the smoke message into. Default: den-mcp
  --target-agent <agent>         Explicit dispatch recipient. Default: claude-code
  --message-type <type>          Message metadata type. Default: review_request
  --sender <sender>              Sender recorded on the smoke message. Default: ops-smoke
  --wait-for-reaction <seconds>  Poll the created dispatch until Signal approval/rejection lands.
  --skip-direct-send             Skip the raw signal-cli JSON-RPC send check.
  --skip-dispatch                Skip creating the message-trigger dispatch check.
  -h, --help                     Show this help text.

Environment overrides:
  LIVE_ROOT, SERVER_ROOT, ENV_FILE, SIGNAL_BIN, SERVICE_HOME,
  PROJECT_ID, TARGET_AGENT, MESSAGE_TYPE, SMOKE_SENDER, WAIT_FOR_REACTION_SECONDS
EOF
}

require_root() {
  if [[ ${EUID:-$(id -u)} -ne 0 ]]; then
    echo "Run this script with sudo/root so it can inspect systemd and execute as the live service users." >&2
    exit 1
  fi
}

die() {
  echo "ERROR: $*" >&2
  exit 1
}

extract_var() {
  local key=$1
  [[ -f "$ENV_FILE" ]] || die "Expected env file at $ENV_FILE"
  sed -n "s/^${key}=//p" "$ENV_FILE" | tail -n 1
}

url_port() {
  local url=${1:-}
  python3 - "$url" <<'PY'
import sys
from urllib.parse import urlparse

value = sys.argv[1].strip()
if not value:
    print("")
    raise SystemExit(0)

parsed = urlparse(value)
if parsed.port is not None:
    print(parsed.port)
PY
}

encode_path_segment() {
  python3 - "$1" <<'PY'
import sys
from urllib.parse import quote

print(quote(sys.argv[1], safe=""))
PY
}

as_patch() {
  sudo -u patch -- env HOME="$SERVICE_HOME" "$@"
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

check_unit() {
  local unit=$1
  local expected_user=$2

  systemctl is-active --quiet "$unit" || die "$unit is not active"

  local actual_user
  actual_user=$(systemctl show -p User --value "$unit")
  echo "$unit active (User=$actual_user)"

  if [[ "$actual_user" != "$expected_user" ]]; then
    die "$unit is running as $actual_user, expected $expected_user"
  fi
}

build_signal_send_rpc() {
  local message=$1
  local recipient=$2
  local account=${3:-}

  python3 - "$message" "$recipient" "$account" <<'PY'
import json
import sys

message, recipient, account = sys.argv[1:4]
params = {"message": message}
if recipient.startswith("+"):
    params["recipient"] = [recipient]
else:
    params["usernames"] = [recipient]
if account:
    params["account"] = account

body = {
    "jsonrpc": "2.0",
    "method": "send",
    "id": "smoke-direct-send",
    "params": params,
}
print(json.dumps(body))
PY
}

build_message_body() {
  local sender=$1
  local content=$2
  local message_type=$3
  local recipient=$4

  python3 - "$sender" "$content" "$message_type" "$recipient" <<'PY'
import json
import sys

sender, content, message_type, recipient = sys.argv[1:5]
metadata = json.dumps({"type": message_type, "recipient": recipient})
body = {
    "sender": sender,
    "content": content,
    "metadata": metadata,
}
print(json.dumps(body))
PY
}

extract_json_field() {
  local expression=$1
  python3 - "$expression" <<'PY'
import json
import sys

expr = sys.argv[1]
data = json.load(sys.stdin)

if expr == "timestamp":
    result = data.get("result", {}).get("timestamp")
elif expr == "message_id":
    result = data.get("id")
elif expr == "dispatch_id":
    result = data.get("id")
elif expr == "dispatch_status":
    result = data.get("status")
elif expr == "dispatch_summary":
    result = data.get("summary")
else:
    raise SystemExit(f"Unknown expression: {expr}")

if result is None:
    raise SystemExit(1)
print(result)
PY
}

find_dispatch_for_message() {
  local message_id=$1

  python3 - "$message_id" <<'PY'
import json
import sys

message_id = int(sys.argv[1])
entries = json.load(sys.stdin)

for entry in entries:
    if entry.get("trigger_type") == "message" and entry.get("trigger_id") == message_id:
        print(json.dumps(entry))
        raise SystemExit(0)

raise SystemExit(1)
PY
}

wait_for_dispatch_for_message() {
  local den_base=$1
  local project_id=$2
  local target_agent=$3
  local message_id=$4
  local attempts=${5:-30}
  local dispatch

  for ((i=1; i<=attempts; i++)); do
    local payload
    payload=$(curl -fsS -G "$den_base/api/dispatch" \
      --data-urlencode "projectId=$project_id" \
      --data-urlencode "targetAgent=$target_agent" \
      --data-urlencode "status=pending,approved,rejected,completed")

    if dispatch=$(printf '%s' "$payload" | find_dispatch_for_message "$message_id" 2>/dev/null); then
      printf '%s\n' "$dispatch"
      return 0
    fi

    sleep 1
  done

  return 1
}

wait_for_dispatch_status() {
  local den_base=$1
  local dispatch_id=$2
  local timeout_seconds=$3
  local elapsed=0

  while (( elapsed < timeout_seconds )); do
    local payload
    payload=$(curl -fsS "$den_base/api/dispatch/$dispatch_id")
    local status
    status=$(printf '%s' "$payload" | extract_json_field dispatch_status)

    case "$status" in
      approved|rejected|completed)
        printf '%s\n' "$payload"
        return 0
        ;;
    esac

    sleep 1
    ((elapsed+=1))
  done

  curl -fsS "$den_base/api/dispatch/$dispatch_id"
  return 124
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --project)
        PROJECT_ID=$2
        shift 2
        ;;
      --target-agent)
        TARGET_AGENT=$2
        shift 2
        ;;
      --message-type)
        MESSAGE_TYPE=$2
        shift 2
        ;;
      --sender)
        SMOKE_SENDER=$2
        shift 2
        ;;
      --wait-for-reaction)
        WAIT_FOR_REACTION_SECONDS=$2
        shift 2
        ;;
      --skip-direct-send)
        SKIP_DIRECT_SEND=true
        shift
        ;;
      --skip-dispatch)
        SKIP_DISPATCH=true
        shift
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
  done
}

main() {
  parse_args "$@"
  require_root

  [[ -f "$ENV_FILE" ]] || die "Expected env file at $ENV_FILE"
  [[ -x "$SIGNAL_BIN" ]] || die "Expected executable signal-cli at $SIGNAL_BIN"

  local signal_account signal_recipient signal_host signal_port listen_url den_port
  signal_account=$(extract_var 'DenMcp__Signal__Account')
  signal_recipient=$(extract_var 'DenMcp__Signal__Recipient')
  if [[ -z "$signal_recipient" ]]; then
    signal_recipient=$(extract_var 'DenMcp__Signal__RecipientNumber')
  fi
  signal_host=$(extract_var 'DenMcp__Signal__HttpHost')
  signal_port=$(extract_var 'DenMcp__Signal__HttpPort')
  listen_url=$(extract_var 'DenMcp__ListenUrl')
  den_port=$(url_port "$listen_url")

  : "${signal_host:=127.0.0.1}"
  : "${signal_port:=12081}"
  : "${den_port:=5199}"

  [[ -n "$signal_account" ]] || die "DenMcp__Signal__Account is not configured in $ENV_FILE"
  [[ -n "$signal_recipient" ]] || die "Signal recipient is not configured in $ENV_FILE"

  local den_base signal_base smoke_tag host_name
  den_base="http://127.0.0.1:${den_port}"
  signal_base="http://${signal_host}:${signal_port}"
  smoke_tag="den-smoke-$(date -u +%Y%m%dT%H%M%SZ)"
  host_name=$(hostname -s 2>/dev/null || hostname)

  echo "== live services =="
  check_unit signal-cli-den.service patch
  check_unit den-mcp.service den-mcp

  echo
  echo "== health endpoints =="
  wait_for_http "$signal_base/api/v1/check" "Signal daemon" 45
  curl -fsS "$signal_base/api/v1/check"
  echo
  wait_for_http "$den_base/health" "den-mcp" 45
  curl -fsS "$den_base/health"
  echo

  echo
  echo "== linked account visibility (service user: patch) =="
  local accounts_output
  accounts_output=$(as_patch "$SIGNAL_BIN" listAccounts 2>&1) || {
    printf '%s\n' "$accounts_output"
    die "signal-cli listAccounts failed for service user patch"
  }
  printf '%s\n' "$accounts_output"
  grep -Fq "$signal_account" <<<"$accounts_output" || die "Configured account $signal_account was not visible to patch"

  if [[ "$SKIP_DIRECT_SEND" == false ]]; then
    echo
    echo "== direct daemon send =="
    local direct_message direct_body direct_response direct_timestamp
    direct_message="[$smoke_tag] direct Signal daemon smoke check from ${host_name}. If you received this, /api/v1/rpc send is healthy."
    direct_body=$(build_signal_send_rpc "$direct_message" "$signal_recipient" "$signal_account")
    direct_response=$(curl -fsS -H 'Content-Type: application/json' -d "$direct_body" "$signal_base/api/v1/rpc")
    printf '%s\n' "$direct_response"
    direct_timestamp=$(printf '%s' "$direct_response" | extract_json_field timestamp) || die "Signal direct-send response did not include a timestamp"
    echo "direct_send_timestamp=$direct_timestamp"
  fi

  if [[ "$SKIP_DISPATCH" == false ]]; then
    echo
    echo "== message-trigger dispatch =="
    local dispatch_message message_body encoded_project message_response message_id
    dispatch_message="[$smoke_tag] dispatch smoke check for ${TARGET_AGENT} from ${host_name}. This should create a message-trigger dispatch and deliver a Signal notification. React with ✅ or 👍 to approve, or ❌ or 👎 to reject."
    message_body=$(build_message_body "$SMOKE_SENDER" "$dispatch_message" "$MESSAGE_TYPE" "$TARGET_AGENT")
    encoded_project=$(encode_path_segment "$PROJECT_ID")
    message_response=$(curl -fsS -H 'Content-Type: application/json' -d "$message_body" \
      "$den_base/api/projects/${encoded_project}/messages")
    printf '%s\n' "$message_response"

    message_id=$(printf '%s' "$message_response" | extract_json_field message_id) || die "Message creation response did not include an id"
    echo "smoke_message_id=$message_id"

    local dispatch_json dispatch_id dispatch_status dispatch_summary
    dispatch_json=$(wait_for_dispatch_for_message "$den_base" "$PROJECT_ID" "$TARGET_AGENT" "$message_id" 30) \
      || die "Timed out waiting for a dispatch created from message #$message_id"
    dispatch_id=$(printf '%s' "$dispatch_json" | extract_json_field dispatch_id)
    dispatch_status=$(printf '%s' "$dispatch_json" | extract_json_field dispatch_status)
    dispatch_summary=$(printf '%s' "$dispatch_json" | extract_json_field dispatch_summary || true)

    printf '%s\n' "$dispatch_json"
    echo "dispatch_id=$dispatch_id"
    echo "dispatch_status=$dispatch_status"
    [[ -n "${dispatch_summary:-}" ]] && echo "dispatch_summary=$dispatch_summary"

    if (( WAIT_FOR_REACTION_SECONDS > 0 )); then
      echo
      echo "Waiting up to ${WAIT_FOR_REACTION_SECONDS}s for a Signal reaction on dispatch #${dispatch_id} ..."
      local final_dispatch final_status reaction_check_rc
      if final_dispatch=$(wait_for_dispatch_status "$den_base" "$dispatch_id" "$WAIT_FOR_REACTION_SECONDS"); then
        reaction_check_rc=0
      else
        reaction_check_rc=$?
      fi

      if (( reaction_check_rc != 0 )) && (( reaction_check_rc != 124 )); then
        die "Failed to recheck dispatch #$dispatch_id (exit $reaction_check_rc)"
      fi

      final_status=$(printf '%s' "$final_dispatch" | extract_json_field dispatch_status)
      printf '%s\n' "$final_dispatch"

      case "$final_status" in
        approved|rejected|completed)
          echo "reaction_result=$final_status"
          ;;
        *)
          cat <<EOF
dispatch #$dispatch_id stayed pending after ${WAIT_FOR_REACTION_SECONDS}s.

Manual verification / cleanup:
  1. Confirm the Signal notification arrived for $signal_recipient.
  2. React with ✅ or 👍 to approve, or ❌ or 👎 to reject.
  3. Re-check:
       curl -fsS $den_base/api/dispatch/$dispatch_id
  4. If this was only a smoke run and you want to clear it manually:
       curl -fsS -H 'Content-Type: application/json' -d '{"decided_by":"ops-smoke-timeout"}' \\
         $den_base/api/dispatch/$dispatch_id/reject
EOF
          ;;
      esac
    else
      cat <<EOF
Manual reaction step:
  1. Confirm the Signal notification for dispatch #$dispatch_id arrived at $signal_recipient.
  2. React with ✅ or 👍 to approve, or ❌ or 👎 to reject.
  3. Re-check:
       curl -fsS $den_base/api/dispatch/$dispatch_id
EOF
    fi
  fi
}

main "$@"
