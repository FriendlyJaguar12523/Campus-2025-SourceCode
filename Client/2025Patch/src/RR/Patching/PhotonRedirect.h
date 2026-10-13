#pragma once
// ================================================================
// PHOTON -> CAMPUSNET REDIRECT  (PC)
// ================================================================
// The client's Photon settings come from the Campus server (app ids / "use name server") and
// PUN then resolves ns.exitgames.com / ns.photonengine.io itself. Every player has to land on
// the SAME network or they never meet in a room, so the swap is done where the Quest hook
// does it: at name resolution. Any Photon Cloud host name resolves to CAMPUS_PHOTON_SERVER
// (ServerConfiguration.h); the port and everything else the caller asked for is kept. No other
// name resolution is touched.
//
// Both ws2_32 entry points are hooked: IL2CPP's System.Net.Dns goes through getaddrinfo,
// other Unity / plugin code may take GetAddrInfoW.
//
// Blank CAMPUS_PHOTON_SERVER = leave Photon Cloud alone (no hooks installed).
#include <windows.h>
#include <iostream>
#include <cstring>
#include "ServerConfiguration.h"
#include "Console.h"
#include "../../Utils/deps/minhook/include/MinHook.h"

namespace PhotonRedirect {
    static const char* kHostPatterns[] = { "photonengine", "exitgames", "photonindustries" };

    static bool IsPhotonHostA(const char* node) {
        if (!node) return false;
        for (const char* p : kHostPatterns) if (strstr(node, p)) return true;
        return false;
    }
    static bool IsPhotonHostW(const wchar_t* node) {
        if (!node) return false;
        char buf[256];
        int n = WideCharToMultiByte(CP_UTF8, 0, node, -1, buf, sizeof(buf), nullptr, nullptr);
        return n > 0 && IsPhotonHostA(buf);
    }

    // Declared loosely so this header needs no winsock includes (windows.h is already in via
    // the other patches and winsock2.h must come BEFORE it - the addrinfo structs are only
    // passed through untouched, so opaque pointers are all that is needed).
    typedef int (WINAPI* getaddrinfo_t)(const char*, const char*, const void*, void**);
    typedef int (WINAPI* GetAddrInfoW_t)(const wchar_t*, const wchar_t*, const void*, void**);
    static getaddrinfo_t  getaddrinfo_O = nullptr;
    static GetAddrInfoW_t GetAddrInfoW_O = nullptr;
    static int g_logged = 0;

    static int WINAPI getaddrinfo_H(const char* node, const char* service, const void* hints, void** res) {
        if (IsPhotonHostA(node)) {
            if (g_logged < 8) { g_logged++; std::cout << "[Photon] dns " << node << " -> " << CAMPUS_PHOTON_SERVER << "\n"; }
            return getaddrinfo_O(CAMPUS_PHOTON_SERVER, service, hints, res);
        }
        return getaddrinfo_O(node, service, hints, res);
    }
    static int WINAPI GetAddrInfoW_H(const wchar_t* node, const wchar_t* service, const void* hints, void** res) {
        if (IsPhotonHostW(node)) {
            wchar_t target[128];
            MultiByteToWideChar(CP_UTF8, 0, CAMPUS_PHOTON_SERVER, -1, target, 128);
            if (g_logged < 8) { g_logged++; std::cout << "[Photon] dns(w) photon host -> " << CAMPUS_PHOTON_SERVER << "\n"; }
            return GetAddrInfoW_O(target, service, hints, res);
        }
        return GetAddrInfoW_O(node, service, hints, res);
    }

    static void Install() {
        if (!CAMPUS_PHOTON_SERVER || !CAMPUS_PHOTON_SERVER[0]) {
            std::cout << "[Photon] no CAMPUS_PHOTON_SERVER configured - Photon Cloud left as-is\n";
            return;
        }
        HMODULE ws = LoadLibraryA("ws2_32.dll");
        if (!ws) { std::cout << "[Photon] ws2_32.dll not loadable - redirect skipped\n"; return; }
        void* a = (void*)GetProcAddress(ws, "getaddrinfo");
        void* w = (void*)GetProcAddress(ws, "GetAddrInfoW");
        int ok = 0;
        if (a && MH_CreateHook(a, (void*)getaddrinfo_H, (LPVOID*)&getaddrinfo_O) == MH_OK && MH_EnableHook(a) == MH_OK) ok++;
        if (w && MH_CreateHook(w, (void*)GetAddrInfoW_H, (LPVOID*)&GetAddrInfoW_O) == MH_OK && MH_EnableHook(w) == MH_OK) ok++;
        std::cout << "[Photon] CampusNet redirect " << (ok ? "armed" : "FAILED")
                  << " (" << ok << "/2 resolvers) -> " << CAMPUS_PHOTON_SERVER << "\n";
    }
}
