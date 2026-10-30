#!/usr/bin/env bash
# One build/run entrypoint for the independent ConvertDesk macOS app.
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT_DIR="$ROOT_DIR/artifacts/macos"
RUNTIME="osx-$(uname -m)"
[[ "$RUNTIME" != osx-x86_64 ]] || RUNTIME=osx-x64
NATIVE_RUNTIME="$RUNTIME"
MODE=run
MAKE_DMG=false
VERSION=0.2.0
SIGN_IDENTITY="${CONVERTDESK_SIGNING_IDENTITY:--}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --build-only|--verify|--debug|--logs|--telemetry) MODE="$1"; shift ;;
    --output) OUTPUT_DIR="$2"; shift 2 ;;
    --runtime) RUNTIME="$2"; shift 2 ;;
    --dmg) MAKE_DMG=true; shift ;;
    --help|-h) printf '%s\n' 'Usage: script/build_and_run.sh [--build-only|--verify|--debug|--logs|--telemetry] [--output DIRECTORY] [--runtime osx-arm64|osx-x64] [--dmg]'; exit 0 ;;
    *) printf 'Unknown option: %s\n' "$1" >&2; exit 2 ;;
  esac
done
[[ "$(uname -s)" == Darwin ]] || { echo 'macOS packaging must run on macOS.' >&2; exit 1; }
[[ "$RUNTIME" == osx-arm64 || "$RUNTIME" == osx-x64 ]] || { echo 'Unsupported macOS runtime.' >&2; exit 1; }
DOTNET_BIN="${CONVERTDESK_DOTNET:-}"
if [[ -z "$DOTNET_BIN" ]]; then
  if command -v dotnet >/dev/null 2>&1; then DOTNET_BIN="$(command -v dotnet)";
  elif [[ -x "$ROOT_DIR/../dotnet/dotnet" ]]; then DOTNET_BIN="$ROOT_DIR/../dotnet/dotnet";
  else echo 'Install .NET SDK 10, or set CONVERTDESK_DOTNET to its dotnet executable.' >&2; exit 1; fi
fi
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(cd "$OUTPUT_DIR" && pwd)"
APP_BUNDLE="$OUTPUT_DIR/ConvertDesk.app"
# Stop only this build; never terminate the separately installed Filee application.
python3 - "$APP_BUNDLE/Contents/MacOS/ConvertDesk" <<'PY'
import os, signal, subprocess, sys
for line in subprocess.check_output(['ps', '-axo', 'pid=,command='], text=True).splitlines():
    fields=line.strip().split(None, 1)
    if len(fields)==2 and (fields[1]==sys.argv[1] or fields[1].startswith(sys.argv[1]+' ')):
        os.kill(int(fields[0]), signal.SIGTERM)
PY
STAGE="$(mktemp -d "$OUTPUT_DIR/.convertdesk-build.XXXXXX")"
STAGED_APP="$STAGE/ConvertDesk.app"
APP_CONTENTS="$STAGED_APP/Contents"
PUBLISH="$APP_CONTENTS/MacOS"
RESOURCES="$APP_CONTENTS/Resources"
mkdir -p "$PUBLISH" "$RESOURCES"
for PROJECT in Filee.App Filee.Cli; do
  "$DOTNET_BIN" publish "$ROOT_DIR/src/$PROJECT/$PROJECT.csproj" -c Release -r "$RUNTIME" --self-contained true \
    -p:Version="$VERSION" -p:FileeRelease=true -p:PublishReadyToRun=false -p:ConvertDeskDocumentsOnly=true \
    --artifacts-path "$ROOT_DIR/artifacts/documents-build" -o "$PUBLISH"
done
# .NET apphosts embed the managed assembly name, so this preserves Filee.dll/resource identity.
mv "$PUBLISH/Filee" "$PUBLISH/ConvertDesk"
chmod +x "$PUBLISH/ConvertDesk" "$PUBLISH/filee-cli"
for ENGINE in rhwp pandoc; do
  if [[ "$RUNTIME" == "$NATIVE_RUNTIME" && -d "$ROOT_DIR/engines/$ENGINE" ]]; then
    mkdir -p "$PUBLISH/engines"
    cp -R "$ROOT_DIR/engines/$ENGINE" "$PUBLISH/engines/"
  else
    python3 "$ROOT_DIR/script/fetch_document_engine.py" --runtime "$RUNTIME" --engine "$ENGINE" --destination "$PUBLISH/engines"
  fi
