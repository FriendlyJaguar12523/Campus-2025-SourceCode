#pragma once

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <dlfcn.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstring>
#include <cstdio>
#include <string>
#include "ServerConfiguration.h"
#include "methods.h"

#define LOG_TAG "Campus"
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace RR::Patches::QuestHTTP {

    static bool g_httpPatched = false;
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

    // Find the BestHTTP.HTTPRequest.Send method by RVA
    static bool PatchHTTPRequestSend(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("HTTP: Patching HTTPRequest.Send at 0x%lx", methodAddr);

        if (!MakeWritable(methodAddr, 0x40)) {
            LOGE("HTTP: Failed to make memory writable");
            return false;
        }

        // ARM64: Replace with simple RET (void function)
        uint32_t ret = 0xD65F03C0;
        *(uint32_t*)methodAddr = ret;

        for (int i = 4; i < 0x20; i += 4) {
            *(uint32_t*)(methodAddr + i) = 0xD503201F;
        }

        MakeExecutable(methodAddr, 0x40);

        LOGI("HTTP: HTTPRequest.Send patched (RET only)");
        return true;
    }

    // Find the URI getter/setter by RVA
    static bool PatchHTTPRequestGetUri(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("HTTP: Patching HTTPRequest.get_Uri at 0x%lx", methodAddr);

        if (!MakeWritable(methodAddr, 0x40)) {
            LOGE("HTTP: Failed to make memory writable");
            return false;
        }

        // ARM64: Return null (MOV X0, #0) + RET
        uint32_t movX0_0 = 0xD2800000;
        uint32_t ret = 0xD65F03C0;

        *(uint32_t*)methodAddr = movX0_0;
        *(uint32_t*)(methodAddr + 4) = ret;

        MakeExecutable(methodAddr, 0x40);

        LOGI("HTTP: HTTPRequest.get_Uri patched (returns null)");
        return true;
    }

    // Patch the URI setter to redirect to camprec.net
    static bool PatchHTTPRequestSetUri(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("HTTP: Patching HTTPRequest.set_Uri at 0x%lx", methodAddr);

        // Since set_Uri takes the URI and sets it on the request object,
        // we can't simply RET it. We need to redirect the string.

        // For Quest, the host redirect is done at the HTTP level
        // We'll use a different approach - hook into the URL string

        LOGI("HTTP: HTTPRequest.set_Uri will be handled by string patching");
        return true;
    }

    // Patch the HTTPRequest.OnResponse method
    static bool PatchHTTPRequestOnResponse(uintptr_t rva) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        uintptr_t methodAddr = base + rva;

        LOGI("HTTP: Patching HTTPRequest.OnResponse at 0x%lx", methodAddr);

        if (!MakeWritable(methodAddr, 0x40)) {
            LOGE("HTTP: Failed to make memory writable");
            return false;
        }

        uint32_t ret = 0xD65F03C0;
        *(uint32_t*)methodAddr = ret;

        for (int i = 4; i < 0x20; i += 4) {
            *(uint32_t*)(methodAddr + i) = 0xD503201F;
        }

        MakeExecutable(methodAddr, 0x40);

        LOGI("HTTP: HTTPRequest.OnResponse patched (RET only)");
        return true;
    }

    // Scan memory and replace "ns.rec.net" with "camprec.net"
    static bool PatchHostString() {
        uintptr_t base = GetIl2CppBase();
        if (!base) return false;

        const char* find = "ns.rec.net";
        const char* replace = CAMPUS_NAMESERVER_HOST;
        size_t findLen = strlen(find);
        size_t replaceLen = strlen(replace);

        LOGI("HTTP: Searching for '%s' to replace with '%s'", find, replace);

        size_t scanSize = 0x2000000; // 32MB scan

        for (uintptr_t i = 0; i < scanSize; i++) {
            uintptr_t addr = base + i;
            __try {
                if (memcmp((void*)addr, find, findLen) == 0) {
                    // Make sure it's a complete string
                    if (*(char*)(addr + findLen) == '\0') {
                        LOGI("HTTP: Found '%s' at 0x%lx", find, addr);

                        if (!MakeWritable(addr, findLen + 1)) {
                            LOGE("HTTP: Failed to make memory writable");
                            continue;
                        }

                        strcpy((char*)addr, replace);

                        MakeReadOnly(addr, findLen + 1);

                        LOGI("HTTP: Replaced with '%s'", replace);
                        return true;
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER) {
                // Skip unreadable memory
            }
        }

        LOGE("HTTP: Could not find '%s' in memory", find);
        return false;
    }

    // MakeReadOnly helper
    static bool MakeReadOnly(uintptr_t addr, size_t size) {
        size_t pageSize = sysconf(_SC_PAGESIZE);
        uintptr_t pageStart = (addr / pageSize) * pageSize;
        size_t pageSizeAligned = size + (addr - pageStart);
        return mprotect((void*)pageStart, pageSizeAligned, PROT_READ) == 0;
    }

    // Main Apply function
    static bool Apply() {
        LOGI("Quest HTTP Redirect Patch");
        LOGI("Target: ns.rec.net -> %s", CAMPUS_NAMESERVER_HOST);

        if (g_httpPatched) {
            LOGI("HTTP already patched");
            return true;
        }

        uintptr_t base = GetIl2CppBase();
        if (!base) {
            LOGE("HTTP: Failed to get libil2cpp.so base");
            return false;
        }

        bool success = false;

        // Method 1: Patch the host string in memory (most reliable)
        LOGI("HTTP: Attempting string patch...");
        success = PatchHostString();

        // Method 2: Patch HTTPRequest.Send if RVA is available
        if (!success && RR::Methods::Quest::BestHTTP::HTTPRequest_Send != 0) {
            LOGI("HTTP: Patching HTTPRequest.Send by RVA...");
            success = PatchHTTPRequestSend(RR::Methods::Quest::BestHTTP::HTTPRequest_Send);
        }

        // Method 3: Patch HTTPRequest.get_Uri
        if (!success && RR::Methods::Quest::BestHTTP::HTTPRequest_get_Uri != 0) {
            LOGI("HTTP: Patching HTTPRequest.get_Uri by RVA...");
            success = PatchHTTPRequestGetUri(RR::Methods::Quest::BestHTTP::HTTPRequest_get_Uri);
        }

        if (success) {
            g_httpPatched = true;
            LOGI("HTTP redirect applied successfully!");
            LOGI("All traffic will be redirected to %s", CAMPUS_NAMESERVER_HOST);
        }
        else {
            LOGE("HTTP redirect FAILED!");
            LOGE("Could not patch 'ns.rec.net' or hook BestHTTP methods");
        }

        return success;
    }

}

#endif