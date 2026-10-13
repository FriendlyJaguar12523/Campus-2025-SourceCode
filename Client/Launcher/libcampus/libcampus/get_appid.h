#pragma once

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <dlfcn.h>
#include <sys/mman.h>
#include <unistd.h>
#include <cstring>
#include <cstdio>
#include <string>
#include "methods.h"

#define LOG_TAG "Campus"
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace RR::Patches::Oculus {

    static const char* ORIGINAL_APPID = "2173678582678296";
    static const char* NEW_APPID = "1535967316729266206";
    static bool g_appidPatched = false;

    static uintptr_t GetIl2CppBase() {
        void* lib = dlopen("libil2cpp.so", RTLD_LAZY);
        if (!lib) {
            lib = dlopen("libil2cpp.so", RTLD_NOW);
        }
        if (!lib) {
            LOGE("Failed to load libil2cpp.so");
            return 0;
        }
        return (uintptr_t)lib;
    }

    static bool MakeWritable(uintptr_t addr, size_t size) {
        size_t pageSize = sysconf(_SC_PAGESIZE);
        uintptr_t pageStart = (addr / pageSize) * pageSize;
        size_t pageSizeAligned = size + (addr - pageStart);
        return mprotect((void*)pageStart, pageSizeAligned, PROT_READ | PROT_WRITE) == 0;
    }

    static bool MakeReadOnly(uintptr_t addr, size_t size) {
        size_t pageSize = sysconf(_SC_PAGESIZE);
        uintptr_t pageStart = (addr / pageSize) * pageSize;
        size_t pageSizeAligned = size + (addr - pageStart);
        return mprotect((void*)pageStart, pageSizeAligned, PROT_READ) == 0;
    }

    static std::string GetCurrentAppID() {
        uintptr_t base = GetIl2CppBase();
        if (!base) return "";

        uintptr_t va = RR::Methods::Quest::Oculus::GET_APPID_VA;
        uintptr_t rva = va - base;
        uintptr_t methodAddr = base + rva;

        LOGI("get_AppID method at: 0x%lx", methodAddr);

        typedef const char* (*GetAppIDFn)();
        GetAppIDFn getAppID = (GetAppIDFn)methodAddr;

        const char* appID = getAppID();
        if (appID) {
            LOGI("Current AppID: '%s'", appID);
            return std::string(appID);
        }

        LOGE("Failed to call get_AppID");
        return "";
    }

    static uintptr_t FindAppIDString(const char* appID) {
        uintptr_t base = GetIl2CppBase();
        if (!base) return 0;

        LOGI("Searching for AppID: '%s'", appID);

        size_t appIdLen = strlen(appID);
        size_t scanSize = 0x10000000;

        for (uintptr_t i = 0; i < scanSize; i++) {
            uintptr_t addr = base + i;
            if (memcmp((void*)addr, appID, appIdLen) == 0) {
                if (*(char*)(addr + appIdLen) == '\0') {
                    LOGI("Found AppID at: 0x%lx", addr);
                    return addr;
                }
            }
        }

        LOGE("Could not find AppID string");
        return 0;
    }

    static bool PatchAppIDString(const char* newAppID) {
        LOGI("Attempting to patch AppID to: '%s'", newAppID);

        std::string currentAppID = GetCurrentAppID();
        if (currentAppID.empty()) {
            LOGE("Could not get current AppID");
            return false;
        }

        uintptr_t stringAddr = FindAppIDString(currentAppID.c_str());
        if (!stringAddr) {
            LOGE("Could not find AppID string in memory");
            return false;
        }

        size_t currentLen = currentAppID.length();
        size_t newLen = strlen(newAppID);

        if (!MakeWritable(stringAddr, currentLen + 1)) {
            LOGE("Failed to make memory writable");
            return false;
        }

        strcpy((char*)stringAddr, newAppID);
        MakeReadOnly(stringAddr, currentLen + 1);

        LOGI("AppID patched! Old: %s, New: %s", currentAppID.c_str(), newAppID);

        g_appidPatched = true;
        return true;
    }

    static bool Apply() {
        LOGI("Oculus AppID Patch");

        if (g_appidPatched) {
            LOGI("AppID already patched");
            return true;
        }

        bool result = PatchAppIDString(NEW_APPID);

        if (result) {
            LOGI("AppID patch applied successfully!");
        }
        else {
            LOGE("AppID patch failed!");
        }

        return result;
    }

}

#endif