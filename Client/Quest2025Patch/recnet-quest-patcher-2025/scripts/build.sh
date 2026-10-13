#!/usr/bin/env bash
#
# One-shot: pure store APK -> signed, patched RecFlare APK for distribution.
#   decode + rename + LAUNCHER   (prepare-project.sh)
#   compile hook + inject        (build-hook.sh)
#   rebuild + sign               (build-sign.sh)
#
# Usage:  build.sh <original-store.apk>
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
APK="${1:?usage: build.sh <original-store.apk>}"

"$HERE/prepare-project.sh" "$APK"
"$HERE/build-hook.sh"
"$HERE/build-sign.sh"
