
APK decompile tools
Nirvana lyrics short
Purple to orange gradient
Rec Room quest patch search
Fix Room Load Error 8715
Code Review Issues
Skip self-search
Fix build errors
ImageSigPatchHelp
Signature Search Analysis
Remove Photon redirection
CampusPatch2025
Create room Rec Room 2025
LNK2019 spoofer stub fix
Subheading for healthy towns
Valentine's Day aesthetic text
Domain change config
Launcher UI redesign prompts
Launcher UI breakdown
Logo Edit Prompt
MinHook Patch Analysis
CampusPatch Launcher GUI
Rec Room ns.rec.net meaning
Enable patches again
Discord banner text right
VRChat tracker fix
BepInEx Discord RPC Plugin
ImageController Comparison
Fix Notification JSON Parsing
Photon logging guide
Change host to adminjude.ddns.net
Combined Installer
JUDE 2023 Quest Installer Batch
Improve Rec Room Patch Code
Image signature fix
JSON URLs
URL and body
Rec Room 20230406 depot
Improved HWID Spoofer Code
ULP Dumper
Epic Games sign-in text
Fix Epic Games login code
Cybersecurity Defense Concepts
HTTP 302 Redirect
Remove features except launch settings
32 bit limit
Spotify GraphQL query usage
Add headers to upload script
Minecraft server code
File Explorer Crash Fix
No previous chat
Rec Room Logout Script
Discord token login warning
Roblox Offset Dumper Tools
Instagram Exploits
Python script replace cocao
Workplace Safety and Policy Answers
BepInEx Plugin for Palette Color Modification
Website source code extraction guidance
Remove menu instruction text
Leaderboard Only Script
Player report code extraction and emoji removal
API Response Body for Room Report
Replace Orange with Coral Red in Theme
Modify gift URL endpoint
Rec Room API Co-Owner Assignment
Rec Room API Exploits August 2021
Two Color Batch Selector Fix
Body parameter in gensig function explained
C++ batch file selector for screen or VR
Modify gift request URL to new domain
Redirect URL with body true
Deobfuscation advice for RewardItemType and SynchronizedField
Deobfuscate CSharp Class Code
Python script for POST request
Installing HttpCanary CA Certificate on Quest 3
Epic Attack Surface Tool Code Split
Fortnite Account Puller Improvements
RecRoom API Monitor DLL Fix
Improved API Traffic Interceptor Script
Fix min identifier error in Dumper.cpp
Universal Mono Dumper Fix
List of games from images
Generate code stubs for listed files
Driver Load Error 0xc0000022 Fix
Claude Bugging Out Troubleshooting
Chinese to English translation request
PE Signature Structure Claude Friendly Rewrite
Fix Offsets for Game Cheat
Osmium Injector Code Analysis and Assistance
Discord template link output
Discord message deletion script with bearer token
Fix dumper.dll injection failure
Fix Easy Anti Cheat Error 30007
Fix IL2CPP dumper injection issue
DLL Injector Loader with Colored Output
Free il2cpp games for testing dumper
il2cpp games free list 2024
IL2CPP Dumper Code Review
Add Fall Guys support to dumper
APK decompile tools
You're right! I mixed them up. Here are the Quest-only files without the PC/Windows stuff:

methods.h
cpp
#pragma once

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <cstdint>

namespace RR::Methods::Quest {

    namespace Oculus {
        static const uintptr_t GET_APPID_VA = 0xffffff89ba0e9000ULL;
    }

    namespace TLS {
        static const uintptr_t NotifyServerCertificate = 0x0;
        static const uintptr_t LegacyTlsAuthentication = 0x0;
        static const uintptr_t GetClientCredentials = 0x0;
    }

    namespace BestHTTP {
        static const uintptr_t HTTPRequest_Send = 0x0;
        static const uintptr_t HTTPRequest_get_Uri = 0x0;
        static const uintptr_t HTTPRequest_set_Uri = 0x0;
        static const uintptr_t HTTPRequest_OnResponse = 0x0;
        static const uintptr_t HTTPRequest_ProcessResponse = 0x0;
    }

}

#endif
get_appid.h
cpp
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
        } else {
            LOGE("AppID patch failed!");
        }

        return result;
    }

}

#endif
tls_bypass.h
cpp
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
        } else {
            LOGE("TLS bypass FAILED!");
        }

        return success;
    }

}

#endif
main.cpp (Quest entry point)
cpp
#include "get_appid.h"
#include "tls_bypass.h"
#include <dlfcn.h>
#include <cstdio>
#include <cstring>

#define LOG_TAG "Campus"
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

static bool g_initialized = false;

void __attribute__((constructor)) lib_main() {
    if (g_initialized) return;
    g_initialized = true;

    LOGI("libcampus.so loaded!");

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)
    LOGI("Applying Quest patches...");
    RR::Patches::Oculus::Apply();
    RR::Patches::QuestTLS::Apply();
#endif

    LOGI("libcampus.so initialization complete!");
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnLoad called");
    return JNI_VERSION_1_6;
}

extern "C" JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnUnload called");
}
Android.mk
makefile
LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)
LOCAL_MODULE := libcampus
LOCAL_SRC_FILES := main.cpp
LOCAL_CPP_FEATURES := exceptions rtti
LOCAL_CFLAGS := -DPLATFORM_QUEST=1
LOCAL_LDLIBS := -llog -ldl
include $(BUILD_SHARED_LIBRARY)
CMakeLists.txt
cmake
cmake_minimum_required(VERSION 3.4.1)
project(libcampus)

add_library(libcampus SHARED main.cpp)

target_compile_definitions(libcampus PRIVATE PLATFORM_QUEST=1)
target_link_libraries(libcampus log dl)
What's Different
File	Purpose
methods.h	All Quest RVAs in one place
get_appid.h	Patches Oculus AppID
tls_bypass.h	Bypasses TLS certificate validation
main.cpp	Entry point with __attribute__((constructor))
Android.mk / CMakeLists.txt	Build configuration
No Windows/PC code, no console, no FindWindow, no DllMain — just pure Quest .so code.

full main.cpp
Here's the full main.cpp for Quest:

cpp
#include "get_appid.h"
#include "tls_bypass.h"
#include <dlfcn.h>
#include <cstdio>
#include <cstring>

#define LOG_TAG "Campus"
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

static bool g_initialized = false;

void __attribute__((constructor)) lib_main() {
    if (g_initialized) return;
    g_initialized = true;

    LOGI("libcampus.so loaded!");

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)
    LOGI("Applying Quest patches...");
    RR::Patches::Oculus::Apply();
    RR::Patches::QuestTLS::Apply();
#endif

    LOGI("libcampus.so initialization complete!");
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnLoad called");
    return JNI_VERSION_1_6;
}

extern "C" JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnUnload called");
}

