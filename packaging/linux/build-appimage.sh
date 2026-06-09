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
cp "${HERE}/netscanner.png"     "${APPDIR}/netscanner.png"
cp "${HERE}/AppRun"             "${APPDIR}/AppRun"
chmod +x "${APPDIR}/AppRun"

# --- OPTIONAL: libvlc + Plugins mitbündeln (auskommentiert) ---------------
# Ohne diesen Block erwartet die AppImage eine System-libvlc (z. B. Paket "vlc"
# bzw. "libvlc5"). Zum Bündeln auf einem Ubuntu-Runner etwa:
#
#   sudo apt-get update
#   sudo apt-get install -y vlc libvlc5 libvlc-bin vlc-plugin-base
#   mkdir -p "${APPDIR}/usr/bin/libvlc/plugins"
#   cp /usr/lib/x86_64-linux-gnu/libvlc*.so* "${APPDIR}/usr/bin/libvlc/"
#   cp -r /usr/lib/x86_64-linux-gnu/vlc/plugins/* "${APPDIR}/usr/bin/libvlc/plugins/"
#
# AppRun setzt LD_LIBRARY_PATH/VLC_PLUGIN_PATH automatisch, sobald der Ordner existiert.
# --------------------------------------------------------------------------

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
