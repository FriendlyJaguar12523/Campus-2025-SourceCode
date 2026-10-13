#pragma once

#include <string>
#include <windows.h>

// HTTP Debug - DISABLED (was breaking responses)
namespace HttpDebug {
    static void Init(HMODULE selfModule) { return; }
    static bool Matches(const std::string& url) { return false; }
    static void RecordLastUrl(const std::string& url) {}
    static void DumpResponse(const std::string& url, int status, const std::string& body) {}
    static void* SafeReadPtr(void* base, size_t off) { return nullptr; }
    static int32_t SafeReadInt32(void* base, size_t off) { return -1; }
}