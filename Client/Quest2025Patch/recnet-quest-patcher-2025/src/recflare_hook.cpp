// RecFlare Quest hooks -- native port of the PC BepInEx plugin's SendRequestPatch.
//
// Loaded via an injected DT_NEEDED entry on libmain.so, so it runs before Unity.
// Waits for libil2cpp.so to appear, then inline-hooks
// BestHTTP.HTTPManager.SendRequest(HTTPRequest) and rewrites the name-server host,
// exactly like the Harmony Prefix does on PC:
//
//     if (request.Uri.Host == "ns.rec.net") request.Uri = <same uri, our host>;
//
// Only ns.rec.net is redirected -- the name-server response supplies every other
// service URL, so nothing else needs patching.

#include <dlfcn.h>
#include <netdb.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <unistd.h>

#include "And64InlineHook.hpp"

// Declared by hand rather than including <android/log.h>, which is an NDK header
// and isn't part of the bionic libc headers zig ships.
extern "C" int __android_log_print(int prio, const char *tag, const char *fmt, ...);
#define LOGI(...) __android_log_print(4, "RECFLARE", __VA_ARGS__)
#define LOGE(...) __android_log_print(6, "RECFLARE", __VA_ARGS__)

// ---------------------------------------------------------------- config ----

// The official name-server host we redirect away from (never changes).
static const char *kOfficialNameServer = "ns.rec.net";

// Endpoints skipped when logging: high-frequency telemetry/heartbeat traffic that drowns
// out everything else. Same list as the PC plugin's SendRequestPatch. Set
// "Log Noisy Endpoints = true" to include them anyway.
static const char *kNoisyUrlParts[] = {
    "/api/gamesight/event", "/data/heartbeat", "/identify", "/httpapi", "/data/event",
};

// Content types treated as text; anything else is rendered as a byte preview. No
// Content-Type at all counts as text, so we err toward showing the body.
static const char *kTextContentTypes[] = {
    "text/", "application/json", "application/xml", "application/javascript",
    "application/x-www-form-urlencoded", "+json", "+xml",
};

// Il2CppArray: [Il2CppObject 0x10][bounds 0x10][length 0x18][data 0x20...] on 64-bit.
static const size_t kArrayLengthOff = 0x18;
static const size_t kArrayDataOff = 0x20;

// One logcat message truncates around 4 KB including tag and priority, so a long body is
// emitted as several continuation lines rather than being silently cut off.
static const size_t kLogChunk = 1500;

// Photon server redirect. On 2025 the client takes its Photon app ids from an endpoint on
// the RecNet server, NOT from AppSettings -- so swapping the *server* is the entire job and
// we do NOT touch the AppId fields (see ../Rec-Room-2025-Patch, which reaches the same
// conclusion and redirects Photon at the DNS layer on PC). Quest has no DNS hook, so instead
// we hook the connect seam and set AppSettings.Server directly, right before the client
// connects.
//
// The target is ConnectUsingSettings(Photon.Realtime.AppSettings) -- an instance method.
// Obfuscated name re-randomises per build, but the 2025 name matches the PC patch's:
// OLPEILEPEAD.JBNCMFDFDLM(AppSettings). Re-resolve by that signature (bool method taking a
// single Photon.Realtime.AppSettings) against ~/x/rf<build>/sig_rva.txt, cross-checked with
// ../Rec-Room-2025-Patch/2025Patch/src/RR/Methods.h (Photon::ConnectUsingSettings).
static const uintptr_t kPhotonConnectRVA = 0x14fca008;
// AppSettings field offsets. Unchanged from 2168575 and confirmed against the PC 2025 patch
// (RR::Offsets::AppSettings). Only kPortOff is written now -- the server swap happens at DNS
// (see hk_getaddrinfo). The rest document the layout for future rebasing: the AppId fields
// (0x10/0x18/0x20) are never ours to set (the RecNet server supplies the ids), and
// UseNameServer/FixedRegion/Server/Protocol are left to the client's own defaults because it
// reaches Photon through the name server, where AppSettings.Server is not consulted.
[[maybe_unused]] static const size_t kAppIdRealtimeOff = 0x10;
[[maybe_unused]] static const size_t kAppIdChatOff = 0x18;
[[maybe_unused]] static const size_t kAppIdVoiceOff = 0x20;
[[maybe_unused]] static const size_t kUseNameServerOff = 0x30;  // bool
[[maybe_unused]] static const size_t kFixedRegionOff = 0x38;
[[maybe_unused]] static const size_t kServerOff = 0x48;
static const size_t kPortOff = 0x50;    // int
[[maybe_unused]] static const size_t kProtocolOff = 0x60;  // int (Udp=0)

// Config. Defaults are baked in at build time: scripts/build-hook.sh generates
// config_generated.h from recflare.cfg and puts it on the include path, so the values
// ship inside the APK -- no device-side file needed. Each RF_* below falls back to a
// built-in default when the key is absent from recflare.cfg (or there's no config at
// all). The runtime file at /sdcard/Android/data/<pkg>/files/recflare.cfg still works
// as an optional override (see load_config).
#if defined(__has_include)
#  if __has_include("config_generated.h")
#    include "config_generated.h"
#  endif
#endif

#ifndef RF_PACKAGE
#  define RF_PACKAGE "net.recflare.RecFlare"
#endif
#ifndef RF_NAMESERVER_HOST
#  define RF_NAMESERVER_HOST "ns.recflare.net"
#endif
// No default app id either -- it ties a build to one Meta app, and an empty value simply
// disables the override so the client keeps its own (see the get_AppID hook).
#ifndef RF_OCULUS_APPID
#  define RF_OCULUS_APPID ""
#endif
// No defaults for the Photon app ids -- they're per-deployment credentials, so they come
// from recflare.cfg (which is git-ignored) or not at all. Empty means "leave the game's own
// value alone", which points at Rec Room's dead Photon apps, so multiplayer won't work
// until they're configured.
#ifndef RF_PHOTON_REALTIME
#  define RF_PHOTON_REALTIME ""
#endif
#ifndef RF_PHOTON_VOICE
#  define RF_PHOTON_VOICE ""
#endif
#ifndef RF_PHOTON_CHAT
#  define RF_PHOTON_CHAT ""
#endif
#ifndef RF_PHOTON_SERVER
#  define RF_PHOTON_SERVER ""   // empty = Photon Cloud
#endif
#ifndef RF_PHOTON_PORT
#  define RF_PHOTON_PORT 0      // 0 = Photon default
#endif
#ifndef RF_LOG_HTTP
#  define RF_LOG_HTTP 1         // one line per request and per response
#endif
#ifndef RF_DEBUG_HTTP
#  define RF_DEBUG_HTTP 0       // + full request/response bodies ("Debug" in the cfg)
#endif
#ifndef RF_LOG_BODY_MAX
#  define RF_LOG_BODY_MAX 10000
#endif
#ifndef RF_LOG_NOISY
#  define RF_LOG_NOISY 0        // include telemetry/heartbeat endpoints
#endif

static char g_nameserver_host[128] = RF_NAMESERVER_HOST;  // ns.rec.net -> this
static char g_oculus_appid[64]     = RF_OCULUS_APPID;
static char g_photon_realtime[64]  = RF_PHOTON_REALTIME;
static char g_photon_voice[64]     = RF_PHOTON_VOICE;
static char g_photon_chat[64]      = RF_PHOTON_CHAT;
static char g_photon_server[128]   = RF_PHOTON_SERVER;
static int  g_photon_port          = RF_PHOTON_PORT;
static bool g_log_http             = RF_LOG_HTTP;
static bool g_debug_http           = RF_DEBUG_HTTP;
static int  g_body_max             = RF_LOG_BODY_MAX;
static bool g_log_noisy            = RF_LOG_NOISY;