done
python3 - "$ROOT_DIR/installer/macos/Info.plist" "$APP_CONTENTS/Info.plist" "$VERSION" <<'PY'
from pathlib import Path
import sys
Path(sys.argv[2]).write_text(Path(sys.argv[1]).read_text().replace('__VERSION__',sys.argv[3]).replace('__BUNDLE_VERSION__',sys.argv[3]))
PY
plutil -lint "$APP_CONTENTS/Info.plist"
cp "$ROOT_DIR/LICENSE" "$ROOT_DIR/THIRD-PARTY-NOTICES.md" "$RESOURCES/"
[[ ! -d "$ROOT_DIR/licenses" ]] || cp -R "$ROOT_DIR/licenses" "$RESOURCES/"
cp "$ROOT_DIR/README.md" "$ROOT_DIR/README.zh-CN.md" "$ROOT_DIR/README.upstream.md" "$ROOT_DIR/README.zh-CN.upstream.md" "$RESOURCES/"
mkdir -p "$RESOURCES/docs"
cp "$ROOT_DIR/docs/CONVERTDESK.md" "$ROOT_DIR/docs/REFACTOR-0.2.md" "$RESOURCES/docs/"
cp "$ROOT_DIR/installer/macos/INSTALL.txt" "$RESOURCES/使用说明.txt"
for LOCALIZATION in "$ROOT_DIR"/installer/macos/*.lproj; do cp -R "$LOCALIZATION" "$RESOURCES/"; done
ICONSET="$STAGE/convertdesk.iconset"
mkdir -p "$ICONSET"
for SIZE in 16 32 128 256 512; do
  sips -z "$SIZE" "$SIZE" "$ROOT_DIR/src/Filee.App/Assets/Icons/convertdesk.png" --out "$ICONSET/icon_${SIZE}x${SIZE}.png" >/dev/null
  sips -z "$((SIZE*2))" "$((SIZE*2))" "$ROOT_DIR/src/Filee.App/Assets/Icons/convertdesk.png" --out "$ICONSET/icon_${SIZE}x${SIZE}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$RESOURCES/convertdesk.icns"
# Contents/MacOS is a code directory. Apple also requires signatures on managed
# assemblies and data there (stored in extended attributes); preserve them with ditto.
python3 - "$PUBLISH" "$ROOT_DIR/installer/macos/entitlements.plist" "$SIGN_IDENTITY" <<'PY'
from pathlib import Path
import subprocess, sys
folder=Path(sys.argv[1]); entitlements=sys.argv[2]; identity=sys.argv[3]
launcher=folder/'ConvertDesk'
paths=sorted((p for p in folder.rglob('*') if p.is_file() and not p.is_symlink() and p!=launcher), key=lambda p:len(str(p)), reverse=True)+[launcher]
for path in paths:
    options=['codesign','--force','--sign',identity,'--entitlements',entitlements]
    if identity!='-': options+=['--timestamp','--options','runtime']
    subprocess.run(options+[str(path)], check=True)
PY
SIGN_OPTIONS=(--force --sign "$SIGN_IDENTITY" --entitlements "$ROOT_DIR/installer/macos/entitlements.plist")
[[ "$SIGN_IDENTITY" == - ]] || SIGN_OPTIONS+=(--timestamp --options runtime)
codesign "${SIGN_OPTIONS[@]}" "$STAGED_APP"
codesign --verify --deep --strict "$STAGED_APP"
if [[ -e "$APP_BUNDLE" ]]; then mv "$APP_BUNDLE" "$STAGE/previous-ConvertDesk.app"; fi
mv "$STAGED_APP" "$APP_BUNDLE"
if $MAKE_DMG; then
  DMG_STAGE="$STAGE/dmg"
  mkdir -p "$DMG_STAGE"
  ditto "$APP_BUNDLE" "$DMG_STAGE/ConvertDesk.app"
  ln -s /Applications "$DMG_STAGE/Applications"
  cp "$ROOT_DIR/LICENSE" "$ROOT_DIR/THIRD-PARTY-NOTICES.md" "$DMG_STAGE/"
  cp "$ROOT_DIR/installer/macos/INSTALL.txt" "$DMG_STAGE/使用说明.txt"
  [[ ! -d "$ROOT_DIR/licenses" ]] || cp -R "$ROOT_DIR/licenses" "$DMG_STAGE/"
  DMG="$OUTPUT_DIR/ConvertDesk-$VERSION-$RUNTIME.dmg"
  [[ ! -e "$DMG" ]] || mv "$DMG" "$STAGE/previous.dmg"
  hdiutil create -volname ConvertDesk -srcfolder "$DMG_STAGE" -fs HFS+ -format UDZO "$DMG" >/dev/null
  (cd "$OUTPUT_DIR" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
fi
printf 'Built: %s\n' "$APP_BUNDLE"
[[ "$SIGN_IDENTITY" != - ]] || echo 'Local ad-hoc signed build; no Apple notarization is claimed.'
[[ "$MODE" != --build-only ]] || exit 0
/usr/bin/open -n "$APP_BUNDLE"
case "$MODE" in
  --debug) sleep 2; lldb -n ConvertDesk ;;
  --logs|--telemetry) /usr/bin/log stream --info --style compact --predicate 'process == "ConvertDesk"' ;;
  --verify) sleep 3; pgrep -x ConvertDesk >/dev/null; echo 'ConvertDesk process is running. GUI/conversion validation is separate.' ;;
esac
