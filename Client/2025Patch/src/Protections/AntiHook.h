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
#include <memoryapi.h>
#include <mutex>
#include <iomanip>
#include <cstdint>

#include "../RR/Patching/Console.h"
#include "AntiKill.h"
#include "../RR/Methods.h"          // <-- use the single source of truth

#pragma comment(lib, "psapi.lib")

namespace AntiHook {

    static bool g_initialized = false;
    static std::mutex g_hookMutex;
    static std::set<uintptr_t> g_allowedOffsets;
    static HMODULE g_gameAssembly = nullptr;
    static HMODULE g_referee = nullptr;
    static uintptr_t g_gameAssemblyBase = 0;
    static uintptr_t g_refereeBase = 0;
    static size_t g_gameAssemblySize = 0;
    static size_t g_refereeSize = 0;
    static const size_t MAX_CHECK_SIZE = 64;

    static void InitializeAllowedOffsets() {
        g_allowedOffsets.insert(RR::Methods::LegacyTlsAuthentication::NotifyServerCertificate);
        g_allowedOffsets.insert(RR::Methods::HTTPManager::SendRequest);
        g_allowedOffsets.insert(RR::Methods::HttpResponse::get_DataAsText);
        g_allowedOffsets.insert(RR::Methods::HttpResponse::Receive);
        g_allowedOffsets.insert(RR::Methods::UIGradient::set_Gradient);
        g_allowedOffsets.insert(RR::Methods::SteamVR::SetFieldOfView);
        g_allowedOffsets.insert(RR::Methods::Il2cpp::il2cpp_string_new);
        g_allowedOffsets.insert(RR::Methods::Il2cpp::il2cpp_object_get_class);
        g_allowedOffsets.insert(RR::Methods::Il2cpp::il2cpp_object_new);
        g_allowedOffsets.insert(RR::Methods::Il2cpp::il2cpp_class_get_method_from_name);
        g_allowedOffsets.insert(RR::Methods::Il2cpp::il2cpp_class_get_parent);
        g_allowedOffsets.insert(RR::Methods::System::Uri::ToString);
        g_allowedOffsets.insert(RR::Methods::System::Uri::ctor);
        g_allowedOffsets.insert(RR::Methods::UnityGradient::SetKeys);
        g_allowedOffsets.insert(RR::Methods::UnityGradient::get_colorKeys);
        g_allowedOffsets.insert(RR::Methods::UnityGradient::get_alphaKeys);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadByName);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadRoom);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadInvention);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadPlayerEvent);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadAccount);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadAnnouncement);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::LoadBytes);
        g_allowedOffsets.insert(RR::Methods::RecNetImages::StaticConstructor);
        g_allowedOffsets.insert((uintptr_t)RR::Methods::ImageSignature::Verify); // fixed: was Outer
        g_allowedOffsets.insert(RR::Methods::Referee::Check1);
        g_allowedOffsets.insert(RR::Methods::Referee::Check2);
        g_allowedOffsets.insert(RR::Methods::Referee::Check3);
        g_allowedOffsets.insert(RR::Methods::Referee::Check4);

        Protections::log << TAG_ANTI() << "[HookProtection] Loaded " << g_allowedOffsets.size() << " allowed offsets\n";
    }

    static bool GetModuleInfo(HMODULE hModule, uintptr_t& base, size_t& size) {
        if (!hModule) return false;

        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)hModule;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;

        IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)((uintptr_t)hModule + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

        base = (uintptr_t)hModule;
        size = nt->OptionalHeader.SizeOfImage;
        return true;
    }

    static bool IsAddressInModule(uintptr_t address, uintptr_t base, size_t size) {
        return (address >= base && address < (base + size));
    }

    static bool IsHookInstruction(uint8_t* address, size_t& hookSize) {
        if (!address) return false;

        if (address[0] == 0xE9) {
            hookSize = 5;
            return true;
        }
        if (address[0] == 0xE8) {
            hookSize = 5;
            return true;
        }
        if (address[0] == 0xEB) {
            hookSize = 2;
            return true;
        }
        if (address[0] == 0xFF && address[1] == 0x25) {
            hookSize = 6;
            return true;
        }
        if (address[0] == 0xFF && address[1] == 0x15) {
            hookSize = 6;
            return true;
        }
        if (address[0] == 0x68) {
            if (address[5] == 0xC3) {
                hookSize = 6;
                return true;
            }
        }
        if (address[0] == 0x48 && address[1] == 0xB8) {
            if (address[10] == 0xFF && address[11] == 0xE0) {
                hookSize = 12;
                return true;
            }
        }
        return false;
    }

    static bool SafeReadMemory(uintptr_t address, uint8_t* buffer, size_t size) {
        __try {
            for (size_t i = 0; i < size; i++) {
                buffer[i] = *(uint8_t*)(address + i);
            }
            return true;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {
            return false;
        }
    }

    static bool ScanAllFunctionsForHooks() {
        Protections::log << TAG_ANTI() << "[HookProtection] Scanning all allowed functions for hooks...\n";

        bool allClean = true;

        if (g_gameAssemblyBase && g_gameAssemblySize) {
            for (uintptr_t offset : g_allowedOffsets) {
                uintptr_t address = g_gameAssemblyBase + offset;

                if (g_refereeBase && address >= g_refereeBase && address < g_refereeBase + g_refereeSize) {
                    continue;
                }

                uint8_t bytes[16] = { 0 };

                if (!SafeReadMemory(address, bytes, 16)) {
                    continue;
                }

                size_t hookSize = 0;
                if (IsHookInstruction(bytes, hookSize)) {
                    if (g_allowedOffsets.find(offset) != g_allowedOffsets.end()) {
                        continue;
                    }

                    Protections::log << TAG_ANTI() << "[!] UNAUTHORIZED HOOK DETECTED in GameAssembly!\n";
                    Protections::log << TAG_ANTI() << "[!] Offset: 0x" << std::hex << offset << std::dec << "\n";
                    Protections::log << TAG_ANTI() << "[!] Hook bytes: ";
                    for (size_t i = 0; i < hookSize && i < 16; i++) {
                        std::cout << std::hex << std::setw(2) << std::setfill('0') << (int)bytes[i] << " ";
                    }
                    std::cout << std::dec << "\n";

                    AntiKill::TriggerAntiCheat("Unauthorized hook in GameAssembly at offset 0x" +
                        std::to_string(offset));
                    allClean = false;
                }
            }
        }

        if (allClean) {
            Protections::log << TAG_ANTI() << "[HookProtection] All functions clean - no unauthorized hooks\n";
        }

        return allClean;
    }

    static DWORD WINAPI HookScannerThread(LPVOID) {
        Sleep(15000);

        Protections::log << TAG_ANTI() << "[HookProtection] Hook scanner thread started\n";
        Protections::log << TAG_ANTI() << "[HookProtection] Monitoring for unauthorized hooks...\n";

        while (true) {
            Sleep(5000);

            if (!ScanAllFunctionsForHooks()) {
                break;
            }
        }

        return 0;
    }

    static void Initialize() {
        if (g_initialized) {
            return;
        }
        g_initialized = true;

        Protections::log << TAG_ANTI() << "[HookProtection] Initializing anti-hook protection...\n";

        g_gameAssembly = GetModuleHandleA("GameAssembly.dll");
        g_referee = GetModuleHandleA("Referee.dll");

        if (!g_gameAssembly) {
            Protections::log << TAG_ANTI() << "[HookProtection] GameAssembly.dll not loaded!\n";
            return;
        }

        if (!GetModuleInfo(g_gameAssembly, g_gameAssemblyBase, g_gameAssemblySize)) {
            Protections::log << TAG_ANTI() << "[HookProtection] Failed to get GameAssembly info!\n";
            return;
        }

        if (g_referee) {
            GetModuleInfo(g_referee, g_refereeBase, g_refereeSize);
        }

        Protections::log << TAG_ANTI() << "[HookProtection] GameAssembly: 0x" << std::hex << g_gameAssemblyBase <<
            " - 0x" << (g_gameAssemblyBase + g_gameAssemblySize) << std::dec << "\n";
        if (g_referee) {
            Protections::log << TAG_ANTI() << "[HookProtection] Referee: 0x" << std::hex << g_refereeBase <<
                " - 0x" << (g_refereeBase + g_refereeSize) << std::dec << "\n";
        }

        InitializeAllowedOffsets();

        Protections::log << TAG_ANTI() << "[HookProtection] Performing initial scan...\n";
        ScanAllFunctionsForHooks();

        HANDLE hThread = CreateThread(NULL, 0, HookScannerThread, NULL, 0, NULL);
        if (hThread) {
            CloseHandle(hThread);
            Protections::log << TAG_ANTI() << "[HookProtection] Started hook scanner thread\n";
        }
        else {
            Protections::log << TAG_ANTI() << "[HookProtection] Failed to start hook scanner thread\n";
        }

        Protections::log << TAG_ANTI() << "[HookProtection] Anti-hook protection active\n";
    }

    static bool IsAllowedHookAddress(uintptr_t address) {
        uintptr_t offset = 0;

        if (IsAddressInModule(address, g_gameAssemblyBase, g_gameAssemblySize)) {
            offset = address - g_gameAssemblyBase;
        }
        else if (g_refereeBase && IsAddressInModule(address, g_refereeBase, g_refereeSize)) {
            offset = address - g_refereeBase;
        }
        else {
            return false;
        }

        std::lock_guard<std::mutex> lock(g_hookMutex);
        return g_allowedOffsets.find(offset) != g_allowedOffsets.end();
    }

    static void AddAllowedOffset(uintptr_t offset) {
        std::lock_guard<std::mutex> lock(g_hookMutex);
        g_allowedOffsets.insert(offset);
        Protections::log << TAG_ANTI() << "[HookProtection] Added allowed offset: 0x" << std::hex << offset << std::dec << "\n";
    }

    static void Shutdown() {
        g_initialized = false;
        Protections::log << TAG_ANTI() << "[HookProtection] Shutting down...\n";
    }

}