// HTTPRequest.CallCallback() fires when a request finishes (Response populated), so
// hooking it lets us log the response status + body -- revealing whether a 403 came
// from the origin, from Cloudflare (HTML challenge), or from no network at all.
static const uintptr_t kCallCallbackRVA = 0x15240450;
static const size_t kReqResponseOffset = 0x70;  // HTTPRequest.<Response>k__BackingField
static const size_t kReqStateOffset = 0xB4;     // HTTPRequest.<State>k__BackingField (int)
static const size_t kReqExceptionOffset = 0x80; // HTTPRequest.<Exception>k__BackingField

// RVA of BestHTTP.HTTPManager.SendRequest(HTTPRequest). Unobfuscated name; disambiguate the
// overload by signature -- the one taking a single BestHTTP.HTTPRequest (there are also
// string/HTTPMethods/delegate overloads that are not the target).
static const uintptr_t kSendRequestRVA = 0x15236bd4;

// HTTPRequest.<Uri>k__BackingField
static const size_t kUriFieldOffset = 0x10;

// Certificate-validation bypass -- the successor to the PC plugin's DisableTLSPinning.
//
// The TLS stack has swung back and forth across builds, so re-check which one is present
// before trusting any target here:
//   * 2106701 bundled BestHTTP's own BouncyCastle stack and stubbed
//     Org.BouncyCastle.Crypto.Tls.LegacyTlsAuthentication.NotifyServerCertificate.
//   * 2168575 DROPPED BouncyCastle entirely and ran on the platform UnityTlsContext stack,
//     which needed TWO hooks (UnityTlsContext.VerifyCallback for trust + a separate
//     cert-pinning verifier), because Unity 2020.3 validates against Mono's bundled CA set.
//   * 2025071801 brings BouncyCastle BACK (global-metadata.dat again contains
//     "LegacyTlsAuthentication" and "BestHTTP.SecureProtocol.Org.BouncyCastle"), matching
//     the PC 2025 patch. So we are back to the ONE BouncyCastle stub and the UnityTls +
//     pinning pair is gone.
//
// Target: void LegacyTlsAuthentication.NotifyServerCertificate(Certificate) -- the seam where
// the client inspects the server's certificate and can reject it. The name is unobfuscated
// (Org.BouncyCastle.Crypto.Tls.LegacyTlsAuthentication) and its parameter is BestHTTP's own
// Certificate type, so it is the copy BestHTTP actually uses. Replace-only: a no-op body
// means the client never inspects (and so never rejects) the chain. Same target the PC patch
// nops (RR::Methods::LegacyTlsAuthentication::NotifyServerCertificate).
static const uintptr_t kNotifyServerCertRVA = 0x1522d83c;

// Oculus.Platform.PlatformSettings app id. Rec Room's original app id lives in the
// serialized ScriptableObject (in the OBB). We swap it for our own registered Oculus app
// so that real platform authentication runs against an app the user is actually entitled
// to -- this is configuration, not a bypass. Both the desktop (get_AppID) and mobile
// (get_MobileAppID) getters are overridden.
// App id value is g_oculus_appid (configurable); these are the getter RVAs.
static const uintptr_t kGetAppIDRVA = 0x14ea2944;
static const uintptr_t kGetMobileAppIDRVA = 0x14ea2928;

// The Meta build's app-id override -- this is what actually makes the 2025 entitlement run
// against the operator's registered app (verified on-device 2026-08-21).
//
// On 2168575 the get_AppID getter hooks above were enough. On 2025 they are NOT: the platform
// init reads the app id INLINE and feeds it down a managed chain (OculusPlatformManager init ->
// Oculus.Platform.Core init -> native ovr_UnityInitWrapper), and the entitlement latches the id
// from the FIRST init. Overriding it at the getters, the PlatformSettings static fields, the
// OculusPlatformManager instance field, and the native ovr_UnityInitWrapper arg all failed --
// the native hooks because they arm too late (after libovrplatformloader loads), the field/getter
// overrides because that is not the source the init read. The fix is to override at the
// Oculus.Platform.Core init itself: it is an il2cpp method (armed early, with the rest), so it
// lands before the first init. void OCOPJMCPGIB(string appId, MethodInfo*) -- static, x0=appId.
// Re-resolve by the crash-trace chain above if the obfuscated name shifts.
static const uintptr_t kCoreInitRVA = 0x14ea23c4;

// Entitlement check, armed ONLY when no app id is configured (see init_thread).
//
// With an app id set, the client initialises the platform against an app the operator
// registered and the user owns, so the entitlement check passes on its own merits and
// this stays disarmed. With no app id the client initialises against *Rec Room's* app
// id -- a delisted title nobody can still be entitled to -- so the check can only ever
// fail, and it fails closed: "You do not have permission to play this game". Forcing it
// to pass is what makes an app-id-less sideload boot at all.
//
// ovr_Message_IsError / ovr_Message_GetType are exported from libovrplatformloader.so,
// so this needs no RVA and survives a game-build rebase untouched. GetType identifies
// the message; every message other than the entitlement reply is passed through with its
// real result.
static const int kEntitlementGetIsViewerEntitled = 409688241;

// User-proof message, handled under the same no-app-id condition as the entitlement check.
//
// Without a Meta session the proof request fails with 190 "Invalid OAuth 2.0 Access Token";
// the client logs "Oculus GetUserProof failed" then "GetPlatformIdProof failed: Failed to
// get Oculus Login" and refuses to continue. Supplying a placeholder nonce lets that step
// complete so the login flow moves on to the client's own credential entry.
//
// The nonce is deliberately a fixed, meaningless string: it cannot be verified against Meta
// by anyone, so a server that checks proofs properly will reject it. That rejection is the
// intended outcome -- it is what pushes the client to username/password. A server that
// *accepts* an unverifiable proof would be logging every no-Meta player in as the same
// account, so do not add a server-side shortcut for this value.
static const int kUserGetUserProof = 578880643;
static const char *kPlaceholderNonce = "recflare-no-platform-account";

// User account-load message (the async ovr_User_GetLoggedInUser reply). 2025 loads the user
// through THIS instead of 2168575's synchronous ovr_GetLoggedInUserID, so without forcing its
// IsError->false the boot stops at "[Platform.BootSequence] ... Unable to load Oculus user
// account". Type value found on-device via the diagnostic in hk_ovr_IsError (2026-08-20).
// Forcing it not-an-error lets ovr_Message_GetUser -> ovr_User_GetID run, which the sentinel
// id-1 hook then satisfies, so the flow lands on the credential login exactly as before.
static const int kUserGetLoggedInUser = 1131361373;

// User Age Group (Meta build). The platform's age-category check
// (ovr_UserAgeCategory_Get) requires Data Use Checkup certification; without it Meta
// returns 403 "This call accesses the User Age Group platform feature, which requires
// certifying through the Oculus Data Use Checkup program", and the age reply comes back an
// error with no category. We mock it as non-child (>= 13): downgrade the reply from error
// to success (below) and return ovrAppAgeCategory_Nch from the getter. The age message
// type is obfuscator-independent but stored in a compressed metadata blob, so -- like the
// entitlement/proof/account types -- it is discovered on-device via the diagnostic in
// hk_ovr_IsError_meta and pinned here.
static const int kAgeCategoryNch = 2;   // ovrAppAgeCategory_Nch (non-child, >= 13)
// Age-category (UserAgeCategory_Get) reply type, found on-device via the diagnostic in
// hk_ovr_IsError_meta: it is the message whose IsError check immediately precedes the
// "User Age Group ... Data Use Checkup ... 403" MMMPPBEAMAC error. Distinct from the
// In-App-Purchases 403 (available_purchases, type 2124073717), which is left untouched.
static int g_ageMessageType = 567009472;

