#!/bin/bash
# Publishes a new build: bumps the version, builds, and updates the feed the
# game checks on startup.
#
#     ./Tools/Release/run.sh 0.2.0 "Dice roll properly now"
#
# The game reads Docs/latest.json from the repository over the web, so pushing
# that file is what makes every copy of the game notice. Players are told on the
# title screen and sent to the download page; nothing installs itself, which is
# deliberate - a macOS app that replaces its own bundle needs signing and
# notarisation to get past Gatekeeper.
set -e

VERSION="$1"
NOTES="$2"
PROJECT="$(cd "$(dirname "$0")/../.." && pwd)"

if [ -z "${VERSION}" ]; then
    echo "Usage: ./Tools/Release/run.sh <version> [notes]" >&2
    echo "   eg: ./Tools/Release/run.sh 0.2.0 \"Dice roll properly now\"" >&2
    exit 1
fi

if ! echo "${VERSION}" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "Version should look like 0.2.0 - the game compares the parts as numbers." >&2
    exit 1
fi

# The version the built player reports, and the one the feed advertises. These
# being the same is the whole mechanism: a player is only told about an update
# when the feed is ahead of the build they are running.
sed -i '' "s/^  bundleVersion: .*/  bundleVersion: ${VERSION}/" \
    "${PROJECT}/ProjectSettings/ProjectSettings.asset"

cd "${PROJECT}"
python3 - "${VERSION}" "${NOTES}" <<'PY'
import json, sys
version = sys.argv[1]
notes = sys.argv[2] if len(sys.argv) > 2 else ""
feed = {
    "version": version,
    "url": "https://github.com/TheoPNG/Indoctrination/releases/latest",
    "notes": notes,
}
with open("Docs/latest.json", "w") as handle:
    json.dump(feed, handle, indent=2)
    handle.write("\n")
PY

echo "Version set to ${VERSION}. Building..."
"${PROJECT}/Tools/Build/run.sh"

echo ""
echo "Built v${VERSION}."
echo ""
echo "To publish it:"
echo "  1. Zip Build/macOS/Indoctrination.app and attach it to a GitHub release"
echo "  2. git add -A && git commit -m \"Release ${VERSION}\" && git push"
echo ""
echo "Pushing Docs/latest.json is what tells every copy of the game there is a"
echo "new build. Do it after the release is attached, or players will be sent"
echo "to a page that does not have it yet."
