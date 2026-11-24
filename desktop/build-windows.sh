#!/usr/bin/env bash
# Build the Windows release on Linux: self-contained single-file Nook.exe, zipped with a README.
# Output: desktop/dist/Nook-<version>-win-x64.zip   (unsigned; nothing here can run it, see README.md)
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Nook.App/Nook.App.csproj)"
OUT=build/win-x64
STAGE=build/win-zip
ZIP="dist/Nook-$VERSION-win-x64.zip"
rm -rf "$OUT" "$STAGE" "$ZIP" && mkdir -p dist "$STAGE"

echo "== dotnet test"
dotnet test Nook.Tests -v q --nologo

echo "== dotnet publish (win-x64, self-contained, single file)"
dotnet publish Nook.App/Nook.App.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$OUT"

# Single file + self-extracting native libs: Nook.exe should be the only file. Fail loudly if that changes.
extra="$(ls "$OUT" | grep -vx 'Nook.exe' || true)"
if [ -n "$extra" ]; then echo "Unexpected files next to Nook.exe: $extra" >&2; exit 1; fi

echo "== zip"
cp "$OUT/Nook.exe" "$STAGE/"
sed "s/VERSION/$VERSION/" packaging/README-windows.txt | sed 's/$/\r/' > "$STAGE/README.txt"  # CRLF for Notepad
(cd "$STAGE" && zip -q -9 -X "../../$ZIP" Nook.exe README.txt)
unzip -l "$ZIP"
ls -lh "$ZIP"