// Placeholder platform user id, armed under the same no-app-id condition.
//
// This is NOT an identity: it is a sentinel that makes the Oculus platform path fail in a
// way the client *handles* instead of one it hangs on. With no Meta session
// ovr_GetLoggedInUserID returns 0; the client logs "GetLoggedInUser failed: User ID was
// zero", falls back to GET graph.oculus.com/v1.20/me/sdk_accounts, gets 190 "Invalid OAuth
// 2.0 Access Token", logs "An unknown error occurred. Request failed." from
// OculusPlatformManager.<>c.<LoadUser>b__60_9 -- and then simply stops, leaving the app on
// "Connecting to server..." forever with no exception and no quit.
//
// A non-zero id makes it take the native path instead, which fails against RecNet (the
// server verifies Oculus logins for real and this id verifies as nobody) and drops the
// player on the client's own username/password login -- the point of an app-id-less build,
// for players with no Meta account. Nothing here can authenticate: it is designed to fail,
// just at a step the client has a fallback for.
//
// If this alone is not enough, the next lever up is the /me response stub the 20230302
// build used: OculusPlatformManager.<>c.<LoadUser>b__60_9(HTTPResponse) -- re-resolved for
// 2168575 at RVA 0x37BBB78 (it was 0x3486DC8 on 2106701). Not armed; try the id alone
// first, because that stub forges a *specific* account id rather than an unusable one.
static const unsigned long long kNoPlatformUserId = 1ULL;

// BestHTTP.HTTPResponse field offsets (from the dump).
static const size_t kRespStatusCodeOffset = 0x18;  // int <StatusCode>k__BackingField

// Certificate PINNING was a SECOND, independent check on 2168575 (the platform-UnityTls
// build): EKAKCHPEMEJ held a byte[][] of pinned SPKI hashes and quit with
// NetworkCertificateMismatch (103) on a miss, so that build needed a dedicated pinning hook
// on top of the trust bypass. 2025071801 is back on BouncyCastle and the single
// NotifyServerCertificate stub above covers everything (the PC 2025 patch likewise installs
// no separate pinning hook), so there is no pinning target here any more. If a future build
// drops BouncyCastle again, this two-hook split is what to restore -- see git history and the
// kNotifyServerCertRVA comment.

// Image signing. Image URLs carry a signature the client verifies against a baked-in RSA
// public key; a self-hosted server can't produce one, so verification fails, the client
// reports ImageSignature (6), and images don't load. It does *not* quit over this -- profile
// pictures and other images are simply missing.
//
// The images class (GDHOAGNFOBD, "api/images/") parses the URL and ends at
//   IMKOEDHDCIB(byte[] data, byte[] signature) -> bool
// which news up RSACryptoServiceProvider + SHA1CryptoServiceProvider and calls
// RSACryptoServiceProvider.VerifyData. That is the same crypto the PC plugin's
// ImageSigningPatch forces true, but here it is reachable at one game-specific entry point,
// so this hook is narrower: the PC patch forces *every* mscorlib RSA verification to succeed
// (its own comment calls that a blunt instrument), while this only affects image signatures
// and leaves the TLS/certificate paths untouched.
//
// The image class matches the PC 2025 patch: HPJKKCCECLH holds both the RSA public key
// (OGONMIPNEKK() -> RSAParameters) and the verifier, so re-resolve by the verify signature
// -- the static (byte[] data, byte[] sig) -> bool on that class. Replace-only, force true.
static const uintptr_t kImageSigVerifyRVA = 0x156f2bc0;

// Anti-cheat self-quit -- why a sideloaded 2025 build quits on its own.
//
// 2025 quits through CheatManager's module scan: it enumerates loaded modules, strips the app
// dir from each path, and hands the survivors to a compiler-generated closure whose whole body
// is SessionManager.FatalApplicationQuit(533223478, filenames). Our injected libvrsupport.so
// shows up in that list, so the client fatally quits ~instantly. (This is a different
// mechanism from 2168575's PlayerReporting report -- that path is no longer the culprit.)
//
// Suppress by no-op'ing the closure. The method name matches the PC patch's LOELLDBOINB (the
// closure CLASS name differs across builds -- compiler-generated closures re-number -- so
// resolve by the method: a nullary void that loads the immediate 533223478 (0x1FC85836) and
// calls FatalApplicationQuit). 2025: GLMGKKNCFGH.LOELLDBOINB(). See ../Rec-Room-2025-Patch
// (AntiCheat::ModuleScanDetected / CheatQuit_H) and REBASE-2025071801-notes.md.
static const uintptr_t kCheatQuitSuppressRVA = 0x0b6d5d70;

// The two quit funnels, kept as log-only tracing so any *other* self-quit names itself rather
// than showing up as a silent shutdown. FatalApplicationQuit carries a reason string that
// never otherwise reaches logcat.
static const uintptr_t kFatalQuitRVA = 0x0b701fe8;   // FatalApplicationQuit(int, string)
static const uintptr_t kTryQuitIntRVA = 0x0b701eec;  // TryApplicationQuit(int)

// How the caller of a quit is identified, since it comes up every time this breaks:
// A64HookFunction redirects with LDR/BR and never touches x30, so at hook entry
// __builtin_return_address(0) is still the real caller's return address; subtract the module
// base and look the RVA up with scripts/xref.py --whois. UnityEngine.StackTraceUtility
// .ExtractStackTrace() is not an option -- it returns an empty string in this player, because
// managed stack traces are only materialised for logged exceptions.
//
// Do not hook UnityEngine.Application.Quit() (0x6A79F60) to do this. It is *8 bytes* --
// "mov w0, wzr; b Quit(int)" -- and A64HookFunction overwrites 20, which corrupts the
// function after it and kills the process during startup. Check a target's length against
// the next method's RVA before hooking anything small.

// ------------------------------------------------------------ il2cpp api ----

typedef void *(*il2cpp_object_get_class_t)(void *obj);
typedef void *(*il2cpp_class_get_method_from_name_t)(void *klass, const char *name, int argc);
typedef void *(*il2cpp_object_new_t)(void *klass);
typedef void *(*il2cpp_string_new_t)(const char *str);
typedef void *(*il2cpp_runtime_invoke_t)(void *method, void *obj, void **params, void **exc);
typedef void (*il2cpp_thread_attach_t)(void *domain);
typedef void *(*il2cpp_domain_get_t)();

static il2cpp_object_get_class_t il2cpp_object_get_class;
static il2cpp_class_get_method_from_name_t il2cpp_class_get_method_from_name;
static il2cpp_object_new_t il2cpp_object_new;
static il2cpp_string_new_t il2cpp_string_new;
static il2cpp_runtime_invoke_t il2cpp_runtime_invoke;
static il2cpp_thread_attach_t il2cpp_thread_attach;
static il2cpp_domain_get_t il2cpp_domain_get;

// Il2CppString: [klass][monitor][int32 length][char16 chars...]
static void il2cpp_str_to_utf8(void *s, char *out, size_t cap) {
    out[0] = '\0';
    if (!s || cap == 0) return;
    int32_t len = *(int32_t *)((char *)s + 0x10);
    if (len < 0) return;
    const uint16_t *chars = (const uint16_t *)((char *)s + 0x14);
    size_t i = 0;
    for (; i < (size_t)len && i + 1 < cap; i++)
        out[i] = chars[i] < 0x80 ? (char)chars[i] : '?';
    out[i] = '\0';
}

// --------------------------------------------------------------- logging ----

static bool url_is_noisy(const char *url) {
    if (g_log_noisy) return false;
    for (size_t i = 0; i < sizeof(kNoisyUrlParts) / sizeof(*kNoisyUrlParts); i++)
        if (strcasestr(url, kNoisyUrlParts[i])) return true;
    return false;
}

// Emit a long value across several logcat lines instead of letting it be cut off.
static void log_chunked(const char *prefix, const char *text) {
    size_t n = strlen(text);
    if (n <= kLogChunk) {
        LOGI("%s%s", prefix, text);
        return;
    }
    char buf[kLogChunk + 1];
    for (size_t off = 0, part = 1; off < n; off += kLogChunk, part++) {
        size_t take = n - off < kLogChunk ? n - off : kLogChunk;
        memcpy(buf, text + off, take);
        buf[take] = '\0';
        LOGI("%s[%zu] %s", prefix, part, buf);
    }
}

