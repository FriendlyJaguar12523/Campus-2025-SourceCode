#pragma once

#include <windows.h>
#include <cstdint>
#include <string>
#include <iostream>
#include "../Methods.h"
#include "Console.h"
#include "../../Utils/globals.h"

// ================================================================================
// Custom in-game notifications
//
// Pops the game's own screen-space notification (the toast that slides in for XP,
// friend requests, etc.) with YOUR text - so the patch can talk to the player in
// the client instead of only the console.
//
// HOW IT WORKS
// NotificationManager.Play(type, text, duration, fade) is an INSTANCE method, so we
// need the live manager object. There is no get_Instance, so we capture `this` the
// first time Awake() runs (hooked below) and cache it. After that, ShowNotification()
// builds an il2cpp string and calls Play directly.
//
// Everything here is by GA + RVA from the runtime dump of THIS build (20250718.01),
// the same mechanism as every other hook in the patch. RVAs:
//   NotificationManager.Awake()                          0x22574D0
//   NotificationManager.Play(KNHAJHOEECB,string,float,float) -> IDisposable  0x225A4D0
//   enum KNHAJHOEECB (priority): Minor=0 Medium=1 Major=2 Vital=3
//
// SAFETY
//  - Play is only ever called once g_manager is non-null, i.e. AFTER the game created
//    the manager - never at inject time.
//  - The call is SEH-guarded; a bad call logs and returns instead of crashing.
//  - spoof_call caps at 4 args and Play needs 6, so this calls Play directly. That is
//    fine for a user-triggered feature; the return address is not spoofed, but the
//    module is already visible to the anti-cheat anyway (see CheatQuitSuppress).
// ================================================================================

namespace CustomNotify {

    inline constexpr uintptr_t AwakeRVA = 0x22574D0;
    inline constexpr uintptr_t PlayRVA  = 0x225A4D0;

    // Priority - maps to KNHAJHOEECB. Higher = more prominent / longer default styling.
    enum class Priority : int32_t { Minor = 0, Medium = 1, Major = 2, Vital = 3 };

    // The live NotificationManager, captured from Awake. Null until the manager exists.
    inline void* g_manager = nullptr;

    // Original Awake, filled by MinHook.
    using AwakeFn = void(*)(void* self, void* mi);
    inline AwakeFn Awake_O = nullptr;

    // NotificationManager.Play(this, type, text, duration, fade, MethodInfo*) -> IDisposable*
    using PlayFn = void* (*)(void* self, int32_t type, void* text, float duration, float fade, void* mi);

    // Hook: capture the manager instance, then run the real Awake so the game is
    // unaffected. Runs once (there is a single NotificationManager).
    inline void Awake_H(void* self, void* mi) {
        if (self && !g_manager) {
            g_manager = self;
            std::cout << TAG_INIT() << "NotificationManager captured @ 0x"
                << std::hex << (uintptr_t)self << std::dec
                << " - custom notifications ready\n";
        }
        if (Awake_O) Awake_O(self, mi);
    }

    // SEH-guarded raw call: build the managed string and invoke Play. Destructor-free
    // so __try is legal. Returns false on any fault.
    inline bool RawPlay(int32_t priority, const char* text, float duration, float fade) {
        __try {
            using StrNewFn = void* (*)(const char*);
            auto strNew = reinterpret_cast<StrNewFn>(GA + RR::Methods::Il2cpp::il2cpp_string_new);
            void* managed = strNew(text);
            if (!managed) return false;

            auto play = reinterpret_cast<PlayFn>(GA + PlayRVA);
            play(g_manager, priority, managed, duration, fade, nullptr);
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // Show a notification. No-ops safely (with a log) until the manager is captured -
    // i.e. until the game is far enough into startup to have one.
    inline bool Show(const char* text,
                     Priority priority = Priority::Medium,
                     float duration = 4.0f,
                     float fade = 0.5f) {
        if (!g_manager) {
            std::cout << TAG_WARN() << "notification skipped - NotificationManager not ready yet "
                         "(try again after the game has loaded)\n";
            return false;
        }
        if (!RR::Methods::Il2cpp::il2cpp_string_new) {
            std::cout << TAG_WARN() << "notification skipped - il2cpp_string_new unresolved\n";
            return false;
        }
        if (!text) text = "";

        // The raw call is SEH-guarded and kept free of anything with a destructor, so
        // __try is legal here (C2712). All logging happens in the caller below.
        bool ok = RawPlay((int32_t)priority, text, duration, fade);
        if (ok) std::cout << TAG_INIT() << "notification shown: \"" << text << "\"\n";
        else    std::cout << TAG_WARN() << "notification call faulted - suppressed\n";
        return ok;
    }
}
