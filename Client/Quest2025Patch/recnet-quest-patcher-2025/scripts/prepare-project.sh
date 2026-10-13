#!/usr/bin/env bash
#
# Decode a pristine Rec Room store APK into a working project: apktool decode, rename
# the manifest package (so it coexists with the store app and avoids duplicate
# permission/authority collisions), and add the LAUNCHER category so it shows in the
# Quest library. Leaves lib/arm64-v8a/libmain.so pristine for build-hook.sh to inject.
#
# Usage:  prepare-project.sh <original-store.apk> [project-dir]
#
# Env:
#   TOOLS        dir with apktool.jar        (default: <repo>/tools)
#   PROJECT      output project dir          (default: <repo>/build/project)
#   CONFIG       recflare.cfg                (default: <repo>/recflare.cfg)
#   OLD_PKG      original package            (com.AgainstGravity.RecRoom)
#   NEW_PKG      renamed package             (recflare.cfg "Package Name", else net.recflare.RecFlare)
#   TARGET_SDK   targetSdkVersion to force   (default: 32)
#   VERSION_NAME versionName to force        (default: keep the APK's)
#   VERSION_CODE versionCode to force        (default: keep the APK's -- see the warning below)
#   OPENXR       1 to swap VrApi -> OpenXR   (default: 1; see openxr-swap.sh)
#   DEBUGGABLE   1 to set android:debuggable (default: 0)
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"

APK="${1:?usage: prepare-project.sh <original-store.apk> [project-dir]}"
PROJECT="${2:-${PROJECT:-$REPO/build/project}}"
TOOLS="${TOOLS:-$REPO/tools}"
CONFIG="${CONFIG:-$REPO/recflare.cfg}"
OLD_PKG="${OLD_PKG:-com.AgainstGravity.RecRoom}"
# Package Name from recflare.cfg wins; else NEW_PKG env; else the default.
NEW_PKG="${NEW_PKG:-$(python3 "$HERE/cfg-get.py" "$CONFIG" "Package Name")}"
NEW_PKG="${NEW_PKG:-net.recflare.RecFlare}"
# Meta rejects binary uploads below API 32 ("Android Target SDK version is 31, but should
# be 32 or greater"). 32 is the smallest bump that clears it; going higher is a behaviour
# change for a 2020.3 Unity app (33 = notification permission + storage split), so don't
# raise this default without testing on-device.
TARGET_SDK="${TARGET_SDK:-32}"
# versionName is a free-form label (Meta shows it as the build's version); versionCode is
# the integer the store orders uploads by. Both default to whatever the store APK carried.
VERSION_NAME="${VERSION_NAME:-}"
VERSION_CODE="${VERSION_CODE:-}"
# Meta also rejects any binary containing libvrapi.so. Verified on-device 2026-08-02, so
# it is on by default; OPENXR=0 builds the original VrApi stack (which Meta will reject).
OPENXR="${OPENXR:-1}"
DEBUGGABLE="${DEBUGGABLE:-0}"

[ -f "$APK" ]                || { echo "APK not found: $APK"; exit 1; }
[ -f "$TOOLS/apktool.jar" ]  || { echo "apktool.jar not found in $TOOLS"; exit 1; }

echo ">> decoding $APK -> $PROJECT"
rm -rf "$PROJECT"
mkdir -p "$(dirname "$PROJECT")"
java -jar "$TOOLS/apktool.jar" decode -f -o "$PROJECT" "$APK"

M="$PROJECT/AndroidManifest.xml"
Y="$PROJECT/apktool.yml"

# apktool keeps <uses-sdk> out of the decoded manifest and in apktool.yml's sdkInfo,
# re-emitting it at build time -- so this is the only place the value can be changed.
echo ">> set targetSdkVersion -> $TARGET_SDK (was $(awk -F': *' '/targetSdkVersion:/{print $2}' "$Y"))"
grep -q 'targetSdkVersion:' "$Y" || { echo "no targetSdkVersion in $Y"; exit 1; }
sed -i -E "s/^([[:space:]]*targetSdkVersion:[[:space:]]*).*/\1$TARGET_SDK/" "$Y"
# Keep the manifest's advisory compileSdk attributes in step with it, so aapt badging and
# Meta's uploader report one consistent SDK level.
sed -i -E "s/android:compileSdkVersion=\"[0-9]+\"/android:compileSdkVersion=\"$TARGET_SDK\"/; \
           s/platformBuildVersionCode=\"[0-9]+\"/platformBuildVersionCode=\"$TARGET_SDK\"/" "$M"

# versionInfo lives in apktool.yml too, for the same reason as sdkInfo.
if [ -n "$VERSION_NAME" ]; then
  echo ">> set versionName -> $VERSION_NAME (was $(awk -F': *' '/^ *versionName:/{print $2}' "$Y"))"
  sed -i -E "s/^([[:space:]]*versionName:[[:space:]]*).*/\1$VERSION_NAME/" "$Y"
fi
if [ -n "$VERSION_CODE" ]; then
  # The OBB's on-device filename is main.<versionCode>.<package>.obb and Unity rebuilds that
  # path at runtime, so changing versionCode means renaming the OBB on every device or the
  # game boots with no assets. Only change it when you also re-place the OBB.
  echo ">> set versionCode -> $VERSION_CODE (was $(awk -F': *' '/^ *versionCode:/{print $2}' "$Y"))"
  echo "   WARNING: the OBB must be renamed to main.$VERSION_CODE.$NEW_PKG.obb on the device"
  sed -i -E "s/^([[:space:]]*versionCode:[[:space:]]*).*/\1$VERSION_CODE/" "$Y"
fi

echo ">> rename package $OLD_PKG -> $NEW_PKG (package attr, C2D permission, authorities)"
sed -i "s/${OLD_PKG//./\\.}/${NEW_PKG}/g" "$M"

echo ">> add LAUNCHER category (so it launches from the Quest library)"
if ! grep -q 'android.intent.category.LAUNCHER' "$M"; then
  if grep -q 'android.intent.category.INFO' "$M"; then
    sed -i 's#\(<category android:name="android.intent.category.INFO"/>\)#\1\n                <category android:name="android.intent.category.LAUNCHER"/>#' "$M"
  else
    sed -i 's#\(<action android:name="android.intent.action.MAIN"/>\)#\1\n                <category android:name="android.intent.category.LAUNCHER"/>#' "$M"
  fi
fi

if [ "$OPENXR" = "1" ]; then
  # Meta's uploader rejects any binary containing libvrapi.so. See openxr-swap.sh.
  "$HERE/openxr-swap.sh" "$PROJECT"
else
  echo ">> OPENXR=0: keeping the original VrApi libs (Meta will reject this upload)"
fi

if [ "$DEBUGGABLE" = "1" ] && ! grep -q 'android:debuggable' "$M"; then
  echo ">> set android:debuggable=true"
  sed -i 's/<application /<application android:debuggable="true" /' "$M"
fi

echo ">> package now: $(grep -oE 'package="[^"]*"' "$M")"
echo ">> project ready at $PROJECT"
echo "   next: scripts/build-hook.sh"