// No Content-Type -> treat as text, so we err toward showing the body.
static bool is_binary_content_type(const char *ct) {
    if (!ct || !*ct) return false;
    for (size_t i = 0; i < sizeof(kTextContentTypes) / sizeof(*kTextContentTypes); i++)
        if (strcasestr(ct, kTextContentTypes[i])) return false;
    return true;
}

// Content sniff, because Content-Type isn't reliably set at SendRequest time (multipart
// bodies set it lazily and still embed raw image bytes). A NUL, or >10% control bytes in
// the first chunk, means binary.
static bool looks_binary(const unsigned char *d, size_t n) {
    if (n == 0) return false;
    size_t sample = n < 4096 ? n : 4096, nontext = 0;
    for (size_t i = 0; i < sample; i++) {
        unsigned char b = d[i];
        if (b == 0) return true;
        if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) nontext++;
    }
    return nontext * 100 / sample > 10;
}

// Render a body into out. Text is copied as-is; binary is shown as printable characters
// with \xNN escapes, so multipart framing and part headers stay readable. Truncated to the
// configured maximum, with the dropped amount noted.
static void format_body(const unsigned char *d, size_t n, const char *ct, char *out,
                        size_t cap) {
    if (!d || n == 0) { snprintf(out, cap, "<empty>"); return; }

    size_t limit = (size_t)g_body_max;
    if (limit > cap - 64) limit = cap - 64;

    if (!is_binary_content_type(ct) && !looks_binary(d, n)) {
        size_t take = n < limit ? n : limit;
        memcpy(out, d, take);
        out[take] = '\0';
        if (take < n)
            snprintf(out + take, cap - take, "... <truncated %zu bytes>", n - take);
        return;
    }

    int w = snprintf(out, cap, "<binary %zu bytes> ", n);
    size_t o = (size_t)(w < 0 ? 0 : w), i = 0;
    for (; i < n && o + 4 < limit; i++) {
        unsigned char b = d[i];
        if (b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b < 0x7F))
            out[o++] = (char)b;
        else
            o += (size_t)snprintf(out + o, cap - o, "\\x%02x", b);
    }
    out[o] = '\0';
    if (i < n) snprintf(out + o, cap - o, "... <truncated %zu bytes>", n - i);
}

// Call a 0-arg method returning byte[] and hand back the array plus its length.
static const unsigned char *invoke_bytes(void *obj, const char *methodName, size_t *outLen) {
    *outLen = 0;
    if (!obj) return nullptr;
    void *cls = il2cpp_object_get_class(obj);
    if (!cls) return nullptr;
    void *m = il2cpp_class_get_method_from_name(cls, methodName, 0);
    if (!m) return nullptr;
    void *exc = nullptr;
    void *arr = il2cpp_runtime_invoke(m, obj, nullptr, &exc);
    if (!arr || exc) return nullptr;
    *outLen = (size_t) * (uint64_t *)((char *)arr + kArrayLengthOff);
    return (const unsigned char *)((char *)arr + kArrayDataOff);
}

// GetFirstHeaderValue(name) on a request or response.
static void invoke_header(void *obj, const char *name, char *out, size_t cap) {
    out[0] = '\0';
    if (!obj) return;
    void *cls = il2cpp_object_get_class(obj);
    if (!cls) return;
    void *m = il2cpp_class_get_method_from_name(cls, "GetFirstHeaderValue", 1);
    if (!m) return;
    void *arg = il2cpp_string_new(name);
    if (!arg) return;
    void *args[1] = {arg};
    void *exc = nullptr;
    void *s = il2cpp_runtime_invoke(m, obj, args, &exc);
    if (s && !exc) il2cpp_str_to_utf8(s, out, cap);
}

// ---------------------------------------------------------------- hooks -----

static void *(*orig_SendRequest)(void *request);

static void *hk_SendRequest(void *request) {
    if (!request) return orig_SendRequest(request);

    void *uri = *(void **)((char *)request + kUriFieldOffset);
    if (!uri) return orig_SendRequest(request);

    void *uri_class = il2cpp_object_get_class(uri);
    if (!uri_class) return orig_SendRequest(request);

    // Read the absolute URI so we can match the host and rebuild it.
    void *m_abs = il2cpp_class_get_method_from_name(uri_class, "get_AbsoluteUri", 0);
    if (!m_abs) return orig_SendRequest(request);

    void *exc = nullptr;
    void *s = il2cpp_runtime_invoke(m_abs, uri, nullptr, &exc);
    if (!s || exc) return orig_SendRequest(request);

    char url[2048];
    il2cpp_str_to_utf8(s, url, sizeof(url));

    // Log every outbound request so we can see the full traffic (which host/endpoint
    // returns what). MethodType is HTTPRequest.<MethodType>k__BackingField at 0x18.
    if (g_log_http && !url_is_noisy(url)) {
        int method = *(unsigned char *)((char *)request + 0x18);
        static const char *methods[] = {"GET", "HEAD", "POST", "PUT", "DELETE",
                                        "PATCH", "MERGE", "OPTIONS", "CONNECT"};
        const char *mname = (method >= 0 && method < 9) ? methods[method] : "?";
        LOGI("[http] %s %s", mname, url);

        if (g_debug_http) {
            size_t blen = 0;
            const unsigned char *body = invoke_bytes(request, "GetEntityBody", &blen);
            if (body && blen) {
                char ct[128];
                invoke_header(request, "content-type", ct, sizeof(ct));
                size_t cap = (size_t)g_body_max + 128;
                char *text = (char *)malloc(cap);
                if (text) {
                    format_body(body, blen, ct, text, cap);
                    log_chunked("[http]   body=", text);
                    free(text);
                }
            }
        }
    }

    // Match the host specifically ("//host/") so a path or query mentioning the
    // name server can't trigger a rewrite.
    char needle[128];
    snprintf(needle, sizeof(needle), "//%s", kOfficialNameServer);
    const char *hit = strstr(url, needle);
    const char *after = hit ? hit + strlen(needle) : nullptr;
    if (!hit || (*after != '\0' && *after != '/' && *after != ':' && *after != '?')) {
        return orig_SendRequest(request);
    }

    // Swap only the host, preserving scheme, path and query.
    char newurl[1024];
    size_t prefix = (size_t)(hit - url) + 2;  // keep "https://"
    if (prefix >= sizeof(newurl)) return orig_SendRequest(request);
    memcpy(newurl, url, prefix);
    newurl[prefix] = '\0';
    strncat(newurl, g_nameserver_host, sizeof(newurl) - strlen(newurl) - 1);
    strncat(newurl, after, sizeof(newurl) - strlen(newurl) - 1);

    void *new_str = il2cpp_string_new(newurl);
    void *new_uri = il2cpp_object_new(uri_class);
    void *ctor = il2cpp_class_get_method_from_name(uri_class, ".ctor", 1);
    if (!new_str || !new_uri || !ctor) {
        LOGE("swap failed: str=%p uri=%p ctor=%p", new_str, new_uri, ctor);
        return orig_SendRequest(request);
    }

    void *args[1] = {new_str};
    exc = nullptr;
    il2cpp_runtime_invoke(ctor, new_uri, args, &exc);
    if (exc) {
        LOGE("Uri ctor threw for %s", newurl);
        return orig_SendRequest(request);
    }

    *(void **)((char *)request + kUriFieldOffset) = new_uri;
    LOGI("intercepted %s -> %s", url, newurl);

    return orig_SendRequest(request);
}

// PlatformSettings.get_AppID / get_MobileAppID -- static il2cpp getters. Their only argument is
// the hidden MethodInfo*, which we ignore. Return a fresh managed string of our app id each call
// so any managed code that reads PlatformSettings sees the operator's id.
//
// NOTE (2025 Meta build): this getter is NOT what carries the entitlement on 2025 -- the platform
// init reads the app id inline and the entitlement latches it there, so the actual override that
// matters is hk_CoreInit (see kCoreInitRVA). This hook is kept for managed consistency.
static void *hk_get_AppID(void *method) {
    (void)method;
    static int logged = 0;
    if (logged < 4) {
        logged++;
        LOGI("get_AppID -> %s", g_oculus_appid);
    }
    return il2cpp_string_new(g_oculus_appid);
}

