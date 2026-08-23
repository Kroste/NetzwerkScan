#!/usr/bin/env bash
# Baut aus einem fertigen linux-x64-Publish eine AppImage.
# Aufruf:  bash packaging/linux/build-appimage.sh <version> <publish-dir>
# Beispiel: bash packaging/linux/build-appimage.sh 1.0.0 publish/linux
set -euo pipefail

VERSION="${1:-0.0.0}"
PUBLISH_DIR="${2:-publish/linux}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

APPDIR="AppDir"
rm -rf "${APPDIR}"
mkdir -p "${APPDIR}/usr/bin"

# 1) Publish-Output in die AppDir kopieren
cp -r "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"
chmod +x "${APPDIR}/usr/bin/NetScanner"

# 2) Desktop-Eintrag, Icon und AppRun
cp "${HERE}/NetScanner.desktop" "${APPDIR}/NetScanner.desktop"
# Eine einzige Icon-Quelle: die App-Assets. Eine Kopie hier daneben driftet
# sonst bei jedem Icon-Rebuild auseinander.
cp "${HERE}/../../NetScanner/Assets/netscanner.png" "${APPDIR}/netscanner.png"
cp "${HERE}/AppRun"             "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun"

# Hinweis: Die Kamera-Vorschau nutzt System-ffmpeg (kein Bundling). Ohne ffmpeg
# zeigt die App einen Hinweis, der externe Player funktioniert weiter.

# 3) appimagetool holen (FUSE-frei via --appimage-extract-and-run)
if [ ! -x appimagetool ]; then
    wget -q -O appimagetool \
        "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
    chmod +x appimagetool
fi

# 4) AppImage erzeugen
export ARCH=x86_64
OUT="NetScanner-${VERSION}-x86_64.AppImage"
./appimagetool --appimage-extract-and-run "${APPDIR}" "${OUT}"
echo "AppImage erstellt: ${OUT}"
