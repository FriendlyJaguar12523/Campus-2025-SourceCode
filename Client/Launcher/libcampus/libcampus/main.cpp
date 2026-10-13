#include "get_appid.h"
#include "tls_bypass.h"

#include <cstdio>

#if defined(__ANDROID__) || defined(PLATFORM_QUEST)

#include <dlfcn.h>
#include <cstring>
#include <android/log.h>

#define LOG_TAG "Campus"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, LOG_TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

static bool g_initialized = false;

void __attribute__((constructor)) lib_main() {
    if (g_initialized) return;
    g_initialized = true;

    LOGI("libcampus.so loaded!");

    LOGI("Applying Quest patches...");
    RR::Patches::Oculus::Apply();
    RR::Patches::QuestTLS::Apply();

    LOGI("libcampus.so initialization complete!");
}

extern "C" JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnLoad called");
    return JNI_VERSION_1_6;
}

extern "C" JNIEXPORT void JNICALL JNI_OnUnload(JavaVM* vm, void* reserved) {
    LOGI("JNI_OnUnload called");
}

#else

// Windows fallback - just prints to console
static bool g_initialized = false;

void lib_main() {
    if (g_initialized) return;
    g_initialized = true;
    printf("libcampus.so loaded! (Windows fallback - Quest only)\n");
}

#endif