// Oculus.Platform.Core init: override the app-id string it forwards to the native platform init.
// See kCoreInitRVA.
static void (*orig_CoreInit)(void *appId, void *method);
static void hk_CoreInit(void *appId, void *method) {
    static int logged = 0;
    if (logged < 2) {
        logged++;
        char cur[64];
        il2cpp_str_to_utf8(appId, cur, sizeof(cur));
        LOGI("Core init: appid %s -> %s", cur[0] ? cur : "(null)", g_oculus_appid);
    }
    orig_CoreInit(il2cpp_string_new(g_oculus_appid), method);
}

// Entitlement check. ovr_Message_GetType(msg) says which platform message this is and
// ovr_Message_IsError(msg) whether it failed; we only ever downgrade an error on the
// entitlement reply. Everything else -- including anything identity-related -- keeps its
// real result. See kEntitlementGetIsViewerEntitled.
typedef bool (*ovr_Message_IsError_t)(void *message);
typedef unsigned int (*ovr_Message_GetType_t)(void *message);

static ovr_Message_IsError_t orig_ovr_IsError;
static ovr_Message_GetType_t ovr_GetType;

static bool hk_ovr_IsError(void *message) {
    bool real = orig_ovr_IsError(message);
    if (!real || !ovr_GetType) return real;
    unsigned int type = ovr_GetType(message);
    if (type == (unsigned int)kEntitlementGetIsViewerEntitled) {
        static int logged = 0;
        if (logged < 3) { logged++; LOGI("entitlement check: forcing IsError -> false"); }
        return false;
    }
    if (type == (unsigned int)kUserGetUserProof) {
        static int logged = 0;
        if (logged < 3) { logged++; LOGI("user-proof message: forcing IsError -> false"); }
        return false;
    }
    if (type == (unsigned int)kUserGetLoggedInUser) {
        static int logged = 0;
        if (logged < 3) { logged++; LOGI("account-load message: forcing IsError -> false"); }
        return false;
    }
    // Diagnostic: any *other* error message on the no-app-id path is a candidate for the
    // account-load step that 2168575 handled via the synchronous ovr_GetLoggedInUserID but
    // 2025 does through an async message this filter does not yet whitelist. Naming its type
    // here is how to find the constant to add above -- the 2025 boot stops at "Unable to load
    // Oculus user account" precisely because one of these stays an error. Log-only; the real
    // result is passed through so nothing genuine is masked.
    {
        static int logged = 0;
        if (logged < 12) { logged++; LOGI("ovr message type=%u IsError=1 (unhandled -- candidate account-load message)", type); }
    }
    return real;
}

// ovr_Message_GetUserProof(msg) -> UserProof handle. On an error message the real call
// returns null; hand back a non-null sentinel so the client goes on to read the nonce,
// which the hook below supplies. The sentinel is never dereferenced by us.
static void *(*orig_ovr_Message_GetUserProof)(void *message);
static void *hk_ovr_Message_GetUserProof(void *message) {
    void *p = orig_ovr_Message_GetUserProof ? orig_ovr_Message_GetUserProof(message) : nullptr;
    return p ? p : (void *)0x1;
}

// ovr_UserProof_GetNonce(proof) -> const char*. A real proof is always passed through; only
// the sentinel above yields the placeholder. See kPlaceholderNonce.
static const char *(*orig_ovr_UserProof_GetNonce)(void *proof);
static const char *hk_ovr_UserProof_GetNonce(void *proof) {
    if (proof && proof != (void *)0x1 && orig_ovr_UserProof_GetNonce) {
        const char *n = orig_ovr_UserProof_GetNonce(proof);
        if (n && n[0]) return n;
    }
    static int logged = 0;
    if (logged < 2) { logged++; LOGI("user proof -> placeholder nonce (unverifiable)"); }
    return kPlaceholderNonce;
}

// ovr_GetLoggedInUserID() -> ulong. Only substitutes when the real call says 0 (no
// session); a genuine id is always passed through. See kNoPlatformUserId.
static unsigned long long (*orig_ovr_GetLoggedInUserID)();
static unsigned long long hk_ovr_GetLoggedInUserID() {
    unsigned long long real = orig_ovr_GetLoggedInUserID ? orig_ovr_GetLoggedInUserID() : 0;
    if (real) return real;
    static int logged = 0;
    if (logged < 2) {
        logged++;
        LOGI("no platform session: user id 0 -> %llu (falls through to credential login)",
             kNoPlatformUserId);
    }
    return kNoPlatformUserId;
}

// ovr_User_GetID(user) -> ulong. Same substitution for the message-delivered user object.
static unsigned long long (*orig_ovr_User_GetID)(void *user);
static unsigned long long hk_ovr_User_GetID(void *user) {
    unsigned long long real = orig_ovr_User_GetID ? orig_ovr_User_GetID(user) : 0;
    return real ? real : kNoPlatformUserId;
}

// ovr_Message_IsError on the Meta build. Downgrade *only* the age-category reply from error
// to success so the client reads a category instead of dropping to no-age (and logging the
// Data Use Checkup 403). Every other message keeps its real result, so a genuine Meta
// failure stays visible. While g_ageMessageType is still 0 this is diagnostic-only: it logs
// the type of each error message so the age type can be identified and pinned above. See
// kAgeCategoryNch.
static bool hk_ovr_IsError_meta(void *message) {
    bool real = orig_ovr_IsError(message);
    if (!real || !ovr_GetType) return real;
    unsigned int type = ovr_GetType(message);
    if (g_ageMessageType && type == (unsigned int)g_ageMessageType) {
        static int logged = 0;
        if (logged < 3) { logged++; LOGI("age-category message: forcing IsError -> false"); }
        return false;
    }
    static int logged = 0;
    if (logged < 16) { logged++; LOGI("[meta] ovr error message type=%u (unhandled)", type); }
    return real;
}

// ovr_UserAccountAgeCategory_GetAgeCategory(handle) -> ovrAppAgeCategory. Always report
// non-child (>= 13); the handle is never dereferenced, so this is safe even on the sentinel
// message the IsError hook downgraded. See kAgeCategoryNch.
static unsigned int (*orig_age_GetAgeCategory)(void *handle);
static unsigned int hk_age_GetAgeCategory(void *handle) {
    (void)handle;
    static int logged = 0;
    if (logged < 2) { logged++; LOGI("age category -> Nch (>= 13)"); }
    return (unsigned int)kAgeCategoryNch;
}

// Read an il2cpp object's string-returning nullary method into a utf8 buffer.
static void invoke_string_to_utf8(void *obj, const char *methodName, char *out, size_t cap) {
    out[0] = '\0';
    if (!obj) return;
    void *cls = il2cpp_object_get_class(obj);
    if (!cls) return;
    void *m = il2cpp_class_get_method_from_name(cls, methodName, 0);
    if (!m) return;
    void *exc = nullptr;
    void *s = il2cpp_runtime_invoke(m, obj, nullptr, &exc);
    if (s && !exc) il2cpp_str_to_utf8(s, out, cap);
}

