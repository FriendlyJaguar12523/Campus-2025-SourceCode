#pragma once

#include <windows.h>
#include <cstring>

// il2cpp API Resolution - falls back to UnityPlayer scan if exports stripped
namespace UnityPlayerScan {
    static void* find_il2cpp_api(const char* name) {
        HMODULE up = GetModuleHandleA("UnityPlayer.dll");
        HMODULE ga = GetModuleHandleA("GameAssembly.dll");
        if (!up || !ga) return nullptr;
        auto upBase = (uint8_t*)up;
        auto dos = (PIMAGE_DOS_HEADER)upBase;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
        auto nt = (PIMAGE_NT_HEADERS64)(upBase + dos->e_lfanew);
        if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
        const uint8_t* modEnd = upBase + nt->OptionalHeader.SizeOfImage;

        auto gaDos = (PIMAGE_DOS_HEADER)ga;
        auto gaNt = (PIMAGE_NT_HEADERS64)((uint8_t*)ga + gaDos->e_lfanew);
        const uint8_t* gaBase = (uint8_t*)ga;
        const uint8_t* gaEnd = gaBase + gaNt->OptionalHeader.SizeOfImage;

        size_t nameLen = strlen(name);
        auto sec = IMAGE_FIRST_SECTION(nt);
        for (WORD s = 0; s < nt->FileHeader.NumberOfSections; s++) {
            if (!(sec[s].Characteristics & IMAGE_SCN_CNT_CODE)) continue;
            uint8_t* code = upBase + sec[s].VirtualAddress;
            size_t codeSize = sec[s].Misc.VirtualSize;
            if (code + codeSize > modEnd) codeSize = (size_t)(modEnd - code);

            for (size_t off = 0; off + 7 < codeSize; off++) {
                uint8_t* p = code + off;
                if (!((p[0] == 0x48 || p[0] == 0x4C) && p[1] == 0x8D && (p[2] & 0xC7) == 0x05)) continue;
                int32_t disp = *(int32_t*)(p + 3);
                uint8_t* target = p + 7 + disp;
                if (target < upBase || target + nameLen + 1 >= modEnd) continue;
                if (memcmp(target, name, nameLen) != 0 || target[nameLen] != 0) continue;

                for (size_t k = 7; k < 128 && off + k + 7 < codeSize; k++) {
                    uint8_t* q = p + k;
                    if (q[0] == 0x48 && q[1] == 0x89 && (q[2] & 0xC7) == 0x05) {
                        int32_t d2 = *(int32_t*)(q + 3);
                        uint8_t* slot = q + 7 + d2;
                        if (slot >= upBase && slot + 8 <= modEnd) {
                            void* fn = *(void**)slot;
                            if (fn && (uint8_t*)fn >= gaBase && (uint8_t*)fn < gaEnd)
                                return fn;
                        }
                        break;
                    }
                }
            }
        }
        return nullptr;
    }
}