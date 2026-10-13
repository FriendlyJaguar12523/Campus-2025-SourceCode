#pragma once

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <dlfcn.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstring>
#include <cstdio>
#include <vector>
#include "methods.h"

#define LOG_TAG "Campus"
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace RR::Patches::QuestTLS {

    static bool g_tlsPatched = false;
    static uintptr_t g_il2cppBase = 0;

    static uintptr_t GetIl2CppBase() {
        if (g_il2cppBase) return g_il2cppBase;

        void* lib = dlopen("libil2cpp.so", RTLD_LAZY);
        if (!lib) {
            lib = dlopen("libil2cpp.so", RTLD_NOW);
        }
        if (!lib) {
            LOGE("Failed to load libil2cpp.so");
            return 0;
        }
        g_il2cppBase = (uintptr_t)lib;
        LOGI("libil2cpp.so base: 0x%lx", g_il2cppBase);
        return g_il2cppBase;
    }

    static bool MakeWritable(uintptr_t addr, size_t size) {
        size_t pageSize = sysconf(_SC_PAGESIZE);
        uintptr_t pageStart = (addr / pageSize) * pageSize;
        size_t pageSizeAligned = size + (addr - pageStart);
        return mprotect((void*)pageStart, pageSizeAligned, PROT_READ | PROT_WRITE) == 0;
    }

    static bool MakeExecutable(uintptr_t addr, size_t size) {
        size_t pageSize = sysconf(_SC_PAGESIZE);
        uintptr_t pageStart = (addr / pageSize) * pageSize;
        size_t pageSizeAligned = size + (addr - pageStart);
        return mprotect((void*)pageStart, pageSizeAligned, PROT_READ | PROT_EXEC) == 0;
    }

    static bool PatchNotifyServerCertificate(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("TLS: Patching NotifyServerCertificate at 0x%lx", methodAddr);

        if (!MakeWritable(methodAddr, 0x40)) {
            LOGE("TLS: Failed to make memory writable");
            return false;
        }

        uint32_t ret = 0xD65F03C0;
        *(uint32_t*)methodAddr = ret;

        for (int i = 4; i < 0x20; i += 4) {
            *(uint32_t*)(methodAddr + i) = 0xD503201F;
        }

        MakeExecutable(methodAddr, 0x40);

        LOGI("TLS: NotifyServerCertificate patched (RET only)");
        return true;
    }

    static bool PatchGetClientCredentials(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("TLS: Patching GetClientCredentials at 0x%lx", methodAddr);

        if (!MakeWritable(methodAddr, 0x40)) {
            LOGE("TLS: Failed to make memory writable");
            return false;
        }

        uint32_t movX0_0 = 0xD2800000;
        uint32_t ret = 0xD65F03C0;

        *(uint32_t*)methodAddr = movX0_0;
        *(uint32_t*)(methodAddr + 4) = ret;

        MakeExecutable(methodAddr, 0x40);

        LOGI("TLS: GetClientCredentials patched (returns null)");
        return true;
    }

    static uintptr_t FindByPattern(const uint8_t* pattern, size_t patternLen, const char* name) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return 0;

        LOGI("TLS: Searching for %s by pattern...", name);

        for (uintptr_t i = 0; i < 0x2000000; i++) {
            uintptr_t addr = base + i;
            if (memcmp((void*)addr, pattern, patternLen) == 0) {
                LOGI("TLS: Found %s at 0x%lx", name, addr);
                return addr - base;
            }
        }

        LOGE("TLS: Could not find %s", name);
        return 0;
    }

    static bool Apply() {
        LOGI("Quest TLS Bypass");

        if (g_tlsPatched) {
            LOGI("TLS already patched");
            return true;
        }

        uintptr_t base = GetIl2CppBase();
        if (!base) {
            LOGE("TLS: Failed to get libil2cpp.so base");
            return false;
        }

        bool success = false;

        if (RR::Methods::Quest::TLS::NotifyServerCertificate != 0) {
            LOGI("TLS: Using known RVA for NotifyServerCertificate");
            success = PatchNotifyServerCertificate(RR::Methods::Quest::TLS::NotifyServerCertificate);
            if (success) {
                g_tlsPatched = true;
                return true;
            }
        }

        uint8_t pattern1[] = {
            0xFF, 0x03, 0x01, 0xD1,
            0xE8, 0x07, 0x00, 0xF9,
            0xF7, 0x53, 0x00, 0xA9,
        };

        uintptr_t rva = FindByPattern(pattern1, sizeof(pattern1), "NotifyServerCertificate");
        if (rva) {
            success = PatchNotifyServerCertificate(rva);
            if (success) {
                g_tlsPatched = true;
                return true;
            }
        }

        LOGI("TLS: Using environment variable bypass");
        setenv("SSL_CERT_FILE", "/dev/null", 1);
        setenv("SSL_CERT_DIR", "/dev/null", 1);
        setenv("OPENSSL_CONF", "/dev/null", 1);
        success = true;
        g_tlsPatched = true;

        if (success) {
            LOGI("TLS bypass applied successfully!");
        }
        else {
            LOGE("TLS bypass FAILED!");
        }

        return success;
    }

}

#endif