// HTTPRequest.CallCallback(this, MethodInfo*) -- log the completed request's outcome.
static void (*orig_CallCallback)(void *thiz, void *method);
static void hk_CallCallback(void *thiz, void *method) {
    if (!thiz || !g_log_http) {
        orig_CallCallback(thiz, method);
        return;
    }

    char url[2048];
    invoke_string_to_utf8(*(void **)((char *)thiz + kUriFieldOffset), "get_AbsoluteUri", url,
                          sizeof(url));
    if (url_is_noisy(url)) {
        orig_CallCallback(thiz, method);
        return;
    }

    int state = *(int *)((char *)thiz + kReqStateOffset);
    void *resp = *(void **)((char *)thiz + kReqResponseOffset);
    if (!resp) {
        // State 3=Finished, others = Error/Aborted/TimedOut/ConnectionTimedOut.
        // The Exception field is the only thing that says *why* -- without it a DNS
        // failure, a refused connect and a TLS verify error all look identical here,
        // and the TLS one is the whole reason the UnityTls hook below exists.
        // ToString() rather than get_Message so the exception type comes along.
        void *ex = *(void **)((char *)thiz + kReqExceptionOffset);
        if (ex) {
            char msg[900];
            invoke_string_to_utf8(ex, "ToString", msg, sizeof(msg));
            LOGI("[http<-] NO-RESPONSE state=%d %s | %s", state, url, msg);
        } else {
            LOGI("[http<-] NO-RESPONSE state=%d %s | (no exception)", state, url);
        }
    } else {
        int status = *(int *)((char *)resp + kRespStatusCodeOffset);
        LOGI("[http<-] %d %s", status, url);

        // A non-2xx body is always worth seeing (it's how a Cloudflare challenge page is
        // told apart from the app's own JSON); with Debug on, every body is.
        bool wantBody = g_debug_http || status < 200 || status >= 300;
        if (wantBody) {
            size_t blen = 0;
            const unsigned char *data = invoke_bytes(resp, "get_Data", &blen);
            char ct[128];
            invoke_header(resp, "content-type", ct, sizeof(ct));
            size_t cap = (size_t)g_body_max + 128;
            char *text = (char *)malloc(cap);
            if (text) {
                format_body(data, blen, ct, text, cap);
                log_chunked("[http<-]   body=", text);
                free(text);
            }
        }
    }
    orig_CallCallback(thiz, method);
}

// LegacyTlsAuthentication.NotifyServerCertificate(this, Certificate, MethodInfo*) -> void.
// Replace-only no-op: the client never inspects the presented certificate, so it can never
// reject a self-hosted host's chain. Same discipline as the PC patch's nop_H.
static void hk_NotifyServerCert(void *thiz, void *cert, void *method) {
    (void)thiz;
    (void)cert;
    (void)method;
    static int logged = 0;
    if (logged < 3) { logged++; LOGI("tls: accepting server certificate (NotifyServerCertificate no-op)"); }
}

// Photon backend swap -- DNS-level, matching the PC 2025 patch (RR::Methods::Photon /
// IsPhotonHost). Rec Room connects to Photon Cloud's *name server* with UseNameServer=true,
// then follows it to a region master; the region flow means AppSettings.Server is never read
// (an earlier version wrote it and the client still went to ns.exitgames.com:5058 and got
// "ReturnCode 32767 Invalid application id"). So we don't touch AppSettings.Server at all --
// we redirect the Photon hostnames at getaddrinfo instead, so ns.exitgames.com and every
// master/game-server it hands back resolve to our own Photon (Luxon) host, which accepts the
// RecNet-issued app id. See hk_getaddrinfo and kServerHostPatterns.
static const char *kPhotonHostPatterns[] = {"photonengine", "exitgames", "photonindustries"};
static bool is_photon_host(const char *node) {
    if (!node) return false;
    for (const char *p : kPhotonHostPatterns)
        if (strstr(node, p)) return true;
    return false;
}

// getaddrinfo(node, service, hints, res). When name resolution targets a Photon Cloud host
// and a Photon server is configured, resolve OUR host instead -- keeping the caller's port
// (service) and hints, so only the address changes. Everything else resolves normally.
static int (*orig_getaddrinfo)(const char *, const char *, const struct addrinfo *,
                               struct addrinfo **);
static int hk_getaddrinfo(const char *node, const char *service,
                          const struct addrinfo *hints, struct addrinfo **res) {
    if (g_photon_server[0] && is_photon_host(node)) {
        static int logged = 0;
        if (logged < 8) { logged++; LOGI("photon dns: %s -> %s", node, g_photon_server); }
        return orig_getaddrinfo(g_photon_server, service, hints, res);
    }
    return orig_getaddrinfo(node, service, hints, res);
}

// ConnectUsingSettings(this, Photon.Realtime.AppSettings, MethodInfo*) -> bool. The server
// swap itself is done at getaddrinfo above; this seam remains only to override the initial
// connect *port* when one is configured (the DNS redirect preserves the caller's port, so
// this is a no-op unless the client's default port differs from the Luxon deployment's).
static bool (*orig_PhotonConnect)(void *thiz, void *settings, void *method);
static bool hk_PhotonConnect(void *thiz, void *settings, void *method) {
    if (settings && g_photon_server[0] && g_photon_port) {
        *(int *)((char *)settings + kPortOff) = g_photon_port;
        static int logged = 0;
        if (logged < 2) {
            logged++;
            LOGI("photon connect: port -> %d (server swap is at DNS)", g_photon_port);
        }
    }
    return orig_PhotonConnect(thiz, settings, method);
}

static uintptr_t g_il2cpp_base;

static void log_call_site(const char *what, void *ret) {
    uintptr_t a = (uintptr_t)ret;
    if (g_il2cpp_base && a > g_il2cpp_base)
        LOGI("[exit] %s <- libil2cpp rva 0x%lx", what, (unsigned long)(a - g_il2cpp_base));
    else
        LOGI("[exit] %s <- %p (outside libil2cpp)", what, ret);
}

// HPJKKCCECLH.BLDFMLMAFLH(byte[] data, byte[] sig, MethodInfo*) -> bool. See
// kImageSigVerifyRVA.
static bool hk_ImageSigVerify(void *data, void *sig, void *method) {
    (void)data;
    (void)sig;
    (void)method;
    static int logged = 0;
    if (logged < 3) {
        logged++;
        LOGI("image signature: accepting");
    }
    return true;
}

// CheatManager module-scan closure GLMGKKNCFGH.LOELLDBOINB(this, MethodInfo*) -> void.
// Its entire body is SessionManager.FatalApplicationQuit(533223478, filenames); it fires when
// the scan spots our injected library. Replace-only no-op: never call the original, so the
// quit is never requested. See kCheatQuitSuppressRVA.
static void hk_CheatQuitSuppress(void *thiz, void *method) {
    (void)thiz;
    (void)method;
    static int logged = 0;
    if (logged < 3) { logged++; LOGI("[anticheat] module-scan quit suppressed"); }
}

static void (*orig_FatalQuit)(int code, void *reason, void *method);
static void hk_FatalQuit(int code, void *reason, void *method) {
    char msg[512];
    il2cpp_str_to_utf8(reason, msg, sizeof(msg));
    LOGI("[exit] FatalApplicationQuit(%d, \"%s\")", code, msg);
    log_call_site("  ...called from", __builtin_return_address(0));
    orig_FatalQuit(code, reason, method);
}

// TryApplicationQuit() (no args) tail-calls this one, so x30 still points at the original
// caller either way.
static void (*orig_TryQuitInt)(int code, void *method);
static void hk_TryQuitInt(int code, void *method) {
    LOGI("[exit] TryApplicationQuit(%d)", code);
    log_call_site("  ...called from", __builtin_return_address(0));
    orig_TryQuitInt(code, method);
}

// Accepts the BepInEx spellings ("true"/"false") plus 1/0, yes/no, on/off.
static bool cfg_bool(const char *v, bool dflt) {
    if (!v || !*v) return dflt;
    if (!strcasecmp(v, "true") || !strcasecmp(v, "1") || !strcasecmp(v, "yes") ||
        !strcasecmp(v, "on"))
        return true;
    if (!strcasecmp(v, "false") || !strcasecmp(v, "0") || !strcasecmp(v, "no") ||
        !strcasecmp(v, "off"))
        return false;
    return dflt;
}

static void str_trim(char *s) {
    char *p = s;
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
    if (p != s) memmove(s, p, strlen(p) + 1);
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' ||
                     s[n - 1] == '\r' || s[n - 1] == '\n'))
        s[--n] = '\0';
}

