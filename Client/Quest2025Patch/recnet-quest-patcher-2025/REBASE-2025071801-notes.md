# Quest rebase 2025071801 (game version 20250718.01, versionCode 3358141) — resolved targets

Game build 2025071801, Unity 2021.3.41f1, IL2CPP metadata **v29** with a v31-style
36-byte Il2CppMethodDefinition. libil2cpp.so md5 7d572f62617b05ae67b309e4e1f81945.
Device: Quest 3, Android 14, NOT rooted.

## How the dump was obtained (metadata + registration are obfuscated)
1. Metadata header is field-permuted + magic/version stripped. Reconstructed a canonical
   v29 header by tiling [0x100,filesize] into 31 sections and identifying each by
   count cross-validation (Images->TypeDefs->per-type counts). Physical sections rewritten
   contiguously in standard order -> global-metadata.canon3.dat.
2. Il2CppMethodDefinition is **36 bytes** (v31 ReturnParameterToken @ +12) though header says
   v29. Converted each method record 36->32 (drop bytes[12:16]) in canon3.
3. Binary registration is relocation-based (pointers 0 in file, RELATIVE relocs), not found
   by Il2CppDumper/Inspector/Cpp2IL auto-search. Located manually:
   - MetadataRegistration @ 0x17d73340 (fieldOffsetsCount=typeDefinitionSizesCount=58423)
   - codeGenModules array @ 0x183c3ad0, 396 modules (module0=Assembly-CSharp.dll)
