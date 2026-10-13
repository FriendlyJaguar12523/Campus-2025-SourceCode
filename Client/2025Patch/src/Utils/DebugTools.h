#pragma once

// ================================================================================
// Opt-in switch for the experimental tooling.
//
// WHY THIS EXISTS
// The ImGui inspector, the sprite menu, the il2cpp metadata worker and the
// cheat-quit hook were all added on top of a patch that already WORKED. Each one
// touches the game in ways the base patch never did - hooking IDXGISwapChain::Present,
// replacing the window's WndProc, scanning UnityPlayer.dll for il2cpp exports, and
// polling the IL2CPP runtime from a thread it does not know about. Any of those is
// plausible as an anti-cheat trip or a startup hang, and together they took the game
// from "reaches the login screen" to "stuck on a black loading screen".
//
// So they are now OFF unless explicitly enabled. With no marker file the DLL does
// exactly what it did before this tooling existed: URL rewriting, TLS bypass, Referee
// hooks, Discord presence. Nothing hooks the renderer, nothing touches the runtime.
//
// TO ENABLE: create an empty file next to the DLL (or one folder up, beside Assets):
//     campus_debug.txt
//
// Checked once and cached - no per-frame disk access.
// ================================================================================

#include <windows.h>
#include <string>
#include <iostream>

namespace DebugTools {

    // Generic marker-file check: campus_<name>.txt next to the DLL, or one folder up.
    //
    // Separate flags on purpose. Every piece of this tooling has broken startup at
    // least once, and lumping them under one switch meant a single failure took the
    // whole lot down - including the cheat-quit suppression, which is the one thing
    // that is not optional. With independent flags a bad piece can be isolated in one
    // run instead of guessing.
    inline bool Flag(const char* name) {
        HMODULE self = nullptr;
        GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&Flag, &self);

        char buf[MAX_PATH]{};
        GetModuleFileNameA(self, buf, MAX_PATH);
        std::string dir(buf);
        size_t slash = dir.find_last_of("\\/");
        dir = (slash == std::string::npos) ? "." : dir.substr(0, slash);

        std::string file = std::string("campus_") + name + ".txt";
        const std::string candidates[] = { dir + "\\" + file, dir + "\\..\\" + file };

        for (const auto& c : candidates)
            if (GetFileAttributesA(c.c_str()) != INVALID_FILE_ATTRIBUTES) return true;
        return false;
    }

    inline bool g_checked = false;
    inline bool g_enabled = false;

    inline bool Enabled() {
        if (g_checked) return g_enabled;
        g_checked = true;

#ifdef CAMPUS_DEV_BUILD
        // The Dev-channel build is only ever downloaded by developer accounts, so the dev panel needs no marker file.
        g_enabled = true;
        std::cout << "[Campus] dev build: dev panel ENABLED (INSERT)\n";
        return true;
#endif

        HMODULE self = nullptr;
        GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCSTR)&Enabled, &self);

        char buf[MAX_PATH]{};
        GetModuleFileNameA(self, buf, MAX_PATH);
        std::string dir(buf);
        size_t slash = dir.find_last_of("\\/");
        dir = (slash == std::string::npos) ? "." : dir.substr(0, slash);

        const std::string candidates[] = {
            dir + "\\campus_debug.txt",
            dir + "\\..\\campus_debug.txt",
        };

        for (const auto& c : candidates) {
            if (GetFileAttributesA(c.c_str()) != INVALID_FILE_ATTRIBUTES) {
                g_enabled = true;
                std::cout << "[Campus] debug tools ENABLED (" << c << ")\n";
                std::cout << "[Campus]   Present hook + ImGui inspector + sprite menu + il2cpp metadata\n";
                return true;
            }
        }

        std::cout << "[Campus] debug tools off (create campus_debug.txt next to the DLL to enable)\n";
        g_enabled = false;
        return false;
    }
}