// Read overrides from /sdcard/Android/data/<pkg>/files/recflare.cfg. Format is the
// BepInEx-style "Key = Value" (so the PC config's keys work verbatim); '#' comments
// and section headers are ignored. Missing file -> compiled-in defaults are used.
static void load_config() {
    char path[256];
    snprintf(path, sizeof(path),
             "/sdcard/Android/data/%s/files/recflare.cfg", RF_PACKAGE);
    FILE *f = fopen(path, "r");
    if (!f) {
        LOGI("no config at %s -- using built-in defaults", path);
        return;
    }
    char line[256];
    while (fgets(line, sizeof(line), f)) {
        char *hash = strchr(line, '#');
        if (hash) *hash = '\0';
        char *eq = strchr(line, '=');
        if (!eq) continue;
        *eq = '\0';
        char *key = line, *val = eq + 1;
        str_trim(key);
        str_trim(val);
        if (!strcasecmp(key, "RecNet NameServer Host") && *val) {
            // Accept a bare host or a full URL; the hook rewrites just the host.
            const char *scheme = strstr(val, "://");
            char *host = scheme ? (char *)scheme + 3 : val;
            char *slash = strchr(host, '/');
            if (slash) *slash = '\0';
            strncpy(g_nameserver_host, host, sizeof(g_nameserver_host) - 1);
        }
        else if (!strcasecmp(key, "Oculus App Id")) {
            // Empty value is meaningful: it disables the app-id override.
            g_oculus_appid[0] = '\0';
            strncpy(g_oculus_appid, val, sizeof(g_oculus_appid) - 1);
        }
        else if (!strcasecmp(key, "App Id Realtime") && *val)
            strncpy(g_photon_realtime, val, sizeof(g_photon_realtime) - 1);
        else if (!strcasecmp(key, "App Id Voice") && *val)
            strncpy(g_photon_voice, val, sizeof(g_photon_voice) - 1);
        else if (!strcasecmp(key, "App Id Chat") && *val)
            strncpy(g_photon_chat, val, sizeof(g_photon_chat) - 1);
        else if (!strcasecmp(key, "Photon NameServer"))
            strncpy(g_photon_server, val, sizeof(g_photon_server) - 1);
        else if (!strcasecmp(key, "Photon NameServer Port"))
            g_photon_port = atoi(val);
        else if (!strcasecmp(key, "Log HTTP"))
            g_log_http = cfg_bool(val, g_log_http);
        else if (!strcasecmp(key, "Debug"))
            g_debug_http = cfg_bool(val, g_debug_http);
        else if (!strcasecmp(key, "Log HTTP Body Max") && *val)
            g_body_max = atoi(val);
        else if (!strcasecmp(key, "Log Noisy Endpoints"))
            g_log_noisy = cfg_bool(val, g_log_noisy);
    }
    fclose(f);
    LOGI("config loaded from %s", path);
    LOGI("http logging: enabled=%d bodies=%d max=%d noisy=%d", g_log_http, g_debug_http,
         g_body_max, g_log_noisy);
}

// ----------------------------------------------------------------- init -----

static uintptr_t module_base(const char *name) {
    FILE *f = fopen("/proc/self/maps", "r");
    if (!f) return 0;
    char line[512];
    uintptr_t base = 0;
    while (fgets(line, sizeof(line), f)) {
        if (strstr(line, name)) {
            base = (uintptr_t)strtoull(line, nullptr, 16);
            break;  // first mapping is the ELF header
        }
    }
    fclose(f);
    return base;
}

