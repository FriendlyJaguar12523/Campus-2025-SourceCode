#!/usr/bin/env bash
#
# Compile the native hook library (libvrsupport.so) and inject it as the first
# DT_NEEDED of the project's libmain.so. Run after prepare-project.sh, which leaves a
# pristine libmain.so straight from apktool decode.
#
# Usage:  build-hook.sh [project-dir]
#
# Env:
#   ANDROID_NDK   NDK root       (default: ~/x/ndk/android-ndk-r27c)
#   PROJECT       project dir    (default: <repo>/build/project)
#   CONFIG        recflare.cfg to bake in  (default: <repo>/recflare.cfg if present)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRC="$REPO/src"

ANDROID_NDK="${ANDROID_NDK:-$HOME/x/ndk/android-ndk-r27c}"
PROJECT="${1:-${PROJECT:-$REPO/build/project}}"
CONFIG="${CONFIG:-$REPO/recflare.cfg}"
ABI="$PROJECT/lib/arm64-v8a"

CLANG="$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android29-clang++"
[ -x "$CLANG" ]          || { echo "NDK clang not found at $CLANG -- set ANDROID_NDK"; exit 1; }
[ -f "$ABI/libmain.so" ] || { echo "no libmain.so in $ABI -- run prepare-project.sh first"; exit 1; }

# Bake recflare.cfg into the .so (config_generated.h on the include path). Absent keys
# fall back to the compiled-in defaults; absent file -> all defaults.
GEN_DIR="$REPO/build/gen"
INCLUDE_FLAGS=()
if [ -f "$CONFIG" ]; then
  mkdir -p "$GEN_DIR"
  python3 "$HERE/gen-config.py" "$CONFIG" "$GEN_DIR/config_generated.h"
  INCLUDE_FLAGS=(-I "$GEN_DIR")
  echo ">> baking config from $CONFIG"
else
  echo ">> no $CONFIG -- using built-in defaults (copy recflare.cfg.example -> recflare.cfg)"
fi

echo ">> compile libvrsupport.so"
"$CLANG" -shared -fPIC -O2 -fno-exceptions -fno-rtti -static-libstdc++ \
  "${INCLUDE_FLAGS[@]}" \
  -o "$ABI/libvrsupport.so" \
  "$SRC/recflare_hook.cpp" "$SRC/And64InlineHook.cpp" -llog

echo ">> inject libvrsupport.so as first DT_NEEDED of libmain.so"
if readelf -d "$ABI/libmain.so" 2>/dev/null | grep -q 'libvrsupport.so'; then
  echo "   libmain.so already references libvrsupport.so -- re-run prepare-project.sh"
  echo "   for a clean base (avoids stacking injections)." >&2
else
  python3 "$HERE/inject.py" "$ABI/libmain.so" "$ABI/libmain.so.new"
  mv "$ABI/libmain.so.new" "$ABI/libmain.so"
fi

echo ">> done. next: scripts/build-sign.sh"
