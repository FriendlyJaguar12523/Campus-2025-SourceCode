#pragma once

#include <windows.h>
#include <atomic>
#include <cstdint>
#include <iostream>
#include <cstdio>
#include <cstdarg>
#include <string>
#include "../Methods.h"
#include "Console.h"
#include "Il2CppApi.h"
#include "../../Utils/globals.h"
#include "../../Utils/deps/minhook/include/MinHook.h"

// ================================================================================
// Avatar fun - resize yourself, give yourself a big (or tiny) head.
//
// THREADING (this is the whole design, and the reason for the rewrite):
//   The ImGui panel is drawn on the D3D PRESENT thread. The first version called the game's
//   Player methods from there (after attaching that thread to the IL2CPP runtime). A crash
//   dump taken the moment INSERT was pressed showed the game's own thread faulting inside
//   UnityPlayer.dll with none of our frames on its stack - the signature of managed state being
//   disturbed from a thread that does not belong to it. The old Unity Explorer died the same way.
//
//   So now NOTHING managed runs on the present thread. The panel only writes plain atomics
//   (what you asked for). A hook on Player.Update - which the game runs on its MAIN thread, once
//   per frame per Player - does the work: it picks out the LOCAL player, applies the pending
//   request and reads the current scale back into an atomic the panel displays.
//
//   Assembly-CSharp / <global ns> / Player          (dump build 20250718.01; RVAs checked against
//   Tool.PhotonInstantiate = 0x21EFFE0, the one the patch already relies on)
//     void  Player.Update()                          RVA 0x20B2B70   (hooked)
//     bool  Player.get_IsLocal()                     RVA 0x20B5B40
//     float Player.get_DesiredAvatarScale()          RVA 0x20B55E0
//     void  Player.set_DesiredAvatarScale(float)     RVA 0x20B7D20
//     void  Player.SetHeadScale(float)               RVA 0x20B1B90
//
// WHAT TO EXPECT: DesiredAvatarScale is what the game's own self-scaling moves, so the avatar should
// grow/shrink like it does. If the game re-applies its own value, tick "keep applying" and it is
// re-asserted every frame. Whether OTHER players see it depends on the game syncing the value
// (PlayerAvatarNetworkSynchronization exists) - not assumed here.
// ================================================================================

namespace AvatarFun {

    inline constexpr uintptr_t UpdateRVA                = 0x20B2B70;
    inline constexpr uintptr_t IsLocalRVA               = 0x20B5B40;   // logged for comparison only, never called
    inline constexpr uintptr_t GetLocalPlayerRVA        = 0x20B6A20;   // static Player Player.get_LocalPlayer()
    inline constexpr uintptr_t GetDesiredAvatarScaleRVA = 0x20B55E0;
    inline constexpr uintptr_t SetDesiredAvatarScaleRVA = 0x20B7D20;
    inline constexpr uintptr_t SetHeadScaleRVA          = 0x20B1B90;

    // ---- shared state: the panel writes the wants, the main-thread hook writes what it saw ----
    inline std::atomic<float> g_wantBody{ 1.0f };
    inline std::atomic<float> g_wantHead{ 1.0f };
    inline std::atomic<bool>  g_pendingBody{ false };    // apply once, then clear
    inline std::atomic<bool>  g_pendingHead{ false };
    inline std::atomic<bool>  g_hold{ false };           // re-apply every frame while the box is ticked
    inline std::atomic<bool>  g_holdBodyActive{ false }; // a body scale has been requested (not reset)
    inline std::atomic<bool>  g_holdHeadActive{ false };

    inline std::atomic<float> g_seenBody{ -1.0f };       // what the game currently holds, -1 = not seen yet
    inline std::atomic<bool>  g_havePlayer{ false };     // the hook has run for the local player
    inline std::atomic<uint64_t> g_frames{ 0 };          // local-player Update calls seen (proves the hook runs)
    // how many times each call was made, and how many faulted (so "nothing happened" can be told from "never ran")
    inline std::atomic<uint32_t> g_bodyOk{ 0 }, g_bodyFail{ 0 }, g_headOk{ 0 }, g_headFail{ 0 };