static bool resolve_il2cpp(void *h) {
#define SYM(n)                                                     \
    n = (n##_t)dlsym(h, #n);                                       \
    if (!n) {                                                      \
        LOGE("dlsym(" #n ") failed: %s", dlerror());               \
        return false;                                              \
    }
    SYM(il2cpp_object_get_class)
    SYM(il2cpp_class_get_method_from_name)
    SYM(il2cpp_object_new)
    SYM(il2cpp_string_new)
    SYM(il2cpp_runtime_invoke)
    SYM(il2cpp_thread_attach)
    SYM(il2cpp_domain_get)
#undef SYM
    return true;
}

static void *init_thread(void *) {
    void *h = nullptr;
    for (int i = 0; i < 600; i++) {  // up to ~60s
        h = dlopen("libil2cpp.so", RTLD_NOLOAD | RTLD_NOW);
        if (h) break;
        usleep(100 * 1000);
    }
    if (!h) {
        LOGE("libil2cpp.so never appeared; aborting");
        return nullptr;
    }

    uintptr_t base = module_base("libil2cpp.so");
    if (!base) {
        LOGE("could not find libil2cpp.so base");
        return nullptr;
    }
    LOGI("libil2cpp.so @ %p", (void *)base);
    g_il2cpp_base = base;  // so log_call_site can report RVAs

    if (!resolve_il2cpp(h)) return nullptr;

    // Load config first so the values below (and the startup logs) reflect overrides.
    load_config();

    void *target = (void *)(base + kSendRequestRVA);
    A64HookFunction(target, (void *)hk_SendRequest, (void **)&orig_SendRequest);
    if (!orig_SendRequest) {
        LOGE("A64HookFunction failed for SendRequest @ %p", target);
        return nullptr;
    }
    LOGI("hooked SendRequest @ %p (rva 0x%lx), orig=%p", target,
         (unsigned long)kSendRequestRVA, (void *)orig_SendRequest);
    LOGI("redirect armed: %s -> %s", kOfficialNameServer, g_nameserver_host);

    // Oculus app id swap -- both getters return our id. Skipped when the config
    // clears the id, so the client keeps its own.
    if (g_oculus_appid[0]) {
        void *getAppId = (void *)(base + kGetAppIDRVA);
        void *getMobileAppId = (void *)(base + kGetMobileAppIDRVA);
        void *dummy = nullptr;
        A64HookFunction(getAppId, (void *)hk_get_AppID, &dummy);
        A64HookFunction(getMobileAppId, (void *)hk_get_AppID, &dummy);
        LOGI("oculus app id override armed: %s", g_oculus_appid);
    } else {
        // The client keeps its own (Rec Room's) app id, which nobody is entitled to any
        // more -- so the entitlement bypass at the end of this function gets armed too.
        LOGI("oculus app id override disabled (empty config)");
    }

    void *cb = (void *)(base + kCallCallbackRVA);
    A64HookFunction(cb, (void *)hk_CallCallback, (void **)&orig_CallCallback);
    if (orig_CallCallback)
        LOGI("response logging armed @ CallCallback %p", cb);

    // Photon server redirect. The swap is done at the DNS layer (getaddrinfo), matching the
    // PC 2025 patch: the client uses Photon Cloud's name server with UseNameServer=true, so
    // redirecting the exitgames/photonengine hostnames is what actually reaches our Luxon
    // host; AppSettings.Server is never read on the region path. App ids are left alone (the
    // RecNet server supplies them on 2025).
    if (g_photon_server[0]) {
        void *ga = dlsym(RTLD_DEFAULT, "getaddrinfo");
        if (ga) {
            A64HookFunction(ga, (void *)hk_getaddrinfo, (void **)&orig_getaddrinfo);
            if (orig_getaddrinfo)
                LOGI("photon dns redirect armed @ getaddrinfo %p -> %s", ga, g_photon_server);
            else
                LOGE("A64HookFunction failed for getaddrinfo @ %p", ga);
        } else {
            LOGE("getaddrinfo not found; photon dns redirect not armed");
        }
        void *photon = (void *)(base + kPhotonConnectRVA);
        A64HookFunction(photon, (void *)hk_PhotonConnect, (void **)&orig_PhotonConnect);
        if (orig_PhotonConnect)
            LOGI("photon connect port hook armed @ %p (rva 0x%lx), server=%s", photon,
                 (unsigned long)kPhotonConnectRVA, g_photon_server);
        else
            LOGE("A64HookFunction failed for photon connect @ %p", photon);
    } else {
        LOGI("photon redirect disabled (no Photon server configured)");
    }

    // Certificate-validation bypass -- the single BouncyCastle NotifyServerCertificate no-op
    // (2025 bundles BouncyCastle again; the 2168575 UnityTls + pinning pair is gone). See
    // kNotifyServerCertRVA.
    void *tlsv = (void *)(base + kNotifyServerCertRVA);
    void *dummy_tls = nullptr;
    A64HookFunction(tlsv, (void *)hk_NotifyServerCert, &dummy_tls);
    LOGI("tls cert-validation bypass armed @ %p (rva 0x%lx)", tlsv,
         (unsigned long)kNotifyServerCertRVA);

    // Image signature verification -- replace-only, so unsigned images load.
    void *imgsig = (void *)(base + kImageSigVerifyRVA);
    void *dummy3 = nullptr;
    A64HookFunction(imgsig, (void *)hk_ImageSigVerify, &dummy3);
    LOGI("image-signing bypass armed @ %p (rva 0x%lx)", imgsig,
         (unsigned long)kImageSigVerifyRVA);

    // Anti-cheat module-scan suppressor -- replace-only, so the CheatManager closure never
    // calls FatalApplicationQuit over our injected library. See kCheatQuitSuppressRVA.
    void *rep = (void *)(base + kCheatQuitSuppressRVA);
    void *dummy_ac = nullptr;
    A64HookFunction(rep, (void *)hk_CheatQuitSuppress, &dummy_ac);
    LOGI("anticheat module-scan suppressor armed @ %p (rva 0x%lx)", rep,
         (unsigned long)kCheatQuitSuppressRVA);

    // Name any *other* quit rather than leaving a silent shutdown to re-diagnose.
    void *fq = (void *)(base + kFatalQuitRVA);
    A64HookFunction(fq, (void *)hk_FatalQuit, (void **)&orig_FatalQuit);
    void *tq = (void *)(base + kTryQuitIntRVA);
    A64HookFunction(tq, (void *)hk_TryQuitInt, (void **)&orig_TryQuitInt);
    if (orig_FatalQuit && orig_TryQuitInt)
        LOGI("quit tracing armed (fatal=%p try=%p)", fq, tq);
    else
        LOGE("A64HookFunction failed for quit tracing (fatal=%p try=%p)", fq, tq);

    // Meta build (app id configured): override the app id at the Oculus.Platform.Core init so the
    // native platform initialises against the operator's registered app and the real entitlement
    // + login flow runs (see kCoreInitRVA). No no-Meta sentinel hooks here.
    if (g_oculus_appid[0]) {
        void *coreInit = (void *)(base + kCoreInitRVA);
        A64HookFunction(coreInit, (void *)hk_CoreInit, (void **)&orig_CoreInit);
        LOGI("meta build: app-id override armed (Core init @%p) -> %s", coreInit, g_oculus_appid);

        // User Age Group mock: Meta's age-category call needs Data Use Checkup certification
        // and 403s without it. libovrplatformloader.so is loaded by the game, so poll for it,
        // then downgrade the age reply to success and report the user as non-child (>= 13).
        void *ovrm = nullptr;
        for (int i = 0; i < 600; i++) {  // up to ~60s
            ovrm = dlopen("libovrplatformloader.so", RTLD_NOLOAD | RTLD_NOW);
            if (ovrm) break;
            usleep(100 * 1000);
        }
        if (!ovrm) {
            LOGE("libovrplatformloader.so never appeared; age-group mock not armed");
            return nullptr;
        }
        ovr_GetType = (ovr_Message_GetType_t)dlsym(ovrm, "ovr_Message_GetType");
        void *isErrM = dlsym(ovrm, "ovr_Message_IsError");
        if (ovr_GetType && isErrM) {
            A64HookFunction(isErrM, (void *)hk_ovr_IsError_meta, (void **)&orig_ovr_IsError);
            LOGI("meta build: age-group IsError hook armed @ %p (age type=%d%s)", isErrM,
                 g_ageMessageType, g_ageMessageType ? "" : " -- diagnostic");
        } else {
            LOGE("ovr symbols missing for age mock: GetType=%p IsError=%p", (void *)ovr_GetType,
                 isErrM);
        }
        void *ageGet = dlsym(ovrm, "ovr_UserAccountAgeCategory_GetAgeCategory");
        if (ageGet) {
            A64HookFunction(ageGet, (void *)hk_age_GetAgeCategory,
                            (void **)&orig_age_GetAgeCategory);
            LOGI("meta build: age category getter armed @ %p -> Nch", ageGet);
        } else {
            LOGE("ovr_UserAccountAgeCategory_GetAgeCategory not found");
        }
        return nullptr;
    }

    // Deliberately last: libovrplatformloader.so is loaded by the game itself, so this polls,
    // and every il2cpp hook above must be armed before we block on that.
    void *ovr = nullptr;
    for (int i = 0; i < 600; i++) {  // up to ~60s
        ovr = dlopen("libovrplatformloader.so", RTLD_NOLOAD | RTLD_NOW);
        if (ovr) break;
        usleep(100 * 1000);
    }
    if (!ovr) {
        LOGE("libovrplatformloader.so never appeared; platform hooks not armed");
        return nullptr;
    }
    ovr_GetType = (ovr_Message_GetType_t)dlsym(ovr, "ovr_Message_GetType");
    void *isErr = dlsym(ovr, "ovr_Message_IsError");
    if (!ovr_GetType || !isErr) {
        LOGE("ovr symbols missing: GetType=%p IsError=%p", (void *)ovr_GetType, isErr);
        return nullptr;
    }
    A64HookFunction(isErr, (void *)hk_ovr_IsError, (void **)&orig_ovr_IsError);
    if (orig_ovr_IsError)
        LOGI("entitlement bypass armed @ ovr_Message_IsError %p (no app id configured)", isErr);
    else
        LOGE("A64HookFunction failed for ovr_Message_IsError @ %p", isErr);

    // Placeholder user id, so a missing session lands on the credential login instead of
    // the "Connecting to server..." hang. See kNoPlatformUserId.
    void *getLoggedIn = dlsym(ovr, "ovr_GetLoggedInUserID");
    if (getLoggedIn) {
        A64HookFunction(getLoggedIn, (void *)hk_ovr_GetLoggedInUserID,
                        (void **)&orig_ovr_GetLoggedInUserID);
        LOGI("placeholder user id armed @ ovr_GetLoggedInUserID %p -> %llu", getLoggedIn,
             kNoPlatformUserId);
    } else {
        LOGE("ovr_GetLoggedInUserID not found");
    }
    void *getUserId = dlsym(ovr, "ovr_User_GetID");
    if (getUserId) {
        A64HookFunction(getUserId, (void *)hk_ovr_User_GetID, (void **)&orig_ovr_User_GetID);
        LOGI("placeholder user id armed @ ovr_User_GetID %p", getUserId);
    }

    // Placeholder user proof, so "Failed to get Oculus Login" doesn't block the login flow
    // before the credential screen is reachable. See kPlaceholderNonce.
    void *msgProof = dlsym(ovr, "ovr_Message_GetUserProof");
    if (msgProof) {
        A64HookFunction(msgProof, (void *)hk_ovr_Message_GetUserProof,
                        (void **)&orig_ovr_Message_GetUserProof);
        LOGI("user-proof handle override armed @ %p", msgProof);
    }
    void *getNonce = dlsym(ovr, "ovr_UserProof_GetNonce");
    if (getNonce) {
        A64HookFunction(getNonce, (void *)hk_ovr_UserProof_GetNonce,
                        (void **)&orig_ovr_UserProof_GetNonce);
        LOGI("user-proof nonce override armed @ %p -> %s", getNonce, kPlaceholderNonce);
    }

    return nullptr;
}

__attribute__((constructor)) static void on_load() {
    LOGI("RECFLARE QUEST HOOKS loaded (build " __DATE__ " " __TIME__ ")");
    pthread_t t;
    pthread_create(&t, nullptr, init_thread, nullptr);
    pthread_detach(t);
}
