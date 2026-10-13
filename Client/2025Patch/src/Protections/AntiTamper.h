#pragma once
#include "AntiLog.h"

#include <windows.h>
#include <vector>
#include <iostream>
#include "../RR/Patching/Console.h"
#include "AntiKill.h"

namespace AntiTamper {

    static uint32_t CalculateChecksum(uintptr_t base, size_t size) {
        uint32_t checksum = 0;
        uint8_t* data = (uint8_t*)base;

        for (size_t i = 0; i < size; i++) {
            checksum = (checksum << 5) - checksum + data[i];
        }

        return checksum;
    }

    static bool GetModuleInfo(HMODULE hModule, uintptr_t& base, size_t& size) {
        IMAGE_DOS_HEADER* dos = (IMAGE_DOS_HEADER*)hModule;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;

        IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)((uintptr_t)hModule + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return false;

        base = (uintptr_t)hModule;
        size = nt->OptionalHeader.SizeOfImage;
        return true;
    }

    static bool VerifyIntegrity() {
        HMODULE hModule = GetModuleHandleA("CampusPatch.dll");
        if (!hModule) {
            Protections::log << TAG_ANTI() << "Could not verify integrity (module not found)\n";
            return true;
        }

        uintptr_t base;
        size_t size;
        if (!GetModuleInfo(hModule, base, size)) {
            Protections::log << TAG_ANTI() << "Could not verify integrity (invalid module)\n";
            return true;
        }

        uint32_t checksum = CalculateChecksum(base, size);

        static uint32_t sessionChecksum = checksum;

        if (checksum != sessionChecksum) {
            Protections::log << TAG_ANTI() << "Integrity check failed - DLL has been modified\n";
            Protections::log << TAG_ANTI() << "Expected: 0x" << std::hex << sessionChecksum << " Got: 0x" << checksum << std::dec << "\n";
            AntiKill::TriggerAntiCheat("Integrity check failed - DLL has been modified");
            return false;
        }

        Protections::log << TAG_ANTI() << "Integrity check passed\n";
        return true;
    }

    static void PrintChecksum() {
        HMODULE hModule = GetModuleHandleA("CampusPatch.dll");
        if (!hModule) {
            Protections::log << TAG_ANTI() << "Could not get module handle\n";
            return;
        }

        uintptr_t base;
        size_t size;
        if (!GetModuleInfo(hModule, base, size)) {
            Protections::log << TAG_ANTI() << "Could not get module info\n";
            return;
        }

        uint32_t checksum = CalculateChecksum(base, size);
        Protections::log << TAG_ANTI() << "DLL Checksum = 0x" << std::hex << checksum << std::dec << "\n";
    }

    static bool CheckForHooks() {
        return true;
    }

}