    inline bool g_hooked = false;

    using UpdateFn   = void  (*)(void* self, void* mi);
    using GetLocalFn = void* (*)(void* mi);
    using GetFloatFn = float (*)(void* self, void* mi);
    using SetFloatFn = void  (*)(void* self, float v, void* mi);
    inline UpdateFn g_origUpdate = nullptr;

    // ---- file log: the Dev build has no console, so everything this feature does goes to <PATCH folder>\Logs\avatarfun.log ----
    inline std::atomic<uint64_t> g_anyFrames{ 0 };    // Player.Update calls for ANY player (proves the hook is firing at all)
    inline std::atomic<uint32_t> g_isLocalFault{ 0 };

    inline std::string LogPath() {
        static std::string path;
        if (!path.empty()) return path;
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(&LogPath), &self);
        char buf[MAX_PATH]{};
        GetModuleFileNameA(self, buf, MAX_PATH);
        std::string dir(buf);
        size_t slash = dir.find_last_of("\\/");
        dir = (slash == std::string::npos) ? "." : dir.substr(0, slash);
        CreateDirectoryA((dir + "\\Logs").c_str(), nullptr);
        path = dir + "\\Logs\\avatarfun.log";
        return path;
    }

    inline void Log(const char* fmt, ...) {
        FILE* f = nullptr;
        if (fopen_s(&f, LogPath().c_str(), "a") != 0 || !f) return;
        SYSTEMTIME t; GetLocalTime(&t);
        fprintf(f, "[%02d:%02d:%02d.%03d] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
        va_list ap; va_start(ap, fmt); vfprintf(f, fmt, ap); va_end(ap);
        fprintf(f, "\n");
        fclose(f);
    }

    // The exception code of the most recent guarded fault - logged so "FAULTED" says WHAT faulted (an access
    // violation 0xC0000005 is a bad call; 0xE06D7363 is a managed/C++ exception thrown by the game's own code).
    inline std::atomic<uint32_t> g_lastExc{ 0 };
    inline int Cap(unsigned long code) { g_lastExc.store(static_cast<uint32_t>(code)); return EXCEPTION_EXECUTE_HANDLER; }

    // ---- watch hooks (LOG ONLY - they change nothing): what the game itself passes to the two scale setters ----
    // Drink a head ("ego") potion or scale yourself with the Maker Pen and the log shows the exact value and whether it
    // was the game or us. There is no get_HeadScale on Player, so the last value seen here is also the panel's read-back.
    inline thread_local bool g_ours = false;                 // true while WE are inside a runtime invoke
    inline std::atomic<float>    g_lastHeadSeen{ -1.0f };
    inline std::atomic<float>    g_lastBodySetSeen{ -1.0f };
    inline std::atomic<uint32_t> g_headCalls{ 0 }, g_bodySetCalls{ 0 };
    inline void (*g_origSetHead)(void* self, float v, void* mi) = nullptr;
    inline void (*g_origSetBody)(void* self, float v, void* mi) = nullptr;

    inline void SetHeadScale_H(void* self, float v, void* mi) {
        g_lastHeadSeen.store(v);
        const uint32_t c = g_headCalls.fetch_add(1) + 1;
        if (c <= 40) Log("SetHeadScale(%.3f) called by %s on self=%p", v, g_ours ? "US" : "THE GAME", self);
        g_origSetHead(self, v, mi);
    }

    inline void SetBodyScale_H(void* self, float v, void* mi) {
        g_lastBodySetSeen.store(v);
        const uint32_t c = g_bodySetCalls.fetch_add(1) + 1;
        if (c <= 40) Log("set_DesiredAvatarScale(%.3f) called by %s on self=%p", v, g_ours ? "US" : "THE GAME", self);
        g_origSetBody(self, v, mi);
    }

    // ---- raw, SEH-guarded calls. Each is its own tiny function: __try cannot share a function with objects. ----
    // get_IsLocal is NOT used: its 16 bytes at 0x20B5B40 are shared by three unrelated methods (a folded stub) and
    // faulted on every call in the first log. "Is this my player" comes from the static get_LocalPlayer instead.
    inline bool RawGetLocalPlayer(void** out) {
        __try {
            *out = reinterpret_cast<GetLocalFn>(GA + GetLocalPlayerRVA)(nullptr);
            return true;
        }
        __except (Cap(GetExceptionCode())) { return false; }
    }

    inline bool RawGetFloat(uintptr_t rva, void* self, float* out) {
        __try {
            *out = reinterpret_cast<GetFloatFn>(GA + rva)(self, nullptr);
            return true;
        }
        __except (Cap(GetExceptionCode())) { return false; }
    }

    inline bool RawSetFloat(uintptr_t rva, void* self, float v) {
        __try {
            reinterpret_cast<SetFloatFn>(GA + rva)(self, v, nullptr);
            return true;
        }
        __except (Cap(GetExceptionCode())) { return false; }
    }

    // Read a few bytes of a function we are about to call (our own process, guarded) so the log shows what the
    // function really is - e.g. a 16-byte stub is "31 C0 C3" (return false) or "C3" (return), not a real getter.
    inline bool CopyBytes(const void* src, unsigned char* dst, int n) {
        __try { memcpy(dst, src, n); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    inline void LogBytes(const char* name, uintptr_t rva, int n) {
        unsigned char b[48]{};
        if (n > 48) n = 48;
        if (!CopyBytes(reinterpret_cast<const void*>(GA + rva), b, n)) { Log("bytes of %s (+0x%llX): UNREADABLE", name, (unsigned long long)rva); return; }
        char hex[48 * 3 + 1]{};
        for (int i = 0; i < n; ++i) snprintf(hex + i * 3, 4, "%02X ", b[i]);
        Log("bytes of %s (+0x%llX): %s", name, (unsigned long long)rva, hex);
    }

    // ---- calling the game's methods PROPERLY: by name through the IL2CPP runtime ----
    // Calling the baked addresses directly faulted for every instance method (even a plain getter): each of these
    // functions starts with "90 E9 <rel32>" and junk bytes (a jump stub into relocated code), and the call also went
    // out with a null MethodInfo. il2cpp_runtime_invoke takes the real MethodInfo, runs the method through the
    // game's own invoker and hands a managed exception back as a pointer instead of faulting.
    using RuntimeInvokeFn = void* (*)(void* method, void* obj, void** params, void** exc);
    using UnboxFn         = void* (*)(void* obj);
    inline RuntimeInvokeFn g_invoke = nullptr;
    inline UnboxFn         g_unbox  = nullptr;
    inline void* g_mSetBody = nullptr;   // MethodInfo* Player.set_DesiredAvatarScale(float)
    inline void* g_mGetBody = nullptr;   // MethodInfo* Player.get_DesiredAvatarScale()
    inline void* g_mSetHead = nullptr;   // MethodInfo* Player.SetHeadScale(float)
    inline bool  g_resolved = false;

    inline void Resolve() {
        if (g_resolved) return;
        g_resolved = true;                       // one attempt per session; the log says what it found
        Il2CppApi::ResolveExports();
        HMODULE ga = GetModuleHandleA("GameAssembly.dll");
        auto get = [&](const char* n) -> void* {
            if (void* p = (void*)GetProcAddress(ga, n)) return p;
            return UnityPlayerScan::find_il2cpp_api(n);
        };
        g_invoke = (RuntimeInvokeFn)get("il2cpp_runtime_invoke");
        g_unbox  = (UnboxFn)get("il2cpp_object_unbox");

        void* klass = Il2CppApi::FindClass("Assembly-CSharp", "", "Player");
        if (!klass) klass = Il2CppApi::FindClassAnywhere("", "Player");
        g_mSetBody = Il2CppApi::FindMethod(klass, "set_DesiredAvatarScale", 1);
        g_mGetBody = Il2CppApi::FindMethod(klass, "get_DesiredAvatarScale", 0);
        g_mSetHead = Il2CppApi::FindMethod(klass, "SetHeadScale", 1);

        Log("Resolve: il2cpp_runtime_invoke=%p object_unbox=%p Player class=%p", (void*)g_invoke, (void*)g_unbox, klass);
        auto off = [&](void* mi) -> unsigned long long {
            void* fp = Il2CppApi::MethodPointer(mi);
            return fp ? (unsigned long long)((uintptr_t)fp - GA) : 0ull;
        };
        Log("Resolve: set_DesiredAvatarScale mi=%p code=+0x%llX (dump says +0x%llX)", g_mSetBody, off(g_mSetBody), (unsigned long long)SetDesiredAvatarScaleRVA);
        Log("Resolve: get_DesiredAvatarScale mi=%p code=+0x%llX (dump says +0x%llX)", g_mGetBody, off(g_mGetBody), (unsigned long long)GetDesiredAvatarScaleRVA);
        Log("Resolve: SetHeadScale           mi=%p code=+0x%llX (dump says +0x%llX)", g_mSetHead, off(g_mSetHead), (unsigned long long)SetHeadScaleRVA);
    }

    inline bool InvokeSetFloat(void* method, void* self, float v) {
        if (!method || !g_invoke) return false;
        __try {
            void* exc = nullptr;
            float val = v;
            void* args[1] = { &val };
            g_ours = true;                       // so the watch hooks can tell our call from the game's
            g_invoke(method, self, args, &exc);
            g_ours = false;
            if (exc) { g_lastExc.store(0xE0000001u); return false; }   // the game threw a managed exception
            return true;
        }
        __except (Cap(GetExceptionCode())) { return false; }
    }

    inline bool InvokeGetFloat(void* method, void* self, float* out) {
        if (!method || !g_invoke || !g_unbox) return false;
        __try {
            void* exc = nullptr;
            void* boxed = g_invoke(method, self, nullptr, &exc);
            if (exc || !boxed) { g_lastExc.store(0xE0000001u); return false; }
            *out = *reinterpret_cast<float*>(g_unbox(boxed));
            return true;
        }
        __except (Cap(GetExceptionCode())) { return false; }
    }

    // ---- the hook: runs on the game's main thread, once per Player per frame ----
    inline void Update_H(void* self, void* mi) {
        g_origUpdate(self, mi);

        const uint64_t n = g_anyFrames.fetch_add(1) + 1;
        if (n == 1) {
            Log("Player.Update hook fired for the first time (self=%p)", self);
            // what the functions we call really are, now that the game's code is up
            LogBytes("Player.get_LocalPlayer", GetLocalPlayerRVA, 32);
            LogBytes("Player.get_IsLocal", IsLocalRVA, 16);
            LogBytes("Player.SetHeadScale", SetHeadScaleRVA, 16);
            LogBytes("Player.get_DesiredAvatarScale", GetDesiredAvatarScaleRVA, 32);
            LogBytes("Player.set_DesiredAvatarScale", SetDesiredAvatarScaleRVA, 32);
        }

        void* localPlayer = nullptr;
        if (!RawGetLocalPlayer(&localPlayer)) {
            if (g_isLocalFault.fetch_add(1) < 3) Log("get_LocalPlayer FAULTED (exception code 0x%08X)", g_lastExc.load());
            return;
        }
        if (n <= 3) Log("get_LocalPlayer -> %p (this Update's self=%p)", localPlayer, self);
        if (localPlayer == nullptr || localPlayer != self) return;

        g_havePlayer.store(true);
        const uint64_t lf = g_frames.fetch_add(1) + 1;
        if (lf == 1) Log("LOCAL player found (self=%p)", self);

        if (lf == 1) Resolve();     // class + methods by NAME, once, on this (the game's main) thread

        const bool hold = g_hold.load();
        if (g_pendingBody.exchange(false) || (hold && g_holdBodyActive.load())) {
            const float v = g_wantBody.load();
            const bool ok = InvokeSetFloat(g_mSetBody, self, v);
            if (ok) g_bodyOk.fetch_add(1); else g_bodyFail.fetch_add(1);
            float back = -1.0f; InvokeGetFloat(g_mGetBody, self, &back);
            if (g_bodyOk.load() + g_bodyFail.load() <= 20) Log("set body scale %.2f -> %s (exc 0x%08X), game now reads %.2f", v, ok ? "ok" : "FAIL", ok ? 0u : g_lastExc.load(), back);
        }
        if (g_pendingHead.exchange(false) || (hold && g_holdHeadActive.load())) {
            const float v = g_wantHead.load();
            const bool ok = InvokeSetFloat(g_mSetHead, self, v);
            if (ok) g_headOk.fetch_add(1); else g_headFail.fetch_add(1);
            if (g_headOk.load() + g_headFail.load() <= 20) Log("set head scale %.2f -> %s (exc 0x%08X)", v, ok ? "ok" : "FAIL", ok ? 0u : g_lastExc.load());
        }

        // A runtime invoke boxes its float result (a small allocation), so read the value back twice a second,
        // not every frame.
        float cur = g_seenBody.load();
        if (lf % 45 == 0 && InvokeGetFloat(g_mGetBody, self, &cur)) g_seenBody.store(cur);
        if (lf % 1800 == 0) Log("heartbeat: local frames=%llu any=%llu seenBody=%.2f bodyOk=%u headOk=%u",
                                (unsigned long long)lf, (unsigned long long)g_anyFrames.load(), cur, g_bodyOk.load(), g_headOk.load());
    }

    // Install once, from the injector thread right after the other hooks (MinHook is initialised by then).
    inline bool Install() {
        if (g_hooked) return true;
        void* target = reinterpret_cast<void*>(GA + UpdateRVA);
        MH_STATUS s = MH_CreateHook(target, reinterpret_cast<void*>(&Update_H), reinterpret_cast<void**>(&g_origUpdate));
        if (s == MH_OK) s = MH_EnableHook(target);
        g_hooked = (s == MH_OK);
        std::cout << TAG_INIT() << "AvatarFun: Player.Update hook " << (g_hooked ? "OK" : "FAILED") << "\n";
        Log("Install: GA=%p target=%p MinHook status=%d -> hook %s", (void*)GA, target, (int)s, g_hooked ? "OK" : "FAILED");

        // the two watch hooks (log-only); a failure here is logged and does not affect the rest
        auto watch = [&](uintptr_t rva, void* detour, void** orig, const char* name) {
            void* t = reinterpret_cast<void*>(GA + rva);
            MH_STATUS st = MH_CreateHook(t, detour, orig);
            if (st == MH_OK) st = MH_EnableHook(t);
            Log("Install: watch hook %s at %p -> MinHook status=%d (%s)", name, t, (int)st, st == MH_OK ? "OK" : "FAILED");
        };
        watch(SetHeadScaleRVA, reinterpret_cast<void*>(&SetHeadScale_H), reinterpret_cast<void**>(&g_origSetHead), "Player.SetHeadScale");
        watch(SetDesiredAvatarScaleRVA, reinterpret_cast<void*>(&SetBodyScale_H), reinterpret_cast<void**>(&g_origSetBody), "Player.set_DesiredAvatarScale");
        return g_hooked;
    }

    // ---- what the panel calls (render thread; touches only the atomics above) ----
    inline void RequestBody(float s) { g_wantBody.store(s); g_holdBodyActive.store(true); g_pendingBody.store(true); }
    inline void RequestHead(float s) { g_wantHead.store(s); g_holdHeadActive.store(true); g_pendingHead.store(true); }
    inline void RequestReset() {
        g_wantBody.store(1.0f); g_wantHead.store(1.0f);
        g_holdBodyActive.store(false); g_holdHeadActive.store(false);
        g_pendingBody.store(true);   g_pendingHead.store(true);
    }
}
