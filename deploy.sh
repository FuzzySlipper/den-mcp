#!/bin/bash
set -e

RELEASE_DIR="/mnt/den-data/dev/den-mcp/server"
PUBLISH_DIR="/tmp/den-publish"

echo "Building and publishing..."
dotnet publish src/DenMcp.Server/DenMcp.Server.csproj \
  -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$PUBLISH_DIR/"

echo "Deploying to $RELEASE_DIR..."
# Sync the binary and web assets, but never touch build/ (user data)
rsync -a --delete \
  --exclude '.den-mcp/' \
  --exclude 'appsettings.json' \
  --exclude 'appsettings.Development.json' \
  "$PUBLISH_DIR/" "$RELEASE_DIR/"

echo "Done."
