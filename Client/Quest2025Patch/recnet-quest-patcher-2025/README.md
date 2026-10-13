# RecFlare Quest

A native hook library that points a sideloaded **Rec Room (Meta Quest)** client at a
private RecNet server, for **game-preservation** purposes. It is the Quest counterpart
to the PC BepInEx plugin ([`recnet-plugin`](../recnet-plugin)) — same effect, but on
Android/IL2CPP there is no BepInEx, so everything is done with **inline hooks in one
injected native library** (`libvrsupport.so`).

> ⚠️ This disables client-side certificate validation. It ships **no** copyrighted Rec Room
> assets — you supply your own extracted APK/OBB. Don't redistribute game binaries.

This README is the build and install guide. For how the patches work internally — the hook
targets, the RVA table, certificate/anti-cheat details, and how to rebase onto a new game
build — see [`CLAUDE.md`](CLAUDE.md).

In short, `scripts/build.sh` takes a pristine store APK, decodes it, renames the package,
compiles `src/recflare_hook.cpp` into `libvrsupport.so`, adds a `DT_NEEDED` entry on
`libmain.so` so the loader pulls it in, and rebuilds + signs the result. Your configuration
is baked into the library at compile time.

## Prerequisites

- **Android NDK r27c** (`ANDROID_NDK`). zig can't provide bionic libc; the real NDK is needed.
- **python3 + [LIEF](https://lief.re)** (`pip install lief`) for the DT_NEEDED injection.
- **A `tools/` dir** (git-ignored; point at it with `TOOLS`, default `<repo>/tools`)
  containing `apktool.jar`, `zipalign`, `lib/apksigner.jar`, and `debug.keystore`
  (alias `recflare`, pass `android`). Signing must be **consistent**: the Oculus
  platform ties tokens to your app id + package name + signing cert, so keep the same
  key and register that package/cert with your Oculus app. **Back this keystore up** —
  it's deliberately not committed, and losing it breaks the Oculus registration.
- **Your own pristine Rec Room store APK** (`com.AgainstGravity.RecRoom`, versionCode
  `2168575`) and the matching **OBB**. These are **not** in this repo — they're
  copyrighted game assets you extract yourself.

## Configure

Copy `recflare.cfg.example` to `recflare.cfg` in the repo root and edit it. `build-hook.sh`
compiles those values into `libvrsupport.so` via a generated `config_generated.h`, so the
config is **baked into the APK at build time**. Keys you omit fall back to compiled-in
defaults; the Photon app ids and the Oculus App ID are per-deployment credentials with **no
default** — supply your own or the corresponding patch does nothing.

| Key | What it does |
| --- | --- |
| `Package Name` | default `net.recflare.RecFlare`; also drives the manifest rename, so the sideloaded client coexists with the store app |
| `RecNet NameServer Host` | where `ns.rec.net` is redirected; the nameserver response supplies every other service URL |
| `Oculus App Id` | your registered Meta app id — **or blank**, which selects the no-Meta-account build (below) |
| `App Id Realtime` / `App Id Voice` / `App Id Chat` | Photon app ids from your own Photon dashboard; leave one blank and the client keeps Rec Room's dead app, so multiplayer won't connect |
| `Photon NameServer` / `Photon NameServer Port` | custom Photon nameserver (blank / `0` = Photon Cloud) |

The same file can optionally be pushed to `/sdcard/Android/data/<package>/files/recflare.cfg`
to override settings at runtime without a rebuild.

### `Oculus App Id` — which build you get

This one key selects between two builds:

- **Set** — the *Meta build*. The client initialises against your own registered Meta app and
  Meta's real entitlement and login flow runs end to end. Players need a Meta account, and
  the package + signing cert must be registered with that app.
- **Blank** — the *no-Meta-account build*, for players without a Meta account. Platform hooks
  steer the client past Meta's entitlement and login checks to its own username/password
  screen. They authenticate nothing; **your server must exclude platform id `1` from platform
  authentication**, or every no-Meta player lands in one shared account. Rationale and the
  healthy log shape are in [`CLAUDE.md`](CLAUDE.md) → *Platform authentication*.

### HTTP tracing

Four `[Advanced]` keys, same names as the PC plugin, so a PC config can be reused:

| Key | Default | Effect |
| --- | --- | --- |
| `Log HTTP` | `true` | one line per request and per response; **non-2xx bodies always shown** |
| `Debug` | `false` | full tracing — request bodies and *every* response body |
| `Log HTTP Body Max` | `10000` | per-body cap; the excess is noted, never silently dropped |
| `Log Noisy Endpoints` | `false` | include telemetry/heartbeat traffic (gamesight, heartbeat, identify, httpapi, data/event) |

```
[http]   POST https://auth.recflare.net/connect/token
[http]     body=grant_type=create_account&client_id=recroom&…
[http<-] 400 https://auth.recflare.net/connect/token
[http<-]   body={"error":"invalid_grant","error_description":"…"}
```

> ⚠️ `Debug = true` logs **auth tokens, client secrets and passwords**. Careful sharing.

## Build

One command turns a pristine store APK into a signed, distributable patched APK:

```sh
export ANDROID_NDK=~/x/ndk/android-ndk-r27c
cp recflare.cfg.example recflare.cfg     # then edit your server / app ids
scripts/build.sh /path/to/RecRoom-store.apk
# -> build/out.apk  (config baked in)
```

`build.sh` chains three steps you can also run individually:

```sh
scripts/prepare-project.sh RecRoom-store.apk   # apktool decode + package rename + LAUNCHER
scripts/build-hook.sh                          # compile libvrsupport.so + inject into libmain.so
scripts/build-sign.sh                          # rebuild + arsc-fix + zipalign + sign -> build/out.apk
```

The intermediate project and signed APK land under `build/` (git-ignored). Because
`prepare-project.sh` decodes fresh each time, `build-hook.sh` always injects into a
**pristine** `libmain.so` — no stale-injection footguns.

`build-sign.sh` re-stores `resources.arsc` uncompressed (Android 11+ requires it stored
+ 4-byte aligned) and signs with an apksigner v2/v3 signature — both are needed or the
install fails (`Failure [-124]` / `NO_CERTIFICATES`). The package rename (manifest
package + C2D permission + Firebase authority) lets it coexist with the store app and
avoids `INSTALL_FAILED_DUPLICATE_PERMISSION`.

### Building for the Meta store

Meta's uploader rejects the raw sideload build three ways; `prepare-project.sh` handles all
of them:

| Rejection | Fix | Default |
| --- | --- | --- |
| "The VRAPI library is not supported for applications created after 06-19-2024" | `OPENXR=1` → [`openxr-swap.sh`](scripts/openxr-swap.sh) | on |
| "Android Target SDK version is 31, but should be 32 or greater" | `TARGET_SDK=32` | on |
| a versionCode already uploaded | `VERSION_CODE=<higher>` | keeps the APK's |

```sh
OPENXR=1 TARGET_SDK=32 VERSION_CODE=2168576 VERSION_NAME=2168575.1 \
  scripts/build.sh RecRoom-store.apk
```

`OPENXR=0` keeps the original VrApi stack (fine for sideloading, rejected by Meta); the swap
itself is described in [`CLAUDE.md`](CLAUDE.md) → *The OpenXR swap*. `versionName` is
free-form; `versionCode` is an **integer**, so a value like `2168575.1` can only be the name.

> ⚠️ **Bumping `versionCode` invalidates the OBB.** Unity rebuilds
> `main.<versionCode>.<package>.obb` at runtime, so a `2168576` APK looks for a `2168576`
> OBB and boots with no assets otherwise. Rename (or copy — copying leaves the previous
> build bootable as a fallback) the OBB to match, both on-device and for the file you upload
> to Meta.

`sdkInfo` and `versionInfo` live in **`apktool.yml`**, not the decoded manifest — apktool
strips `<uses-sdk>` and the version attributes and re-emits them at build time, so that file
is the only place they can be set.

## Install / distribute

`build/out.apk` is the deliverable. It needs the matching **OBB** (unchanged from the
store build) placed on the device at (`<package>` = your configured Package Name):

```
/sdcard/Android/obb/<package>/main.<versionCode>.<package>.obb
```

Install the APK, then push the OBB with adb:

```sh
PKG=net.recflare.RecFlare      # your Package Name
VC=2168575                     # versionCode
OBB=/path/to/main.2168575.com.............obb   # your source OBB

adb install -r build/out.apk
adb shell mkdir -p /sdcard/Android/obb/$PKG
adb push "$OBB" /sdcard/Android/obb/$PKG/main.$VC.$PKG.obb
```

The **on-device filename must be `main.<versionCode>.<package>.obb`** — Unity rebuilds
that exact path at runtime from the app's package name + versionCode, so the source
file's own name doesn't matter, only the destination does. (SideQuest and other
sideload tools handle this same placement for you.) The optional runtime config goes at
`/sdcard/Android/data/<package>/files/recflare.cfg`.

## Checking a build

**The headset must be worn** — off-head it sleeps within seconds and the app never gets past
init, which looks exactly like a startup crash.

```sh
adb logcat -c && adb shell am start -n net.recflare.RecFlare/com.unity3d.player.UnityPlayerActivity
adb logcat -d -s RECFLARE
```

A healthy boot logs every hook `armed`, then `intercepted https://ns.rec.net/ -> …`,
`unitytls verify: forcing 0x8 -> SUCCESS`, `cert pinning: accepting chain`, `[http<-] 200`,
and `photon appsettings: RT=… Voice=… Chat=…`. The logcat buffer holds days of history, so
running `-d` after the fact is fine.

Common failures:

| Symptom | Cause |
| --- | --- |
| install fails `-124` / `NO_CERTIFICATES` | signing/arsc step — rebuild via `build-sign.sh`, don't hand-zip the APK |
| boots to an empty world / no assets | OBB missing or named for the wrong versionCode |
| login 403 with nothing in your server logs | Cloudflare edge (bot-fight/WAF) blocked it before the origin — check Security events; the response log prints the challenge page |
| client quits itself, no error | an anti-cheat detection — see [`CLAUDE.md`](CLAUDE.md) → *Diagnose a silent quit* |

## Layout

| Path | Purpose |
| --- | --- |
| `src/recflare_hook.cpp` | all the hooks + config loader |
| `src/And64InlineHook.*` | vendored arm64 inline hooker (MIT, © Rprop) |
| `scripts/build.sh` | one-shot: store APK → signed patched APK |
| `scripts/prepare-project.sh` | apktool decode + package rename + LAUNCHER + SDK/version overrides |
| `scripts/openxr-swap.sh` | VrApi → OpenXR lib swap, so Meta's uploader accepts the binary |
| `scripts/build-hook.sh` | bake config + compile libvrsupport.so + inject into libmain.so |
| `scripts/gen-config.py` | recflare.cfg → `config_generated.h` (baked at build time) |
| `scripts/cfg-get.py` | read one recflare.cfg key (used by the shell scripts) |
| `scripts/inject.py` | LIEF DT_NEEDED injection |
| `scripts/build-sign.sh` | apktool rebuild → arsc fix → zipalign → apksigner → build/out.apk |
| `scripts/xref.py` | static caller/callee finder over `libil2cpp.so` + `dump.cs` (see `CLAUDE.md`) |
| `scripts/patch_metadata.py`, `list_literals.py` | il2cpp v27 metadata helpers (the metadata-patch route is a **dead end** for the server redirect — kept for reference) |
| `recflare.cfg.example` | config template (PC-plugin key names); copy to `recflare.cfg`, baked in at build |

## Credits

- 2023 APKs and OBBs: fusion6624 on Discord
- PC ground truth: [`recnet-plugin`](../recnet-plugin).
- [And64InlineHook](https://github.com/Rprop/And64InlineHook) © Rprop (MIT).
- [Il2CppDumper](https://github.com/Perfare/Il2CppDumper), [LIEF](https://lief.re).
