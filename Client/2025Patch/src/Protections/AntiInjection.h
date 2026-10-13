#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <set>
#include <string>
#include <iostream>
#include <vector>
#include <algorithm>
#include <tlhelp32.h>
#include <psapi.h>
#include <shlwapi.h>
#include <mutex>
#include <map>
#include "../RR/Patching/Console.h"
#include "AntiKill.h"

#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "shlwapi.lib")

namespace AntiInjection {

    static bool g_installed = false;

    static const std::set<std::string> kAllowedDLLs = {
        "GameAssembly.dll",
        "UnityPlayer.dll",
        "Referee.dll",
        "CampusPatch.dll",
        "steam_api64.dll",
        "steamclient64.dll",
        "discord_game_sdk.dll",
        "audioplugindissonance.dll",
        "gameoverlayrenderer64.dll",
        "tier0_s64.dll",
        "vstdlib_s64.dll"
    };

    static const std::vector<std::string> kBlacklistedDLLs = {
        "extremeinjector",
        "xenos",
        "dllinject",
        "sinject",
        "sharpinject",
        "injector",
        "cheatengine"
    };

    static std::set<std::string> GetLoadedDLLs() {
        std::set<std::string> result;

        HANDLE hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
        if (hSnapshot == INVALID_HANDLE_VALUE) {
            return result;
        }

        MODULEENTRY32 me32;
        me32.dwSize = sizeof(MODULEENTRY32);

        if (Module32First(hSnapshot, &me32)) {
            do {
                std::string dllName = me32.szModule;
                std::transform(dllName.begin(), dllName.end(), dllName.begin(), ::tolower);
                if (!dllName.empty()) {
                    result.insert(dllName);
                }
            } while (Module32Next(hSnapshot, &me32));
        }

        CloseHandle(hSnapshot);
        return result;
    }

    static bool GetLoadedDLLsRaw(std::string* dllNames, size_t maxCount, size_t& count) {
        count = 0;
        HANDLE hSnapshot = INVALID_HANDLE_VALUE;
        bool success = false;

        __try {
            hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
            if (hSnapshot != INVALID_HANDLE_VALUE) {
                MODULEENTRY32 me32;
                me32.dwSize = sizeof(MODULEENTRY32);

                if (Module32First(hSnapshot, &me32)) {
                    do {
                        if (count < maxCount) {
                            dllNames[count] = me32.szModule;
                            count++;
                        }
                        else {
                            break;
                        }
                    } while (Module32Next(hSnapshot, &me32));
                }
                success = true;
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            success = false;
        }

        if (hSnapshot != INVALID_HANDLE_VALUE) {
            CloseHandle(hSnapshot);
        }

        return success;
    }

    static DWORD WINAPI DLLScannerThread(LPVOID) {
        Sleep(60000);

        Protections::log << TAG_ANTI() << "DLL Scanner thread started\n";
        Protections::log << TAG_ANTI() << "Checking for cheat/injector DLLs and .tmp files\n";

        std::set<std::string> loadedDLLs;
        int scanCount = 0;

        {
            std::string rawDLLs[512];
            size_t count = 0;
            if (GetLoadedDLLsRaw(rawDLLs, 512, count)) {
                for (size_t i = 0; i < count; i++) {
                    std::string dll = rawDLLs[i];
                    std::transform(dll.begin(), dll.end(), dll.begin(), ::tolower);
                    loadedDLLs.insert(dll);
                }
            }
        }

        while (true) {
            Sleep(3000);

            std::string rawDLLs[512];
            size_t count = 0;

            if (!GetLoadedDLLsRaw(rawDLLs, 512, count)) {
                Protections::log << TAG_ANTI() << "Scan exception, skipping cycle\n";
                continue;
            }

            std::set<std::string> currentDLLs;
            for (size_t i = 0; i < count; i++) {
                std::string dll = rawDLLs[i];
                std::transform(dll.begin(), dll.end(), dll.begin(), ::tolower);
                if (!dll.empty()) {
                    currentDLLs.insert(dll);
                }
            }

            for (const auto& dll : currentDLLs) {
                if (loadedDLLs.find(dll) != loadedDLLs.end() || dll.empty()) {
                    continue;
                }

                Protections::log << TAG_ANTI() << "NEW DLL DETECTED: " << dll << "\n";

                bool isAllowed = false;
                for (const auto& allowed : kAllowedDLLs) {
                    std::string lowerAllowed = allowed;
                    std::transform(lowerAllowed.begin(), lowerAllowed.end(), lowerAllowed.begin(), ::tolower);
                    if (dll == lowerAllowed) {
                        isAllowed = true;
                        break;
                    }
                }

                if (isAllowed) {
                    Protections::log << TAG_ANTI() << "IGNORING " << dll << " (allowed)\n";
                    continue;
                }

                if (dll.find(".tmp") != std::string::npos) {
                    Protections::log << TAG_ANTI() << "[!] .tmp DLL DETECTED: " << dll << "\n";
                    AntiKill::TriggerAntiCheat("Suspicious .tmp DLL loaded: " + dll);
                    return 0;
                }

                bool isBlacklisted = false;
                for (const auto& bad : kBlacklistedDLLs) {
                    if (dll.find(bad) != std::string::npos) {
                        isBlacklisted = true;
                        break;
                    }
                }

                if (isBlacklisted) {
                    Protections::log << TAG_ANTI() << "[!] BLACKLISTED DLL: " << dll << "\n";
                    AntiKill::TriggerAntiCheat("Unauthorized DLL loaded: " + dll);
                    return 0;
                }

                Protections::log << TAG_ANTI() << "IGNORING " << dll << " (safe)\n";
            }

            loadedDLLs = currentDLLs;
            scanCount++;
            if (scanCount % 10 == 0) {
                Protections::log << TAG_ANTI() << "Scan #" << scanCount << " - " << loadedDLLs.size() << " loaded\n";
            }
        }

        return 0;
    }

    static void Install() {
        if (g_installed) {
            return;
        }
        g_installed = true;

        Protections::log << TAG_ANTI() << "Installing anti-injection protections...\n";
        Protections::log << TAG_ANTI() << "Allowed DLLs: ";
        for (const auto& dll : kAllowedDLLs) {
            std::cout << dll << " ";
        }
        std::cout << "\n";
        Protections::log << TAG_ANTI() << "Checking for: cheat/injector DLLs, .tmp files\n";

        HANDLE hThread = CreateThread(NULL, 0, DLLScannerThread, NULL, 0, NULL);
        if (hThread) {
            CloseHandle(hThread);
            Protections::log << TAG_ANTI() << "Started DLL scanner thread\n";
        }
        else {
            Protections::log << TAG_ANTI() << "Failed to start DLL scanner thread\n";
        }

        Protections::log << TAG_ANTI() << "Exception handler: DISABLED (prevents crashes)\n";
        Protections::log << TAG_ANTI() << "Anti-injection protections installed.\n";
    }

}