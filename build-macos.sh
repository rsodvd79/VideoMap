#!/usr/bin/env bash
# build-macos.sh — pubblica VideoMap e crea il bundle .app
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT="$SCRIPT_DIR/VideoMap.App/VideoMap.App.csproj"
APP_NAME="VideoMap"
DIST_DIR="$SCRIPT_DIR/dist"
APP_BUNDLE="$DIST_DIR/$APP_NAME.app"
PUBLISH_DIR="$SCRIPT_DIR/.publish-macos"
RID="osx-arm64"

echo "▶ Pulizia..."
rm -rf "$APP_BUNDLE" "$PUBLISH_DIR"
mkdir -p "$DIST_DIR"

echo "▶ Pubblicazione ($RID)..."
dotnet publish "$PROJECT" \
    -r "$RID" \
    --self-contained true \
    -c Release \
    -o "$PUBLISH_DIR" \
    /p:PublishSingleFile=false \
    --nologo -v quiet

echo "▶ Creazione bundle $APP_NAME.app..."
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# Copia tutti i file pubblicati nella cartella MacOS
cp -r "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"

# Info.plist
cp "$SCRIPT_DIR/VideoMap.App/Info.plist" "$APP_BUNDLE/Contents/"

# Icona personalizzata
ICNS_SRC="$SCRIPT_DIR/VideoMap.App/Assets/AppIcon.icns"
if [ -f "$ICNS_SRC" ]; then
    cp "$ICNS_SRC" "$APP_BUNDLE/Contents/Resources/AppIcon.icns"
fi

echo "✅ Bundle creato: $APP_BUNDLE"
echo "▶ Apertura..."
open "$APP_BUNDLE"
