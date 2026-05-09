#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(CDPATH='' cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_PROJECT="${PUBLISH_PROJECT:-$REPO_ROOT/src/DenMcp.Cli}"
PUBLISH_BINARY="${PUBLISH_BINARY:-$PUBLISH_PROJECT/bin/Release/net10.0/publish/DenMcp.Cli}"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/bin}"
LINK_PATH="${LINK_PATH:-$INSTALL_DIR/den}"

dotnet publish "$PUBLISH_PROJECT" -c Release
mkdir -p "$INSTALL_DIR"
ln -sfn "$PUBLISH_BINARY" "$LINK_PATH"
echo "Linked $LINK_PATH -> $PUBLISH_BINARY"
