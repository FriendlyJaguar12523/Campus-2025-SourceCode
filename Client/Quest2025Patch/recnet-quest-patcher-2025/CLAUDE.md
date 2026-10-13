# CLAUDE.md

Guidance for working in this repo. `README.md` is the user-facing **build and install guide**;
this file is everything about *how and why the patches work*, plus the stuff you only learn by
getting burned. **Read this before rebasing onto a new game build.**

This is the Quest half of RecFlare — a native hook library (`libvrsupport.so`) injected into a
sideloaded Rec Room APK via a `DT_NEEDED` entry on `libmain.so`, pointing the client at a
self-hosted RecNet server. The PC half is the sibling BepInEx plugin at `../recnet-plugin`.
Everything lives in one file, `src/recflare_hook.cpp`.

## Ground rules

- **Work on `master`.** History is linear and `master` is the current line of work — the
  20230414 (versionCode 2168575) client. The other refs are markers, not parallel work:
  `working-20230302` is `4f0c7e7`, the last commit targeting the 20230302 / 2106701 build, and
  `20240414` is an alias for the same commit as `master`. An earlier version of this file said
  `master` past `4f0c7e7` was abandoned; those commits were dropped in a history rewrite and
  no longer exist. If you need the pre-upgrade RVAs and hooks, `git show working-20230302:…`.
- **`Oculus App Id` selects one of two builds — keep every platform hook conditional on it.**
  Set = the Meta build (real entitlement + real Meta login, no platform hooks armed). Blank =
  the no-Meta-account build, for players without a Meta account. `init_thread` arms the whole
  `libovrplatformloader.so` block only when `g_oculus_appid` is empty; never make any of it
  unconditional, or a genuine failure on the Meta build gets silently hidden. Full rationale
  and the healthy log shape below, under *Platform authentication*.
- **The no-Meta hooks must stay unusable-by-design.** They exist to make the platform path
  fail at a step the client *handles*, not to authenticate: the user id is the sentinel `1`
  and the nonce is the fixed `recflare-no-platform-account`, and the server is expected to
  reject both (RecFlare excludes platform id 1 from authentication; the healthy trace is
  `400 /connect/token` then `200 /connect/token`). A real id or nonce is always passed
  through untouched. Never "fix" this by making the values plausible, and never add a
  server-side shortcut that accepts them — every no-Meta client sends the *same* pair, so
  accepting it merges all those players into one account.
- **Don't reach for the `graph.oculus.com/me` stub.** The 20230302 build stubbed
  `OculusPlatformManager.<>c.<LoadUser>b__60_9` (RVA `0x37BBB78` on 2168575, `0x3486DC8` on
  2106701) to forge `{"id":1234567890123456}`. It is not needed — the three ovr-level hooks
  are sufficient — and unlike them it forges a *specific* account id and is RVA-bound.
- **Il2CppDumper output goes in `~/x/`, never `/tmp`.** A dump is ~650 MB and filling tmpfs
  silently wedges the shell.
- **The metadata-patch route is a dead end** for the server redirect. `patch_metadata.py` /
  `list_literals.py` are kept for reference only; hook, don't rewrite `global-metadata.dat`.

## What the patches are

All of them live in `src/recflare_hook.cpp`. A `constructor` spawns a thread that waits for
`libil2cpp.so`, then installs inline hooks (via And64InlineHook) at the RVAs below.

