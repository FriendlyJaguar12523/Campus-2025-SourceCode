#pragma once

#include <iostream>
#include <string>
#include <cstdint>
#include "../Methods.h"
#include "Console.h"
#include "../../Utils/globals.h"
#include "../../Utils/Inc/Includes.h"
#include "../../Utils/deps/minhook/include/MinHook.h"

// NOTE: Il2cppString is defined via Includes.h (same as SendRequest.h).
// Do NOT redefine it locally here — a second, differently-named/laid-out
// struct will cause pointer-type-mismatch errors the moment a string
// crosses between this file and SendRequest.h.

namespace RR::Patches {

    namespace RadioMethods {
        uintptr_t Start = 0xD1B0A0;
        uintptr_t PlayNextTrack = 0xD1AA10;
        uintptr_t LJPIPALHKPH = 0xD1A390;
        uintptr_t GetNowPlayingString = 0xD1A000;
    }

    // Original function pointers
    void (*Radio_Start_O)(void*);
    void (*Radio_PlayNextTrack_O)(void*);
    void (*Radio_LJPIPALHKPH_O)(void*, int32_t);
    ::Il2cppString* (*Radio_GetNowPlayingString_O)(void*);

    // Custom song URL
    static const char* CUSTOM_SONG_URL = "https://ia600401.us.archive.org/14/items/mbdtf-cd/09.%20Runaway.flac";

    // Helper to create Il2Cpp strings
    static ::Il2cppString* CreateIl2CppString(const char* str) {
        using Il2cppStringNewFn = ::Il2cppString* (*)(const char*);
        auto strNew = reinterpret_cast<Il2cppStringNewFn>(
            GA + RR::Methods::Il2cpp::il2cpp_string_new
            );
        if (strNew) {
            return strNew(str);
        }
        return nullptr;
    }

    // Hook: Radio.Start
    void Radio_Start_H(void* __instance) {
        std::cout << TAG_NET() << "[Radio] Start called\n";
        std::cout << TAG_NET() << "[Radio] Custom song: " << CUSTOM_SONG_URL << "\n";

        if (Radio_Start_O) {
            Radio_Start_O(__instance);
        }
    }

    // Hook: LJPIPALHKPH - Intercept track loading
    void Radio_LJPIPALHKPH_H(void* __instance, int32_t trackIndex) {
        std::cout << TAG_NET() << "[Radio] Loading track " << trackIndex << " from Archive.org\n";
        std::cout << TAG_NET() << "[Radio] URL: " << CUSTOM_SONG_URL << "\n";

        if (Radio_LJPIPALHKPH_O) {
            Radio_LJPIPALHKPH_O(__instance, trackIndex);
        }
    }

    // Hook: GetNowPlayingString - Show custom song name
    ::Il2cppString* Radio_GetNowPlayingString_H(void* __instance) {
        ::Il2cppString* result = nullptr;
        if (Radio_GetNowPlayingString_O) {
            result = Radio_GetNowPlayingString_O(__instance);
        }

        std::cout << TAG_NET() << "[Radio] Now playing: Runaway - Kanye West (from Archive.org)\n";
        return CreateIl2CppString("Runaway - Kanye West (Archive.org)");
    }

    // Install radio patches
    static void InstallRadioPatches() {
#if 0
        // DISABLED — ruling this out while debugging the connect hang.
        // Flip to #if 1 to re-enable. No hooks are installed while off;
        // Radio.Start / LJPIPALHKPH / GetNowPlayingString run unpatched.
        std::cout << TAG_INIT() << "[Radio] Installing radio patches...\n";
        std::cout << TAG_INIT() << "[Radio] Custom song: " << CUSTOM_SONG_URL << "\n";

        if (!GA) {
            std::cout << TAG_INIT() << "[Radio] GameAssembly.dll not loaded!\n";
            return;
        }

        uintptr_t baseAddr = GA;

        // Hook Radio.Start
        uintptr_t startAddr = baseAddr + RadioMethods::Start;
        MH_STATUS s = MH_CreateHook(
            (void*)startAddr,
            (void*)Radio_Start_H,
            (LPVOID*)&Radio_Start_O
        );
        if (s == MH_OK) s = MH_EnableHook((void*)startAddr);
        std::cout << TAG_INIT() << "[Radio] Start hook -> " << (s == MH_OK ? "OK" : "FAIL") << "\n";

        // Hook LJPIPALHKPH (track loader)
        uintptr_t loadAddr = baseAddr + RadioMethods::LJPIPALHKPH;
        s = MH_CreateHook(
            (void*)loadAddr,
            (void*)Radio_LJPIPALHKPH_H,
            (LPVOID*)&Radio_LJPIPALHKPH_O
        );
        if (s == MH_OK) s = MH_EnableHook((void*)loadAddr);
        std::cout << TAG_INIT() << "[Radio] LJPIPALHKPH hook -> " << (s == MH_OK ? "OK" : "FAIL") << "\n";

        // Hook GetNowPlayingString
        uintptr_t nowAddr = baseAddr + RadioMethods::GetNowPlayingString;
        s = MH_CreateHook(
            (void*)nowAddr,
            (void*)Radio_GetNowPlayingString_H,
            (LPVOID*)&Radio_GetNowPlayingString_O
        );
        if (s == MH_OK) s = MH_EnableHook((void*)nowAddr);
        std::cout << TAG_INIT() << "[Radio] GetNowPlayingString hook -> " << (s == MH_OK ? "OK" : "FAIL") << "\n";

        std::cout << TAG_INIT() << "[Radio] Radio patches installed\n";
#else
        std::cout << TAG_INIT() << "[Radio] Radio patches DISABLED (skipped)\n";
#endif
    }

} // namespace RR::Patches