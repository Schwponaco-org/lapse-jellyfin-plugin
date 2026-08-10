#!/usr/bin/env bash
# Builds the Lapse plugin and deploys it into the local jellyfin-test container.
set -euo pipefail

CONTAINER=jellyfin-test
PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_OUTPUT="$PROJECT_DIR/Jellyfin.Plugin.Lapse/bin/Debug/net9.0"

# Match the latest published GitHub release (not just any local/pushed tag,
# which may exist without a finished release if CI failed on it), since
# build.yaml's version field is not wired into the build.
LATEST_TAG="$(curl -fsS "https://api.github.com/repos/rs-jensen/lapse-jellyfin-plugin/releases/latest" 2>/dev/null | python3 -c "import sys,json; print(json.load(sys.stdin).get('tag_name',''))" 2>/dev/null)"
if [ -z "$LATEST_TAG" ]; then
  echo "==> Could not reach GitHub releases API, falling back to latest local tag"
  LATEST_TAG="$(git -C "$PROJECT_DIR" describe --tags --abbrev=0 2>/dev/null || echo v0.0.0)"
fi
VERSION="${LATEST_TAG#v}"
IFS='.' read -ra PARTS <<< "$VERSION"
while [ "${#PARTS[@]}" -lt 4 ]; do PARTS+=("0"); done
FULL_VERSION="${PARTS[0]}.${PARTS[1]}.${PARTS[2]}.${PARTS[3]}"

echo "==> Building plugin as version $FULL_VERSION (from tag $LATEST_TAG)"
dotnet build "$PROJECT_DIR/Jellyfin.Plugin.Lapse/Jellyfin.Plugin.Lapse.csproj" -c Debug \
  -p:Version="$FULL_VERSION" \
  -p:AssemblyVersion="$FULL_VERSION" \
  -p:FileVersion="$FULL_VERSION"

echo "==> Stopping $CONTAINER"
docker stop "$CONTAINER" > /dev/null

echo "==> Clearing old plugin files"
docker run --rm -v jellyfin-config:/config alpine sh -c "rm -rf /config/plugins/LAPSE && mkdir -p /config/plugins/LAPSE"

echo "==> Copying new plugin files"
docker run --rm -v jellyfin-config:/config -v "$BUILD_OUTPUT":/src:ro alpine sh -c "cp -a /src/. /config/plugins/LAPSE/"

echo "==> Starting $CONTAINER"
docker start "$CONTAINER" > /dev/null

echo "==> Done. Tailing logs (Ctrl+C to stop)"
docker logs -f "$CONTAINER" --since 1s