| Patch | Target | Effect |
| --- | --- | --- |
| Name-server redirect | `BestHTTP.HTTPManager.SendRequest(HTTPRequest)` | rewrite host `ns.rec.net` → the configured name server (the nameserver response supplies every other service URL) |
| Cert-validation bypass | `Mono.Unity.UnityTlsContext.VerifyCallback` | force `UNITYTLS_X509VERIFY_SUCCESS` |
| Cert-**pinning** bypass | `EKAKCHPEMEJ.KNDLCMMMHAK(HTTPRequest, X509Certificate, X509Chain)` | accept unconditionally — a separate check from the one above |
| Oculus App ID | `PlatformSettings.get_AppID` / `get_MobileAppID` | return the configured Oculus app id |
| Photon | `HPEENKELKDJ.MGKINLFMJLB()` → `AppSettings` | inject Photon App IDs / server (postfix, like the PC `PhotonPatches`) |
| Image signing | `GDHOAGNFOBD.IMKOEDHDCIB(byte[], byte[])` | accept unsigned images (narrower than the PC `ImageSigningPatch`) |
| Request/response logging | `SendRequest` + `HTTPRequest.CallCallback()` | log every URL, status, body and exception |
| Anti-cheat / quit tracing | `IBPKDJLFBHM.FNODNAHADDD`, `SessionManager.FatalApplicationQuit` / `TryApplicationQuit` | log-only: name the detection or reason behind a quit |
| Platform-account bypass (conditional) | `ovr_Message_IsError`, `ovr_GetLoggedInUserID`, `ovr_User_GetID`, `ovr_Message_GetUserProof`, `ovr_UserProof_GetNonce` (exported by `libovrplatformloader.so`) | steer the client to its own username/password login — **only** when no Oculus App ID is configured |

### Platform authentication — the two builds

`Oculus App Id` selects which of two builds you are making. Everything in this section is
armed or disarmed by that one key.

**App ID configured — the Meta build.** The client initialises the platform against your own
registered Meta app, and Meta's real entitlement and login flow runs end to end. None of the
hooks below are armed; a genuine entitlement or login failure stays visible.

**App ID blank — the no-Meta-account build.** For players who want to play without a Meta
account. The client falls back to *Rec Room's* app id (`2173678582678296`), which doesn't
match the package, so Meta refuses everything:

```
User is not entitled for appid 2173678582678296 (PackageNotInLibrary)
```

