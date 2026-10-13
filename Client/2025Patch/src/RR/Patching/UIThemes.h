#pragma once
#include <atomic>
#include <cstdint>
#include <windows.h>
#include "Console.h"

// The UI gradient the patch paints Rec Room's UI with.
//
// Nothing is built in any more: each player picks theirs in the Campus launcher (presets or their
// own colours) and the SERVER keeps it per account, so it follows them to any PC. Once the game has
// signed in, SendRequest sees the account id (the Notifications hub URL) and calls SetAccount; the
// patch then reads /api/campus/theme/<id> and re-checks every 30 s, so a change made in the launcher
// shows in game without a restart. Until then (the login screen) it's the Campus gradient.
namespace UIThemes {

    struct Color {
        float r, g, b, a;
    };

    struct Theme {
        bool enabled = true;        // off = Rec Room's own colours, untouched
        Color stops[4]{};           // the gradient, left to right
        Color accent{};             // replaces Rec Room's orange (and tints its white)
        int tone = 0;               // everything else: 0 as is, 1 greyscale, 2 darkened
        bool campus = true;         // the Campus preset keeps the white line white
    };

    // The theme to draw with right now (a copy - safe from any thread).
    Theme Current();

    // The signed-in account (0 = none yet). Starts the server read + the 30 s re-check.
    void SetAccount(uint64_t accountId);

}
