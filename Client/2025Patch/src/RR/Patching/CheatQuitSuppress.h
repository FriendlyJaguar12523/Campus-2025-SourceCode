#pragma once

#include <windows.h>
#include <cstdint>
#include <iostream>
#include "Console.h"
#include "../../Utils/globals.h"

// ================================================================================
// CheatManager self-quit suppression  (PC)
//
// SYMPTOM: the game runs, then CheatManager decides the session is cheating and the
// client quits itself - in the session log this is "player/logout" followed a couple
// of seconds later by the process exiting. No crash dump, because it is not a crash.
//
// The 2025 client's CheatManager has several detectors (OnTimeCheatDetected,
// OnObscuredTypeCheatDetected, OnAdvancedMovementCheatDetected) plus a loaded-module
// scan, and they all end at one funnel:
//
//     SessionManager.FatalApplicationQuit(int reasonCode, string detail)
//
// Hook that funnel and the game cannot force-quit itself, whichever detector fired.
//
// ------------------------------------------------------------------------------
// WHY THIS VERSION IS DIFFERENT (and why the last few crashed)
// ------------------------------------------------------------------------------
// Earlier attempts resolved FatalApplicationQuit from IL2CPP METADATA on a background
// thread, which meant entering the runtime before il2cpp_init had finished - that is
// what crashed the game on startup.
//
// This version does NONE of that. It hooks GA + a hardcoded RVA at Patch() time, the
// EXACT mechanism every working hook in this patch already uses (Referee checks,
// HTTPManager.SendRequest, etc). No metadata, no runtime worker, no timing race.
//
// The RVA is valid because the runtime dump is from this precise build. Proof: the
// dump lists HTTPManager.SendRequest at RVA 0x77E0950, and Kyle's own session log
// shows that hook installed at 0x19999820950 = GA(0x19992040000) + 0x77E0950. The
// same GA+RVA math therefore lands FatalApplicationQuit correctly too.
//
//   Runtime dump: D:\Downloadsaww\RuntimeDump\dump.cs  (build 20250718.01, 2025-07-19)
//   Assembly-CSharp / <global ns> / SessionManager
//     FatalApplicationQuit(System.Int32, System.String)  RVA 0x2170A70  <- hooked
//     TryApplicationQuit(System.Int32)                   RVA 0x2177880  (backup target)
//
// PC ONLY. These RVAs are meaningless on Quest - different build, different arch.
// Quest has its own values in campus_hook.cpp.
// ================================================================================

namespace CheatQuit {

    // SessionManager.FatalApplicationQuit(int, string) - the fatal/cheat quit funnel.
    inline constexpr uintptr_t FatalApplicationQuit_RVA = 0x2170A70;

    // ---- detection sources (no-op'd so a quit is never TRIGGERED) --------------------
    //
    // Suppressing FatalApplicationQuit alone left the client soft-locked on login: the
    // anti-cheat still set its "detected" state, which gates login->dorm, even though we
    // blocked the actual quit. So we stop the detection at the source instead - the same
    // approach the Quest patch uses (no-op the module-scan closure). Each of these just
    // returns without doing anything, so no detection state is set and no quit is called.
    //
    //   LFKHKEPJKMB.LOELLDBOINB()  - the module-scan closure (body: FatalApplicationQuit)
    //   CheatManager.OnTimeCheatDetected / OnObscuredTypeCheatDetected /
    //   OnAdvancedMovementCheatDetected  - the runtime detectors
    inline constexpr uintptr_t ModuleScanClosureRVA   = 0x2148FB0;
    inline constexpr uintptr_t OnTimeCheatRVA         = 0x2135280;
    inline constexpr uintptr_t OnObscuredTypeCheatRVA = 0x21351D0;
    inline constexpr uintptr_t OnAdvancedMoveCheatRVA = 0x2134EA0;

    // All four are nullary instance methods: (this, MethodInfo*) -> void. One no-op fits.
    using NoOpFn = void(*)(void* self, void* mi);
    inline NoOpFn oModuleScan = nullptr, oTime = nullptr, oObscured = nullptr, oMove = nullptr;