Left alone that is fatal twice over — the entitlement check fails closed ("You do not have
permission to play this game"), and if you only fix that, `ovr_GetLoggedInUserID` returns 0,
the client tries `graph.oculus.com/v1.20/me/sdk_accounts`, gets `190 Invalid OAuth 2.0 Access
Token`, logs "An unknown error occurred. Request failed." and then **hangs on "Connecting to
server…" forever** with no exception and no quit. So three things are needed together:

| Hook | Without it |
| --- | --- |
| entitlement message → not-an-error | "You do not have permission to play this game" |
| `GetLoggedInUserID` / `User_GetID` → `1` when the real call says 0 | silent hang on "Connecting to server…" |
| user-proof message → not-an-error, with a placeholder nonce | "GetPlatformIdProof failed: Failed to get Oculus Login" at the login screen |

Together they get the client to its own username/password screen. **None of this
authenticates anything** — see the ground rule above. The server is expected to *reject* the
sentinel pair; that rejection is what makes the client fall through to credentials. A healthy
no-Meta login looks like this:

```
[http<-] 400 https://auth.recflare.net/connect/token   ← platform id 1 refused
[http<-] 200 https://auth.recflare.net/connect/token   ← username/password accepted
```

These hooks target exported symbols rather than RVAs, so they survive a game-build rebase
untouched.

### Certificate validation

Build 2106701 bundled BestHTTP's own BouncyCastle TLS stack, and the bypass stubbed
`LegacyTlsAuthentication.NotifyServerCertificate` — the same target the PC plugin's
`DisableTLSPinning` uses. **2168575 removed that stack entirely** (`global-metadata.dat`
contains neither `LegacyTlsAuthentication` nor `BouncyCastle`), moving BestHTTP onto the
platform stack — `MonoTlsStream` / `UnityTlsContext`, i.e. mbedTLS via UnityTls.

The problem moved with it rather than going away. Unity 2020.3 validates against Mono's
*bundled* CA set, not Android's system store, and that bundle lacks newer roots — so a
Cloudflare-fronted host (ECDSA leaf → GTS WE1 → GTS Root R4) fails with
`UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED` (`0x8`) even though the headset itself trusts R4.
The hook therefore targets the **instance** overload of `UnityTlsContext.VerifyCallback`
— not the static `[MonoPInvokeCallback]` trampoline, and not `Debug.CheckAndThrow` (by the
time that runs the native handshake has already aborted).

**That is only half of it.** Rec Room pins certificates *on top of* the TLS trust decision,
and the two are independent — fixing trust alone gets you a working handshake and then a
silent shutdown. `EKAKCHPEMEJ` holds a `byte[][]` of pinned SPKI hashes and checks every
response (`KNDLCMMMHAK` → `FONBPFGIHGB` walks to the root → `BDBPJJCIFNE` compares →
`MEEELJJBJIP` reports). A self-hosted host never matches the pins, so it reports
`NetworkCertificateMismatch` and the client quits itself:

```
[anticheat] report reason=103 (NetworkCertificateMismatch)
            msg="recflare.net | WE1 | GTS Root R4 | BPN0c6doi2CuQ7g1xYEwe0tJ..."
```

Both hooks are required and neither substitutes for the other. On 2106701 this never came
up separately because stubbing BouncyCastle's `NotifyServerCertificate` happened to cover
both; on 2168575 they are genuinely two checks in two places.

### HTTP tracing

The port of the PC plugin's `SendRequestPatch` logging, with the same four `[Advanced]`
config keys so a PC config can be reused (`Log HTTP`, `Debug`, `Log HTTP Body Max`,
`Log Noisy Endpoints` — documented for users in README → *Configure*).

Bodies are read via `GetEntityBody()` / `get_Data()` invoked *by name*, so this survives a
build rebase with no new RVAs. Text-vs-binary uses the content type **plus** a byte sniff,
because `Content-Type` isn't reliably set at `SendRequest` time (multipart bodies set it
lazily while already carrying raw image bytes); binary renders as printable characters with
`\xNN` escapes so multipart framing stays readable. A single logcat message truncates around
4 KB, so long bodies are emitted as numbered continuation lines (`body=[1] …`, `[2] …`)
rather than being cut off mid-JSON.

### The OpenXR swap

The game ships Unity's Oculus XR Plugin 1.12.1, whose `libOVRPlugin.so` is the **VrApi**
build of OVRPlugin 1.66.0 and links `libvrapi.so`. `com.unity.xr.oculus` 3.0.2 ships the
*same* OVRPlugin build compiled against **OpenXR**, linking `libopenxr_loader.so` instead,
and exporting a strict superset of the VrApi build's `ovrp_*` symbols (477 vs 469, none
dropped) — so `libOculusXRPlugin.so` and the game's baked-in `OVRPlugin.cs` P/Invokes keep
resolving against it. That makes it a drop-in swap rather than a port:

```
libOVRPlugin.so (vrapi) -> libOVRPlugin.so (openxr)
libvrapi.so             -> deleted
                        +  libopenxr_loader.so
manifest                +  IMMERSIVE_HMD category, <queries> for com.oculus.systemdriver
```

`libOculusXRPlugin.so` is deliberately **not** swapped: 3.0.2's copy targets Unity 2021.2+'s
XR SDK interface, and this game is 2020.3.33f1 with `Unity.XR.Oculus.dll` already compiled
into `libil2cpp.so` against the 1.12.1 API. `openxr-swap.sh` pins SHA-256s for both
swapped-in binaries and refuses to run if the game's OVRPlugin version isn't the one it was
verified against.

## Build

```sh
export ANDROID_NDK=~/x/ndk/android-ndk-r27c
scripts/build.sh <pristine-store.apk>      # -> build/out.apk, ~30s
adb install -r build/out.apk
```