4. Built name->RVA map directly from codeGenModules[img].methodPointers[token_rid-1], using
   metadata (typeDef.methodStart/Count, image typeStart ranges). Scripts in scratchpad;
   output /home/devin/x/rf2025071801/sig_rva.txt (364701 methods, signatures included).
   VERIFIED correct by disassembling get_AppID (loads string field, ldr x0,[x0,#0x18]).

## Resolved RVAs (arm64, build 2025071801)
| Target | RVA | Signature |
| --- | --- | --- |
| HTTPManager.SendRequest(HTTPRequest) | 0x15236bd4 | BestHTTP.HTTPRequest SendRequest(BestHTTP.HTTPRequest) |
| HTTPRequest.CallCallback() | 0x15240450 | void |
| PlatformSettings.get_AppID | 0x14ea2944 | string |
| PlatformSettings.get_MobileAppID | 0x14ea2928 | string |
| Cert NotifyServerCertificate | 0x1522d83c | Org.BouncyCastle.Crypto.Tls.LegacyTlsAuthentication (BestHTTP.SecureProtocol Certificate) |
| Image sig verify | 0x156f2bc0 | bool HPJKKCCECLH.BLDFMLMAFLH(byte[],byte[]) |
| Image RSA pubkey (ref) | 0x156f2fc8 | RSAParameters HPJKKCCECLH.OGONMIPNEKK() |
| Anti-cheat module-scan suppressor | 0x0b6d5d70 | void GLMGKKNCFGH.LOELLDBOINB()  (loads 533223478) |
| SessionManager.FatalApplicationQuit(int,string) | 0x0b701fe8 | |
| SessionManager.TryApplicationQuit() | 0x0b701e9c | |
| SessionManager.TryApplicationQuit(int) | 0x0b701eec | |
| Photon ConnectUsingSettings | 0x14fca008 | bool OLPEILEPEAD.JBNCMFDFDLM(Photon.Realtime.AppSettings) |

## Key architecture changes vs 2168575
- CERT: BouncyCastle is BACK. Single NotifyServerCertificate nop hook REPLACES the
  UnityTlsContext.VerifyCallback + EKAKCHPEMEJ cert-pinning pair (both DROPPED).
- PHOTON: app ids come from the server now; DO NOT inject app ids. Only set the Photon
  SERVER (+ port). Hook ConnectUsingSettings (OLPEILEPEAD.JBNCMFDFDLM) instead of the
  AppSettings factory.
- ANTI-CHEAT: 2025 quits via CheatManager module scan -> FatalApplicationQuit(533223478,
  filenames) when it sees the injected lib. Suppress by nop-ing GLMGKKNCFGH.LOELLDBOINB.
  (Old PlayerReporting report hook mechanism no longer the culprit.)
- Field offsets UNCHANGED from 2168575 (confirmed against PC 2025 patch):
  HTTPRequest.Uri=0x10, Response=0x70; HTTPResponse.StatusCode=0x18;
  AppSettings Realtime=0x10/Chat=0x18/Voice=0x20/UseNameServer=0x30/FixedRegion=0x38/Server=0x48/Port=0x50;
  array data=0x20,length=0x18.
- No-Meta platform hooks (ovr_* exported symbols) unchanged — survive rebase.
- OpenXR: 2025 APK already ships libopenxr_loader.so, no libvrapi.so -> OpenXR swap likely moot.

Reference: ../Rec-Room-2025-Patch (working PC patch, same build 19/07/25). Obfuscated
METHOD names match Quest<->PC; CLASS names (esp. compiler closures) differ.

## On-device test (2026-08-20, no-Meta build)
(`202608201` is our repackaged APK's **internal** versionCode — an arbitrary sideload build
number we set via `VERSION_CODE`, **not** the patched game version. The game build under test is
**20250718.01** throughout: build string `2025071801`, game versionCode `3358141`.)
WORKS: boots cleanly (no anti-cheat self-quit -> CheatManager suppressor confirmed), all
hooks arm, nameserver redirect ns.rec.net->ns.recflare.net (200), cert bypass
(NotifyServerCertificate no-op fires, HTTPS 200s), gameconfigs 200, entitlement bypass fires
("entitlement check: forcing IsError -> false").

GAP (no-Meta path only): stops at "[Platform.BootSequence] MMMPPBEAMAC: Unable to load Oculus
user account". The ovr_GetLoggedInUserID hook's 0->1 branch NEVER fires on 2025 -- the account
load moved from 2168575's synchronous ovr_GetLoggedInUserID to an async ovr message whose type
is NOT whitelisted in hk_ovr_IsError (only Entitlement 409688241 and User_GetUserProof
578880643 are). Added a diagnostic to hk_ovr_IsError that logs the type of any other error
message on the no-app-id path; the next no-Meta boot names the constant to add. The Meta build
(Oculus App Id set) does NOT use these hooks, so it is expected to work -- untested pending a
real App ID. This is deferred work per the user.

## No-Meta login progress (2026-08-20, iterating)
Server-side (Cloudflare + RecNet endpoints) fixes by the operator unblocked the RecNet login:
- Cloudflare was 403-ing POST /cachedlogin/forplatformid/1/1 at the edge (empty body, not in
  origin logs). Allowlisted -> 200.
- Client GET /oculus/nonce (no query/body, bearer from cachedlogin session). Server returns a
  JSON string nonce -> client authenticates. NOTE: no RecNet "Nonce" DTO in metadata, so the
  response deserializes as a plain string.
- After that the full logged-in boot runs (playerReputation/progression/avatar/clubs/chat/
  playersettings, all 200). RecNet login WORKS.

Two remaining blockers:
1. LOGIN-BREAKER: forcing the account-load ovr message (1131361373) makes the client believe it
   has a real Oculus session, so it fetches the account from https://graph.oculus.com/me (via the
   NATIVE OVR SDK CurlHttpClient, not BestHTTP) -> 190 Invalid OAuth 2.0 Access Token -> the
   "[Platform.BootSequence] MMMPPBEAMAC" logger reports it and the boot fails. 2168575 never hit
   this (CLAUDE.md: graph /me stub "not needed"); 2025 does. Fix analog = the old
   OculusPlatformManager.<>c.<LoadUser>b__60_9 graph-/me forge. TODO: find the 2025 C# method that
   builds/parses "https://graph.oculus.com/me" (string IS in managed metadata) and forge a valid
   account so the boot completes. A new unhandled ovr message type 2124073717 also appears late.
2. PHOTON: our redirect armed on OLPEILEPEAD.JBNCMFDFDLM(AppSettings) (1-arg, 0x14fca008) but that
   hook never fires -- the live client connects through OEOMKFKDCCG.JBNCMFDFDLM(AppSettings,bool)
   (2-arg, 0x14fe973c). So it hit Photon Cloud and failed "GetRegions ... Invalid application id
   format" + OnDisconnected InvalidAuthentication. Fix: hook the 2-arg overload to set
   AppSettings.Server, and the server must supply a valid Photon app-id GUID (current one is
   invalid format). OEOMKFKDCCG is the live LoadBalancingClient; OLPEILEPEAD is a second instance.

## Tooling conclusion (2026-08-20)
Neither Il2CppDumper nor Cpp2IL will auto-find the binary registration on this build, even with
metadata fully fixed (canon4 = methods converted + default-values emptied) and even after applying
all RELATIVE relocations to the .so (libil2cpp.reloc.so): Il2CppDumper gets past metadata then
"This file may be protected" at Searching; Cpp2IL fails validating the codeGenModules array
(396 vs its expected 399). Neither accepts manual CodeReg/MetaReg cleanly. So: the working dump is
the custom Python resolver (scratchpad resolve2.py -> ~/x/rf2025071801/sig_rva.txt, name+signature
-> RVA, 364701 methods, VERIFIED by disassembly). Method BODIES require manual llvm-objdump on the
il2cpp section (0xa1190e8..0x17307e3c) at the RVA from sig_rva; raw bytes since objdump won't
disassemble that custom section by name. MetadataRegistration=0x17d73340, codeGenModules=0x183c3ad0.

## Meta build (Oculus App Id set) findings (2026-08-20)
Tested with real app id 1232175103309633 (the account owns it: "AppID found in
Library(1232175103309633)"). Result: entitlement FAILS "Package net.recflare.RecFlare not in
users library" / "does NOT match the appID in the bundle(2173678582678296)".
- The managed get_AppID/get_MobileAppID override IS armed and returns 1232175103309633, but it
  does NOT change the app id the NATIVE OVR platform initialises with: IL2CPP inlines the app-id
  field read at the init call site, so the getter is never called before init. The Horizon system
  service (separate process) checks entitlement against the native-init app id = Rec Room's
  2173678582678296 (not a plaintext string anywhere in APK/OBB/il2cpp; passed at runtime init).
- Attempt: hook the native init to rewrite arg0 (appId). All public inits funnel through the
  exported ovr_PlatformInitializeAndroidHelper (0x53748, takes x0=appId,x1,w2=bool). Hooking the
  4 public variants did NOT fire (game uses a different variant). Hooking the Helper funnel DID
  fire but CRASHES: SIGABRT "JNI DETECTED ERROR: java_class == null" inside the Helper's
  getApplicationContext() -> not safely hookable. Reverted (kept the 4 harmless public hooks).
- Even with the app id fixed, entitlement also needs net.recflare.RecFlare + tools/debug.keystore
  cert registered under app 1232175103309633 in the Oculus dashboard, with the account entitled.
- IMPORTANT: this proves the CLIENT ITSELF WORKS -- the no-Meta path already reached full account
  load. The Meta failure is app-id/dashboard, not a client bug. A safe native app-id override (or
  a manifest/OBB app-id patch) is the open item if the Meta build is wanted.

## Meta build app-id: exhausted client-side overrides (2026-08-20, all FAILED)
The entitlement checks Rec Room's baked-in app id 2173678582678296 (account owns operator app
1232175103309633: "AppID found in Library(1232175103309633) does NOT match the appID in the
bundle(2173678582678296)"). Tried, on-device, all of these -- entitlement STILL used 2173:
1. get_AppID / get_MobileAppID managed getter hooks (what carried 2168575). On 2025 the
   entitlement path reads the value INLINE, never calling the getter.
2. Hooking the native platform init to rewrite arg0: ovr_UnityInitWrapper (the entry the game
   uses: game -> ovr_UnityInitWrapper -> setupUnityShim -> ovr_PlatformInitializeAndroidHelper)
   fired and overrode 2173->1232, but the entitlement (already run) still used 2173; hooking all
   8 public init variants -> none fired before the first entitlement. Hooking the internal
   ...Helper funnel CRASHES (SIGABRT JNI java_class==null in getApplicationContext).
3. Overwriting the Oculus.Platform.PlatformSettings static fields ovrAppID(@+0x18) /
   ovrMobileAppID(@+0x20) via il2cpp_field_static_set_value: patch fired and set both to 1232,
   entitlement 21ms later STILL used 2173 (then SEGV).
CONCLUSION: on 2025 the entitlement app id does NOT come from Oculus.Platform.PlatformSettings
nor any hookable ovr_*Initialize* arg. It comes from a native path -- almost certainly OVRManager
(Unity.XR.Oculus) reading its own OculusPlatformSettings.asset (Resources, separate from
Oculus.Platform.PlatformSettings) and initialising the native platform through libOVRPlugin. The
app id 2173678582678296 is NOT plaintext anywhere in the APK/OBB/il2cpp/metadata (it's in the OBB
Unity data, compressed). Open leads for the Meta build: (a) patch OculusPlatformSettings.asset's
app id in the OBB (needs Unity asset decompress/recompress), or (b) hook OVRManager's platform
init / the libOVRPlugin path. Source reverted to get_AppID-hook-only (non-crashing) for the Meta
build. The no-Meta build remains the working path (reached full account load).

## Meta app-id: source is not findable statically (2026-08-20, final)
The entitlement app id 2173678582678296 is NOT present as a string OR int64 anywhere: APK
(incl. reassembled globalgamemanagers.assets splits), OBB, libil2cpp, libOVRPlugin,
libovrplatformloader. Yet the C# constructs it at runtime and passes it to ovr_UnityInitWrapper
(seen in the hook: "appid 2173678582678296"). It does NOT come from
Oculus.Platform.PlatformSettings.ovrMobileAppID -- patching that field did not change what
ovr_UnityInitWrapper received. So the native OVR init reads the app id from some OTHER
settings object (OVRManager / Unity.XR.Oculus OculusSettings / OVRProjectConfig, via
libOVRPlugin), and the value is built/decoded at runtime rather than stored plainly.
Cracking the Meta build needs finding that construction site (deeper RE than done here) or a
build-time patch of whatever asset actually holds it. get_AppID-hook-only build deployed
(non-crashing). The no-Meta build remains the working path.

## Meta app-id: entitlement is NOT from the runtime init chain (2026-08-20, definitive)
Overrode the app id at EVERY point in the platform-init chain -- all fired, none changed the
entitlement (still 2173678582678296):
 - Oculus.Platform.PlatformSettings get_AppID/get_MobileAppID getters + ovrAppID/ovrMobileAppID
   static fields
 - OculusPlatformManager.get_AppID getter + its instance app-id field @+0x130 (patched in Awake,
   which ran 2.6s BEFORE the entitlement -- so timing was fine, field just isn't the source)
 - the native ovr_UnityInitWrapper(appId) arg (fired, overrode 2173->1232, entitlement still 2173)
Since overriding ovr_UnityInitWrapper -- the terminal native call the whole C# chain
(OculusPlatformManager.HBGDJDKKNIO -> Core.OCOPJMCPGIB -> ovr_UnityInitWrapper) feeds -- did NOT
change the entitlement, the entitlement app id does NOT come from that init chain. It is read by
the Horizon OS system service (com.oculus.horizon, ExternalPlatformLocal / HzPlatformJavaBridge)
from the installed PACKAGE / Java side, independent of the app process, and 2173678582678296 is
not plaintext anywhere in the APK/OBB/libs. No in-process runtime hook can reach it.
=> For a RELEASE CHANNEL: the "PackageNotInLibrary" failure is the signature of a SIDELOAD. The
   real test is to UPLOAD the APK to the operator's app (1232175103309633) release/test channel and
   install it FROM Oculus -- a legitimate channel install is what makes Horizon treat the package
   as entitled, which sideloading never will. That likely also requires the app to actually be
   built as app 1232 (its OVR app id set at build time in the Unity Oculus settings), which for
   this repackaged client means finding/patching that value at build time -- still an open item.

## Meta build: SOLVED (2026-08-21)
The fix is to override the app id at the Oculus.Platform.Core init (HEKMJNGPIEH.OCOPJMCPGIB,
static, RVA 0x14ea23c4) -- hk_CoreInit replaces the appId string it forwards to
ovr_UnityInitWrapper. This is an il2cpp method so it arms EARLY (with the other il2cpp hooks),
which is why it lands before the first platform init and the entitlement latches the operator's
id -- every other override (getters, PlatformSettings static fields, OculusPlatformManager
instance field, native ovr_UnityInitWrapper) either read the wrong source or armed too late.
Confirmed on-device: entitlement passes on the release channel with app id 1232175103309633.
Dead-code from the search (OculusPlatformManager get_AppID/Awake hooks, extended il2cpp field
API) removed; only hk_CoreInit remains armed on the Meta build (plus the get_AppID getter hooks
for managed consistency).

Next: after auth, an OVR "User Age Group" call returns 403 (code 10, error_subcode 1891537) --
"requires certifying through the Oculus Data Use Checkup program". Dashboard-fixable (Data Use
Checkup / Compliance Dashboard) OR suppressible client-side (the call is optional). TBD.