    inline void NoOp_H(void* /*self*/, void* /*mi*/) {
        // Swallow the detection entirely - do NOT call the original.
        static bool once = false;
        if (!once) { once = true;
            std::cout << TAG_ANTI() << "cheat detector fired - neutralised at the source "
                         "(no quit, no soft-lock)\n"; }
    }

    // ---- mask the "quitting" state --------------------------------------------------
    //
    // Suppressing FatalApplicationQuit stops the process closing, but the game had
    // already flipped its quitting flags before calling it - and the room-join sequence
    // checks those and halts, leaving you stuck on the loading screen (requests stop,
    // no photon_access_token / room join). Force the getters to report NOT quitting so
    // the load carries on regardless.
    //   SessionManager.get_IsLoadingOrQuitting     0x21792E0
    //   SessionManager.get_IsQuittingOrLoggingOut  0x21793E0
    //   SessionManager.get_IsQuitting              0x2179460
    inline constexpr uintptr_t IsLoadingOrQuittingRVA    = 0x21792E0;
    inline constexpr uintptr_t IsQuittingOrLoggingOutRVA = 0x21793E0;
    inline constexpr uintptr_t IsQuittingRVA             = 0x2179460;

    using BoolGetFn = bool(*)(void* self, void* mi);
    inline bool ReturnFalse_H(void* /*self*/, void* /*mi*/) { return false; }

    // Anti-cheat reason codes seen calling FatalApplicationQuit:
    //   533223478  - from the Quest patch notes
    //   619916923  - observed on THIS PC build (session log, fired repeatedly with no
    //                detail while the module scan detected the injected DLL)
    // The detector fires FatalApplicationQuit with a numeric code and NO detail string,
    // whereas a genuine fatal error carries a message. So we suppress: a known code, OR
    // any no-detail fatal quit. Quits that carry a message pass through, so real crashes
    // still shut down. Every call is logged either way.
    inline constexpr int32_t kCheatQuitCodeQuest = 533223478;
    inline constexpr int32_t kCheatQuitCodePC    = 619916923;

    // Il2CppString* layout at native level (x64): length @ +0x10, UTF-16 chars @ +0x14.
    using Il2CppStr = void;
    using FatalQuitFn = void(*)(int32_t, Il2CppStr*, void* /*MethodInfo*/);
    inline FatalQuitFn oFatalQuit = nullptr;

    // SEH-guarded raw copy into a caller-owned buffer. No object with a destructor here,
    // so __try is legal (C2712). The managed string is not ours - a bad read must not
    // turn suppression into the crash it prevents.
    inline bool ReadStrRaw(void* s, char* out, int cap) {
        if (!s || !out || cap <= 0) return false;
        __try {
            int32_t len = *reinterpret_cast<int32_t*>((uintptr_t)s + 0x10);
            if (len <= 0 || len > 4096) return false;
            auto data = reinterpret_cast<const wchar_t*>((uintptr_t)s + 0x14);
            int n = WideCharToMultiByte(CP_UTF8, 0, data, len, out, cap - 1, nullptr, nullptr);
            if (n <= 0) return false;
            out[n] = '\0';
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    inline std::string ReadStr(void* s) {
        char buf[4096];
        return ReadStrRaw(s, buf, sizeof(buf)) ? std::string(buf) : std::string();
    }

    inline void FatalQuit_H(int32_t code, Il2CppStr* reason, void* method) {
        std::string msg = ReadStr(reason);

        // Anti-cheat signature: a known code, or a fatal quit with no detail at all.
        const bool suppress = code == kCheatQuitCodeQuest
                           || code == kCheatQuitCodePC
                           || msg.empty();

        if (suppress) {
            // Log the first few (throttled) so repeats don't flood, but a NEW code is
            // always worth seeing.
            static int shown = 0;
            if (shown < 5) {
                ++shown;
                std::cout << TAG_ANTI() << "CheatManager tried to quit (code " << code
                    << "): " << (msg.empty() ? "(no detail)" : msg)
                    << " - SUPPRESSED, game stays up\n";
            }
            return;   // swallow it - do NOT call the original
        }

        // Carries a real message - a genuine fatal error. Name it and let it happen.
        std::cout << TAG_WARN() << "FatalApplicationQuit(" << code << "): " << msg
            << "  [passed through - real error]\n";
        if (oFatalQuit) oFatalQuit(code, reason, method);
    }
}
