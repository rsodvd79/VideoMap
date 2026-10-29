#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo "Questo script richiede macOS Apple Silicon." >&2
    exit 1
fi

cargo build --release --locked
APP="$ROOT/dist/VideoMap.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
install -m 755 target/release/videomap "$APP/Contents/MacOS/videomap"
cp packaging/Info.plist "$APP/Contents/Info.plist"
cp assets/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
cp packaging/LEGGIMI.txt "$APP/Contents/Resources/LEGGIMI.txt"

if [[ -n "${VIDEOMAP_VERSION:-}" ]]; then
    [[ "$VIDEOMAP_VERSION" =~ ^v[0-9]+(\.[0-9]+)*$ ]] || {
        echo "Versione non valida" >&2
        exit 1
    }
    VERSION="${VIDEOMAP_VERSION#v}"
    BUNDLE_VERSION="$(printf '%s' "$VERSION" | awk -F. '{printf "%d.%d.%d", $1, $2, $3}')"
    /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $BUNDLE_VERSION" "$APP/Contents/Info.plist"
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUNDLE_VERSION" "$APP/Contents/Info.plist"
fi

echo "Bundle creato: $APP"
if [[ "${1:-}" != "--no-open" ]]; then
    open "$APP"
fi
