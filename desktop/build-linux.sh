#!/usr/bin/env bash
# Build the Linux release: self-contained single-file publish, then an AppImage (and a tar.gz).
# Output: desktop/dist/Nook-x86_64.AppImage and desktop/dist/Nook-linux-x64.tar.gz
# No sudo needed. appimagetool is downloaded once into desktop/tools/ (gitignored) and run without FUSE.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Nook.App/Nook.App.csproj)"
OUT=build/linux-x64
APPDIR=build/Nook.AppDir
rm -rf "$OUT" "$APPDIR" && mkdir -p dist tools

echo "== dotnet test"
dotnet test Nook.Tests -v q --nologo

echo "== dotnet publish (linux-x64, self-contained, single file)"
dotnet publish Nook.App/Nook.App.csproj -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:DebugType=none -o "$OUT"

echo "== tar.gz"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" "$APPDIR/usr/share/icons/hicolor/512x512/apps"
cp "$OUT"/* "$APPDIR/usr/bin/"
cp packaging/nook.desktop "$APPDIR/nook.desktop"
cp packaging/nook.desktop "$APPDIR/usr/share/applications/nook.desktop"
cp Nook.App/Assets/nook.png "$APPDIR/nook.png"
cp Nook.App/Assets/nook.png "$APPDIR/usr/share/icons/hicolor/512x512/apps/nook.png"
ln -sf nook.png "$APPDIR/.DirIcon"
cp packaging/AppRun "$APPDIR/AppRun"
tar -C build -czf "dist/Nook-$VERSION-linux-x64.tar.gz" --transform "s,^Nook.AppDir,Nook-$VERSION," Nook.AppDir

echo "== AppImage"
TOOL=tools/appimagetool-x86_64.AppImage
if [ ! -x "$TOOL" ]; then
  curl -fL -o "$TOOL" https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$TOOL"
fi
# Extract-and-run: works without FUSE (e.g. in containers / Flatpak sandboxes).
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APPDIR" "dist/Nook-$VERSION-x86_64.AppImage"
ls -lh dist/
