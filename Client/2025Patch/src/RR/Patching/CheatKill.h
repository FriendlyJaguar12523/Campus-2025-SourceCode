#pragma once

#include <windows.h>
#include <cstdint>
#include <iostream>
#include "Console.h"
#include "../../Utils/globals.h"

// ================================================================================
// CheatManager neutralise  (PC)  -  campus_killcheat.txt (opt-in, default OFF)
//
// HISTORY OF THIS FILE - so nobody re-treads it:
//   v1 "delete the gameobject": destroy the CheatManager GameObject from its Awake.
//      Needed UnityEngine Object.Destroy resolved by metadata at Awake time; that sweep
//      isn't reliable ~12s in, so it returned null and fell back to the original Awake,
//      whose DUID check then quit. We tested the fallback, not the kill.
//   v2 "skip Awake/Start": never run the original Awake, so the DUID check can't fire.
//      That CRASHED on load: CheatManager is a SingletonMonoBehaviour and the game reads
//      CheatManager.Instance during init - skipping Awake leaves Instance null -> NullRef.
//      => The game DEPENDS on this object. It cannot be deleted or skipped.
//
//   The decisive evidence: in the one run that stayed up, the managed detectors were
//   ALREADY no-op'd and the game STILL quit (player/logout ~12s after login). So the
//   managed CheatManager detectors are NOT what closes the game - Referee.dll (native)
//   or a managed path invisible in the encrypted PC build is.
//
// WHAT THIS VERSION DOES (surgical, cannot crash):
//   Let Awake / Start run normally (Instance is set, no NullRef). Neutralise only the two
//   things that could quit from the managed side:
//     1. CheatManager.CheckForDUIDMismatch(string&, string&) : bool  (RVA 0x2133600)
//        -> call the original (out-params + side effects intact), then FORCE-return false,
//           so a device-id mismatch is never reported and the caller never quits over it.
//     2. The runtime detectors + the scene-load callback -> no-op backstop, in case
//        anything reaches them directly.
//
// This is a DIAGNOSTIC as much as a fix: if the game still quits with this on, the DUID
// path is ruled out and the culprit is Referee (native). That is the next, separate job.
//
//   Assembly-CSharp / <global ns> / CheatManager : SingletonMonoBehaviour
//     CheckForDUIDMismatch(String&, String&):bool  RVA 0x2133600  <- forced false
//     NIFEDIAIEHP (SceneManager.sceneLoaded)        RVA 0x2134CB0  <- no-op
//     OnTimeCheatDetected                           RVA 0x2135280  <- no-op
//     OnObscuredTypeCheatDetected                   RVA 0x21351D0  <- no-op
//     OnAdvancedMovementCheatDetected               RVA 0x2134EA0  <- no-op
//
// PC ONLY. These RVAs are meaningless on Quest.
// ================================================================================

namespace CheatKill {

    inline constexpr uintptr_t DuidCheckRVA    = 0x2133600;
    inline constexpr uintptr_t SceneLoadedRVA  = 0x2134CB0;  // NIFEDIAIEHP
    inline constexpr uintptr_t OnTimeRVA       = 0x2135280;
    inline constexpr uintptr_t OnObscuredRVA   = 0x21351D0;
    inline constexpr uintptr_t OnMoveRVA       = 0x2134EA0;

    // CheckForDUIDMismatch(this, String** expected, String** actual, MethodInfo*) -> bool
    using DuidCheckFn = bool(*)(void* self, void** a, void** b, void* mi);
    inline DuidCheckFn oDuid = nullptr;

    using VoidFn = void(*)(void* self, void* mi);
    inline VoidFn oScene    = nullptr;   // NIFEDIAIEHP; no-op'd, extra args ignored
    inline VoidFn oTime     = nullptr;
    inline VoidFn oObscured = nullptr;
    inline VoidFn oMove     = nullptr;

    // Run the real check (SEH-guarded) so its out-params/side effects are intact, then
    // always answer "no mismatch". The managed string refs are the game's, not ours - a
    // bad read must not turn this into the crash it prevents.
    inline bool CallOrigDuid(void* self, void** a, void** b, void* mi) {
        if (!oDuid) return false;
        __try { return oDuid(self, a, b, mi); }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    inline bool DuidCheck_H(void* self, void** a, void** b, void* mi) {
        bool orig = CallOrigDuid(self, a, b, mi);
        static bool once = false;
        if (!once) { once = true;
            std::cout << TAG_ANTI() << "CheckForDUIDMismatch -> forced false (real result was "
                      << (orig ? "MISMATCH" : "ok") << ")\n"; }
        return false;   // never report a device-id mismatch
    }

    // Backstop: swallow any detector / scene-load callback reached directly.
    inline void NoOp_H(void* /*self*/, void* /*mi*/) {
        static bool once = false;
        if (!once) { once = true;
            std::cout << TAG_ANTI() << "CheatManager detector fired - neutralised (backstop)\n"; }
    }
}
