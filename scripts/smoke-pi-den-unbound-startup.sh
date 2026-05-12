#!/usr/bin/env bash
set -euo pipefail

# Smoke Pi's Den extension from a directory that is intentionally outside every
# registered Den project root. Requires a reachable Den server and an installed
# `pi` CLI/model. The extension should enter the quiet "no project bound" state
# and still allow the model turn to complete without check-in/project errors.

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEN_PI_DIR="${DEN_PI_DIR:-/home/dev/den-pi}"
if [ ! -d "$DEN_PI_DIR" ]; then
  echo "den-pi checkout not found at $DEN_PI_DIR; set DEN_PI_DIR to the den-pi repo path" >&2
  exit 1
fi
MODEL="${PI_SMOKE_MODEL:-openai-codex/gpt-5.5}"
BASE_URL="${DEN_MCP_URL:-${DEN_MCP_BASE_URL:-http://192.168.1.10:5199}}"
TMP_DIR="$(mktemp -d)"
OUTPUT_FILE="$(mktemp)"
trap 'rm -rf "$TMP_DIR" "$OUTPUT_FILE"' EXIT

unset DEN_PI_PROJECT_ID

(
  cd "$TMP_DIR"
  DEN_MCP_URL="$BASE_URL" pi \
    -e "$DEN_PI_DIR/extensions/den.ts" \
    -e "$DEN_PI_DIR/extensions/den-subagent.ts" \
    --model "$MODEL" \
    --mode json \
    -p 'Reply with exactly: ok'
) >"$OUTPUT_FILE" 2>&1

if grep -Eiq 'Den check-in failed|FOREIGN KEY|failed with 404|Project .* not found|No active session or binding found' "$OUTPUT_FILE"; then
  echo "Unexpected project-binding/check-in error while unbound:" >&2
  cat "$OUTPUT_FILE" >&2
  exit 1
fi

cat "$OUTPUT_FILE"