For a Meta store upload the three rejections are handled by `prepare-project.sh` env vars —
`OPENXR=1` (default; drops `libvrapi.so`), `TARGET_SDK=32` (default), and
`VERSION_CODE=<higher than the last upload>`. **`sdkInfo` and `versionInfo` live in
`apktool.yml`, not the decoded manifest** — apktool strips and re-emits them, so that file is
the only place they can be set. Bumping `versionCode` invalidates the OBB, whose name Unity
rebuilds at runtime as `main.<versionCode>.<package>.obb`; copy the OBB to the new name
rather than renaming, so the previous build stays bootable. See README → *Building for the
Meta store*.

`build.sh` chains `prepare-project.sh` (apktool decode + package rename + LAUNCHER),
`build-hook.sh` (bake `recflare.cfg` → `config_generated.h`, compile, LIEF-inject `DT_NEEDED`)
and `build-sign.sh` (rebuild → `resources.arsc` stored uncompressed → zipalign → apksigner
v2+v3). Because `prepare-project.sh` decodes fresh each time, injection always starts from a
pristine `libmain.so`. Keep `tools/debug.keystore` — Oculus ties tokens to app id + package +
signing cert, so the key must stay consistent.

## Testing on device

**The headset must be worn.** Off-head it sleeps within seconds and the app never gets past
init, which looks exactly like a startup crash. Don't work around this by disabling the
proximity sensor without asking — it's the user's device, and they generally prefer to drive
it themselves.

```sh
adb logcat -c && adb shell am start -n net.recflare.RecFlare/com.unity3d.player.UnityPlayerActivity
adb logcat -d -s RECFLARE
```

A healthy boot logs: every hook `armed`, then `intercepted https://ns.rec.net/ -> …`,
`unitytls verify: forcing 0x8 -> SUCCESS`, `cert pinning: accepting chain`, `[http<-] 200`,
and `photon appsettings: RT=… Voice=… Chat=…`. The logcat buffer holds days of history, so
`-d` after the fact is fine — no need to keep a capture running.

For request/response bodies set `Debug = true` in `recflare.cfg`. It can also be toggled
without a rebuild by pushing the cfg to
`/sdcard/Android/data/<package>/files/recflare.cfg`. Those logs contain auth tokens and
client secrets, so don't paste them around wholesale.

## Rebasing onto a new game build

Targets are hardcoded `k*RVA` constants at the top of `recflare_hook.cpp`. Obfuscated
Assembly-CSharp names are re-randomised **every build**, so never trust a name — re-resolve by
*signature*.

1. **Check `../recnet-plugin/Patches/*.cs` first.** The PC plugin is usually updated ahead of
   this one and its comments record rename chains verbatim (e.g. `PhotonPatches.cs` carried
   `GPFPFDBGCEK.AMOHMPKKGHL` → `LEALBOODIEE.GBNKOFMAJPA` → `HPEENKELKDJ.MGKINLFMJLB`). This
   turns a blind search of a 100 MB `dump.cs` into one grep — and it reveals when a target has
   been *removed* rather than renamed.
2. **Dump:** `DOTNET_ROLL_FORWARD=Major dotnet Il2CppDumper.dll libil2cpp.so global-metadata.dat ~/x/rfdump<build>`.
   The metadata must match the `.so` or every RVA is wrong.
3. **Re-resolve each target** by the signature in its header comment (table below).
4. **Verify the prologue** before building — `llvm-objdump -d --start-address=0x… libil2cpp.so`
   (NDK's copy; there's no system `llvm-objdump`). In this ELF `Offset == VA == RVA`, so the
   file disassembles directly. You want a real frame setup (`sub sp` / `stp x29, x30`).
5. **Re-check field offsets** but expect them not to move — none did across 2106701 → 2168575.

### How to re-find each target

