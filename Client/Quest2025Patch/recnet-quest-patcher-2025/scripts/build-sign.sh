#!/usr/bin/env bash
#
# Rebuild the prepared project into an installable, signed APK for distribution.
#
# Why each step exists (all three bit us once):
#   1. apktool output is UNSIGNED            -> INSTALL_PARSE_FAILED_NO_CERTIFICATES
#   2. apktool COMPRESSES resources.arsc     -> Failure [-124] (R+ needs it stored+aligned)
#   3. targetSdk>=30 needs a v2/v3 signature -> jarsigner (v1 only) is not enough
#
# Usage:  build-sign.sh
#
# Env:
#   TOOLS      dir with apktool.jar, zipalign, lib/apksigner.jar, debug.keystore
#   PROJECT    prepared project dir  (default: <repo>/build/project)
#   OUT        output apk path       (default: <repo>/build/out.apk)
#   CONFIG     recflare.cfg          (default: <repo>/recflare.cfg; for the OBB name)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

TOOLS="${TOOLS:-$REPO/tools}"
PROJECT="${PROJECT:-$REPO/build/project}"
OUT="${OUT:-$REPO/build/out.apk}"
CONFIG="${CONFIG:-$REPO/recflare.cfg}"

VC="$(awk -F': *' '/^ *versionCode:/{print $2}' "$PROJECT/apktool.yml")"
[ -n "$VC" ] || { echo "could not read versionCode from $PROJECT/apktool.yml"; exit 1; }

# Package (for the OBB filename hint): the built project's manifest is authoritative.
PKG="$(grep -oE 'package="[^"]*"' "$PROJECT/AndroidManifest.xml" | head -1 | sed 's/.*"\(.*\)"/\1/')"
PKG="${PKG:-net.recflare.RecFlare}"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
REBUILT="$WORK/rebuilt.apk"
ALIGNED="$WORK/aligned.apk"

echo ">> [1/4] apktool build"
java -jar "$TOOLS/apktool.jar" build "$PROJECT" -o "$REBUILT"

echo ">> [2/4] re-store resources.arsc uncompressed (R+ requirement)"
( cd "$WORK" && unzip -oq "$REBUILT" resources.arsc )
zip -dq "$REBUILT" resources.arsc
( cd "$WORK" && zip -X -0 -q "$REBUILT" resources.arsc )

echo ">> [3/4] zipalign (4-byte, page-align .so)"
"$TOOLS/zipalign" -p -f 4 "$REBUILT" "$ALIGNED"

echo ">> [4/4] apksigner (v2+v3) with tools/debug.keystore"
mkdir -p "$(dirname "$OUT")"
java -jar "$TOOLS/lib/apksigner.jar" sign \
  --ks "$TOOLS/debug.keystore" --ks-key-alias recflare \
  --ks-pass pass:android --key-pass pass:android \
  --out "$OUT" "$ALIGNED"
java -jar "$TOOLS/lib/apksigner.jar" verify --print-certs "$OUT" >/dev/null && echo "   signature OK"

echo ">> built: $OUT  (versionCode $VC)"
echo "   distribute this APK alongside the OBB: main.$VC.$PKG.obb"
