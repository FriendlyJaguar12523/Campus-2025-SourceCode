#!/usr/bin/env bash
#
# Migrate a prepared project off VrApi and onto OpenXR, so Meta's uploader accepts the
# binary ("The VRAPI library is not supported for applications created after 06-19-2024").
#
# Why this is a drop-in swap and not a port:
#   The game ships Unity's Oculus XR Plugin 1.12.1, whose libOVRPlugin.so is the VrApi build
#   of OVRPlugin 1.66.0 (ovr-version ovrplugin-android-universal:34.0.0.72.185) and links
#   libvrapi.so. Unity's com.unity.xr.oculus 3.0.2 package ships the *same* OVRPlugin build
#   compiled against OpenXR (ovrplugin-android-openxr:34.0.0.72.185), linking
#   libopenxr_loader.so instead. It exports a strict superset of the VrApi build's ovrp_*
#   symbols (477 vs 469, none dropped), so libOculusXRPlugin.so and the game's baked-in
#   OVRPlugin.cs P/Invokes keep resolving against it.
#
#   libOVRPlugin.so (vrapi) -> libOVRPlugin.so (openxr)
#   libvrapi.so             -> deleted
#                           +  libopenxr_loader.so
#   manifest                +  IMMERSIVE_HMD category, <queries> for the runtime broker
#
# libOVRPlugin.so's DT_NEEDED is the APK's only reference to vrapi (checked: smali, il2cpp,
# every other .so), so nothing else has to move. libOculusXRPlugin.so is deliberately NOT
# swapped: 3.0.2's copy targets Unity 2021.2+'s XR SDK interface, and the game is 2020.3.33f1
# with Unity.XR.Oculus.dll already compiled into libil2cpp.so against the 1.12.1 API.
#
# This is still a rendering-backend change to a shipped game: verify on-device
# (`logcat -s RECFLARE OculusXRPlugin Unity`) before distributing. To build the original
# VrApi stack instead, run prepare-project.sh with OPENXR=0.
#
# Usage:  openxr-swap.sh [project-dir]
#
# Env:
#   TOOLS      dir holding the cached Unity package  (default: <repo>/tools)
#   PROJECT    prepared project dir                  (default: <repo>/build/project)
#   XR_PKG     path to com.unity.xr.oculus-3.0.2.tgz (default: $TOOLS/<that name>, downloaded
#              from packages.unity.com on first run)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

PROJECT="${1:-${PROJECT:-$REPO/build/project}}"
TOOLS="${TOOLS:-$REPO/tools}"

PKG_VER="3.0.2"
PKG_NAME="com.unity.xr.oculus-$PKG_VER.tgz"
PKG_URL="https://packages.unity.com/com.unity.xr.oculus/-/$PKG_NAME"
XR_PKG="${XR_PKG:-$TOOLS/$PKG_NAME}"

# The two binaries lifted out of the package, pinned so a re-published tarball or a bad
# download can't silently put unknown code into a distributed APK.
SHA_OVRPLUGIN="caf79bedfff6d3a7ade53014dcf80007e25768d9aa023542a290bfa2e69ea49e"
SHA_LOADER="d159159c3b47caefba5e630c27d3cf765a5cc207f9e21b47e2554d8425989eba"

# The OVRPlugin build the swap-in is known to be ABI-compatible with. If a game update ships
# a different one, re-check the symbol diff before trusting the pinned 3.0.2 binaries.
EXPECT_OVR_VERSION="ovrplugin-android-universal:34.0.0.72.185"

LIBS="$PROJECT/lib/arm64-v8a"
M="$PROJECT/AndroidManifest.xml"

[ -d "$LIBS" ] || { echo "no $LIBS -- run prepare-project.sh first"; exit 1; }
[ -f "$M" ]    || { echo "no $M -- run prepare-project.sh first"; exit 1; }

if [ ! -f "$LIBS/libvrapi.so" ] && [ -f "$LIBS/libopenxr_loader.so" ]; then
  echo ">> already on OpenXR; nothing to do"
  exit 0
fi

echo ">> checking the game's OVRPlugin build"
if ! strings -a "$LIBS/libOVRPlugin.so" | grep -q "$EXPECT_OVR_VERSION"; then
  echo "libOVRPlugin.so is not $EXPECT_OVR_VERSION."
  echo "found: $(strings -a "$LIBS/libOVRPlugin.so" | grep -o 'ovr-version:[^ ]*' | head -1)"
  echo "The pinned OpenXR binaries match the version above only -- re-verify the ovrp_ symbol"
  echo "diff against a matching com.unity.xr.oculus release before changing EXPECT_OVR_VERSION."
  exit 1
fi

if [ ! -f "$XR_PKG" ]; then
  echo ">> fetching $PKG_NAME from packages.unity.com"
  mkdir -p "$(dirname "$XR_PKG")"
  curl -fL --retry 3 -o "$XR_PKG.part" "$PKG_URL"
  mv "$XR_PKG.part" "$XR_PKG"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo ">> extracting OpenXR OVRPlugin from $(basename "$XR_PKG")"
tar xzf "$XR_PKG" -C "$WORK" package/Runtime/Android/OVRPlugin.aar
unzip -oq "$WORK/package/Runtime/Android/OVRPlugin.aar" \
  'jni/arm64-v8a/libOVRPlugin.so' 'jni/arm64-v8a/libopenxr_loader.so' -d "$WORK"
SRC="$WORK/jni/arm64-v8a"

echo "$SHA_OVRPLUGIN  $SRC/libOVRPlugin.so"     | sha256sum -c - >/dev/null
echo "$SHA_LOADER  $SRC/libopenxr_loader.so"    | sha256sum -c - >/dev/null
echo "   checksums OK"

echo ">> swapping libs in $LIBS"
install -m 644 "$SRC/libOVRPlugin.so"      "$LIBS/libOVRPlugin.so"
install -m 644 "$SRC/libopenxr_loader.so"  "$LIBS/libopenxr_loader.so"
rm -f "$LIBS/libvrapi.so"
echo "   libOVRPlugin.so -> $(strings -a "$LIBS/libOVRPlugin.so" | grep -o 'ovr-version:[^ ]*' | head -1)"
echo "   + libopenxr_loader.so, - libvrapi.so"

echo ">> manifest: OpenXR intent category + runtime-broker package visibility"
if ! grep -q 'org.khronos.openxr.intent.category.IMMERSIVE_HMD' "$M"; then
  sed -i 's#\(<category android:name="com.oculus.intent.category.VR"/>\)#\1\n                <category android:name="org.khronos.openxr.intent.category.IMMERSIVE_HMD"/>#' "$M"
fi
# targetSdk >= 30 hides other packages by default, and the loader resolves the active runtime
# through com.oculus.systemdriver.
if ! grep -q '<queries>' "$M"; then
  sed -i 's#\(<application \)#<queries>\n        <package android:name="com.oculus.systemdriver"/>\n    </queries>\n    \1#' "$M"
fi

echo ">> OpenXR swap done"