| Target | Signature to search for |
| --- | --- |
| `HTTPManager.SendRequest` | unobfuscated; BestHTTP names are stable |
| `HTTPRequest.CallCallback` | unobfuscated |
| `PlatformSettings.get_AppID` / `get_MobileAppID` | unobfuscated |
| Photon `AppSettings` factory | the only **concrete** instance, 0-param method returning `Photon.Realtime.AppSettings` (an abstract interface declaration also exists — not a target) |
| `UnityTlsContext.VerifyCallback` | the **instance** overload, *not* the static `[MonoPInvokeCallback]` trampoline and not `Debug.CheckAndThrow` |
| Cert pinning verifier | the static method taking `(HTTPRequest, X509Certificate, X509Chain)` returning `bool`, on the class holding a `byte[][]` of pinned hashes |
| Image signature verify | the static `(byte[], byte[]) -> bool` on the images class (`"api/images/"`) that constructs `RSACryptoServiceProvider` + `SHA1CryptoServiceProvider` and calls `VerifyData` |
| Cheat report | the static method taking `(<readable reason enum>, string, Nullable<int>)`, on the class with `"api/PlayerReporting/"` |
| `SessionManager.FatalApplicationQuit` / `TryApplicationQuit` | unobfuscated |

### Current RVAs

Current build: versionCode `2168575` (`20230414`), `libil2cpp.so` md5
`eaa5ebbfce2dc484424f049aa37cd17e`, `global-metadata.dat` 29,021,860 bytes.

RVAs for `2168575`, with the `2106701` values they replaced:

| Target | 2106701 | 2168575 |
| --- | --- | --- |
| `HTTPManager.SendRequest(HTTPRequest)` | `0x6B553D8` | `0x70440E8` |
| `HTTPRequest.CallCallback()` | `0x6B5DFA0` | `0x704C93C` |
| `PlatformSettings.get_AppID` | `0xA2FB10C` | `0x962D660` |
| `PlatformSettings.get_MobileAppID` | `0xA2FB0EC` | `0x962D808` |
| Photon `AppSettings` factory | `0x34D6828` (`GPFPFDBGCEK.AMOHMPKKGHL`) | `0x38B6854` (`HPEENKELKDJ.MGKINLFMJLB`) |
| Cert validation | `0x6D21564` (`LegacyTlsAuthentication.NotifyServerCertificate`) | `0x90AA350` (`UnityTlsContext.VerifyCallback`) |
| Cert pinning | — (covered by the above) | `0x5D8C1A8` (`EKAKCHPEMEJ.KNDLCMMMHAK`) |
| Image signing | — (not patched) | `0x6671BA0` (`GDHOAGNFOBD.IMKOEDHDCIB`) |
| Cheat report | — | `0x6912F5C` (`IBPKDJLFBHM.FNODNAHADDD`) |
| Quit funnels | — | `0x367D328` / `0x367D1D4` (`SessionManager.FatalApplicationQuit` / `TryApplicationQuit`) |

Every struct field offset used by the hooks (`HTTPRequest`, `HTTPResponse`, `AppSettings`)
is **unchanged** between the two builds.

## Hard-won gotchas

1. **Certificates are two independent checks.** Fixing either alone looks like failure.
   *Trust*: Unity 2020.3 validates against Mono's **bundled** CA set, not Android's system
   store, and that bundle lacks newer roots — a Cloudflare-fronted host (ECDSA leaf → GTS WE1
   → GTS Root R4) fails `NOT_TRUSTED` (`0x8`) even though the headset itself trusts R4.
   *Pinning*: Rec Room separately compares the chain against baked-in SPKI hashes, which a
   self-hosted host never matches; it reports `NetworkCertificateMismatch` and **quits**. On
   2106701 one BouncyCastle stub covered both, so this reads as one problem in old code and
   isn't. Both hooks are required. Details above, under *Certificate validation*.

2. **`A64HookFunction` overwrites 20 bytes — check the target's length first.** Bound a method
   with the next RVA in `dump.cs`. `Application.Quit()` is *8* bytes
   (`mov w0, wzr; b Quit(int)`); hooking it corrupts the following function and kills the
   process during startup. Tail calls help: `Quit()` `b`s to `Quit(int)`, so hooking only the
   larger one covers both overloads *and* preserves the original caller in `x30`.

3. **Diagnose a silent quit, don't guess at it.** The client shuts down through
   `FatalApplicationQuit` → `TryApplicationQuit` → `Action.Invoke` → `Application.Quit` with
   **no exception and no error line** — graceful Photon disconnect, then it kills its own
   process. The lever is that detections are reported through the PlayerReporting class with a
   reason enum the obfuscator left **readable** (`Obscured`, `Time`, `Inject`, `UnknownDll`,
   `ImageSignature`, `NetworkCertificateMismatch`, `Memory_Hash_Mismatch`,
   `Driver_Invalid_Signature`, …). Hook that one method and the cause names itself. An early
   guess in this session that the culprit was `CheatManager.<OnUnknownDLLDetected>b__0` was
   wrong — the stub never even fired — and cost two build cycles.
   **Ignore the `SpeedHackDetector` `NullReferenceException`** in logcat: it fires ~1.2 s
   *after* the quit decision, during teardown. It is never the cause.

4. **Getting a caller from inside a hook.** `A64HookFunction` redirects with `LDR`/`BR` and
   never touches `x30`, so `__builtin_return_address(0)` is the real caller — subtract the
   logged libil2cpp base for an RVA. Unity's `StackTraceUtility.ExtractStackTrace()` returns
   an **empty string** in this player (managed traces are only materialised for logged
   exceptions), so don't reach for it.

5. **`scripts/xref.py` for cross-references.** `dump.cs` has RVAs but no xrefs; IL2CPP is
   AOT-compiled, so a call is a `bl` with a PC-relative immediate and scanning `.text` gives
   real xrefs in ~2 s.
   ```sh
   scripts/xref.py libil2cpp.so dump.cs 0xRVA           # who calls this
   scripts/xref.py --calls libil2cpp.so dump.cs 0xRVA   # what this calls
   scripts/xref.py --whois libil2cpp.so dump.cs 0xRVA   # which method contains this address
   ```
   `--calls` is the fast way to characterise an obfuscated method: its callees are mostly
   unobfuscated framework names, which usually give away what it does.

6. **`il2cpp_domain_get()` creates a domain, it doesn't just read one.** Calling it before
   runtime init aborts inside `il2cpp_init`. The hook thread instead polls
   `dlopen("libil2cpp.so", RTLD_NOLOAD)`.

7. **Packaging.** `resources.arsc` must be stored uncompressed and 4-byte aligned (Android
   11+) and the APK needs an apksigner v2/v3 signature, or install fails with `-124` /
   `NO_CERTIFICATES`. The OBB's on-device name must be
   `main.<versionCode>.<package>.obb` — Unity rebuilds that path at runtime, so the source
   file's own name is irrelevant.

8. **Cloudflare 403s are an edge problem, not a client bug.** An `"Attention Required!"` HTML
   body in the response log means fix it in the Cloudflare dashboard — a `POST` to
   `auth.recflare.net/connect/token` can be blocked by bot-fight/WAF *before* it reaches the
   origin, so the server logs show no such request at all.

## Anti-cheat detections

Only two of the reason enum's values have actually been seen on a self-hosted server, and
both are patched: `NetworkCertificateMismatch` (103, quits) and `ImageSignature` (6, doesn't
quit — images just fail to load). The report hook stays armed precisely so a new one names
itself rather than showing up as another silent shutdown or a missing feature.

If `ImageSignature` reappears, note *which* of the caller's three report branches fired: the
last one (after `Convert.FromBase64String` has succeeded) means the signature parsed and
genuinely didn't verify, which the RSA-verify hook covers. An earlier branch means a
malformed or absent `sig` parameter, and the hook has to move up into the URL parser instead.
