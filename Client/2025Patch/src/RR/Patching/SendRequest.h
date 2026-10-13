#pragma once
#include "UIThemes.h"
#include "PatchProof.h"
#include "../Methods.h"
#include "../../Utils/scan.h"
#include "../../Utils/obf.h"
#include "../../Utils/deps/spoofcall/RetSpoof.hpp"
#include "Console.h"
#include "ServerConfiguration.h"
#include "RefereeBypass.h"
#include "LoadTurbo.h"
#include "../../Discord/DiscordRPC_Presence.h"
#include <cstring>
#include <atomic>
#include <thread>

// NOTE: nop_O and nop_H are in RefereeBypass.h - DO NOT redefine here

void (*SendRequest_O)(void*);

// ================================================================
// IMAGE SIGNATURE HOOK - PROVEN WORKING
// Forces the image signature verification to always return true
// This is the SAME approach from the original working Patches.h
// ================================================================
bool (*VerifyImageSig_O)(void*, void*, void*) = nullptr;
bool VerifyImageSig_H(void* data, void* sig, void* mi) {
    static bool logged = false;
    if (!logged) {
        logged = true;
        bool orig = false;
        __try {
            if (VerifyImageSig_O) orig = VerifyImageSig_O(data, sig, mi);
        }
        __except (EXCEPTION_EXECUTE_HANDLER) {}
        std::cout << "[ImgSig] original=" << (orig ? 1 : 0) << " -> forcing TRUE\n";
    }
    return true;
}

// Helper: Convert wchar_t* to std::string
static std::string WStringToString(const wchar_t* wstr, int len) {
    if (!wstr || len <= 0) return "";
    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, wstr, len, nullptr, 0, nullptr, nullptr);
    if (utf8Len <= 0) return "";
    std::string result(utf8Len, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr, len, &result[0], utf8Len, nullptr, nullptr);
    return result;
}

// Helper: Find substring in Il2cppString buffer
static const wchar_t* FindSubstringInIl2cppString(Il2cppString* str, const wchar_t* find, int findLen) {
    if (!str || !find || findLen <= 0) return nullptr;

    int strLen = str->Length;
    if (strLen < findLen) return nullptr;

    const wchar_t* data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(str) + 4);

    for (int i = 0; i <= strLen - findLen; i++) {
        bool found = true;
        for (int j = 0; j < findLen; j++) {
            if (data[i + j] != find[j]) {
                found = false;
                break;
            }
        }
        if (found) {
            return data + i;
        }
    }
    return nullptr;
}

// Helper: Replace substring in Il2cppString with new string
static Il2cppString* ReplaceSubstringInIl2cppString(Il2cppString* original, const wchar_t* find, int findLen, const wchar_t* replace, int replaceLen) {
    if (!original || !find || !replace || findLen <= 0) return nullptr;

    const wchar_t* pos = FindSubstringInIl2cppString(original, find, findLen);
    if (!pos) return nullptr;

    int originalLen = original->Length;
    const wchar_t* data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(original) + 4);
    int offset = (int)(pos - data);

    int newLen = originalLen - findLen + replaceLen;

    wchar_t* newData = (wchar_t*)malloc((newLen + 1) * sizeof(wchar_t));
    if (!newData) return nullptr;

    memcpy(newData, data, offset * sizeof(wchar_t));
    memcpy(newData + offset, replace, replaceLen * sizeof(wchar_t));
    memcpy(newData + offset + replaceLen, data + offset + findLen, (originalLen - offset - findLen) * sizeof(wchar_t));
    newData[newLen] = L'\0';

    int utf8Len = WideCharToMultiByte(CP_UTF8, 0, newData, newLen, nullptr, 0, nullptr, nullptr);
    if (utf8Len <= 0) {
        free(newData);
        return nullptr;
    }

    std::string utf8Str(utf8Len, 0);
    WideCharToMultiByte(CP_UTF8, 0, newData, newLen, &utf8Str[0], utf8Len, nullptr, nullptr);
    free(newData);

    using Il2cppStringNewFn = Il2cppString * (*)(const char*);
    auto strNew = reinterpret_cast<Il2cppStringNewFn>(GA + RR::Methods::Il2cpp::il2cpp_string_new);
    if (!strNew) return nullptr;

    return strNew(utf8Str.c_str());
}

// NOTE: the X-Campus-Patch stamping that used to live here has been REMOVED.
//
// It called BestHTTP HTTPRequest.SetHeader on every outbound request to prove the client
// was running this patch. That is gone on BOTH platforms now (see campus_hook.cpp on Quest),
// so neither patch depends on a game method for the gate.
//
// CONSEQUENCE: the server can no longer tell a patched client from an unpatched one per
// request. PatchGateMiddleware.Enable MUST stay false, or every call 403s. See
// Middleware/PatchGateMiddleware.cs. The startup check (POST /api/patch/verify) still runs
// from the patch's OWN http client, which needs no game offsets.

void SendRequest_H(void* request) {
    void* uri = read<void*>(request, 16);
    if (!uri) {
        return SendRequest_O(request);
    }

    // STEP 1: Get the string from the Uri object using ToString
    using ToStringFn = Il2cppString * (*)(void*);
    auto toStringFn = reinterpret_cast<ToStringFn>(GA + RR::Methods::System::Uri::ToString);
    if (!toStringFn) {
        std::cout << TAG_WARN() << "ToString function is null!" << std::endl;
        return SendRequest_O(request);
    }

    Il2cppString* UriString = spoof_call(RetAddr, toStringFn, uri);
    if (!UriString) {
        std::cout << TAG_WARN() << "UriString is null!" << std::endl;
        return SendRequest_O(request);
    }

    // Read the returned string with the standard il2cpp x64 layout FIRST (length @ +16,
    // wchar_t data @ +20 after a 16-byte klass+monitor header), then fall back to the
    // legacy layout this codebase's Il2cppString struct declares (length @ +0, data @ +4).
    auto readStd = [](void* s) -> std::string {
        int32_t len = *reinterpret_cast<int32_t*>(reinterpret_cast<uintptr_t>(s) + 16);
        if (len <= 0 || len > 4096) return "";
        auto data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(s) + 20);
        return WStringToString(data, len);
        };
    auto readLegacy = [](Il2cppString* s) -> std::string {
        int32_t len = s->Length;
        if (len <= 0 || len > 4096) return "";
        auto data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(s) + 4);
        return WStringToString(data, len);
        };

    std::string urlStr = readStd(UriString);
    bool usedStd = !urlStr.empty();
    if (urlStr.empty()) urlStr = readLegacy(UriString);

    std::cout << TAG_NET() << "URI: " << urlStr
        << (urlStr.empty() ? " [empty - both layouts failed]" : (usedStd ? "" : " [legacy layout]"))
        << std::endl;

    LoadTurbo::OnRequest(urlStr);      // a room bundle download lifts the frame cap until the join result is reported

    // Discord presence: capture the local account id from the Notifications hub URL
    // (/Notifications/hub/v1?...id=<accountId>), which the client opens right after login. This is
    // how the presence learns WHO the local player is, so it can look up their own room + "Appear
    // Online To" setting.
    {
        size_t hubPos = urlStr.find("/Notifications/hub/v1");
        if (hubPos != std::string::npos) {
            size_t idPos = urlStr.find("id=", hubPos);
            if (idPos != std::string::npos) {
                uint64_t acc = 0;
                size_t i = idPos + 3;
                while (i < urlStr.size() && urlStr[i] >= '0' && urlStr[i] <= '9') {
                    acc = acc * 10 + (urlStr[i] - '0');
                    i++;
                }
                // Capture unconditionally: the hub URL is seen only ONCE (on connect), so gating
                // on IsRunning() would lose the id forever if presence wasn't up yet. SetLocalAccountId
                // just stores it (atomic) and the 10s poll uses it once presence is running.
                if (acc != 0) {
                    std::thread([acc]() { DiscordRPC::SetLocalAccountId(acc); }).detach();
                    UIThemes::SetAccount(acc);   // this account's UI gradient, from the server
                }
            }
        }
    }

    // The login gate: the server refuses a PC login without a fresh proof from the current patch,
    // so prove it just before the game's token request goes out (PatchProof.h).
    if (urlStr.find("/connect/token") != std::string::npos)
        PatchProof::BeforeLogin();

    // Discord presence: refresh on room URLs, and only when the room ID actually changes.
    {
        size_t roomsPos = urlStr.find("/Room_server/rooms/");
        if (roomsPos != std::string::npos) {
            size_t idStart = roomsPos + sizeof("/Room_server/rooms/") - 1;
            uint64_t id = 0;
            size_t i = idStart;
            while (i < urlStr.size() && urlStr[i] >= '0' && urlStr[i] <= '9') {
                id = id * 10 + (urlStr[i] - '0');
                i++;
            }
            static std::atomic<uint64_t> lastRoomId{ 0 };
            if (id != 0 && DiscordRPC::IsRunning() && lastRoomId.exchange(id) != id) {
                std::thread([]() {
                    DiscordRPC::RefreshPresence();
                    }).detach();
            }
        }
    }

    // Hile drop (HTTP-layer suppressor). Do this before the rewrite so we don't waste an
    // allocation on a URL we're about to swallow.
    if (urlStr.find("/api/PlayerReporting/v1/hile") != std::string::npos ||
        urlStr.find("/api/PlayerReporting/v1/referee") != std::string::npos) {
        std::cout << TAG_HILE() << "DROPPED " << urlStr << std::endl;
        return;   // suppress; do NOT call original
    }

    // ns.rec.net -> ns.camprec.net rewrite.
    size_t nsPos = urlStr.find("ns.rec.net");
    if (nsPos != std::string::npos) {
        std::string newUrl = urlStr;
        newUrl.replace(nsPos, sizeof("ns.rec.net") - 1, CAMPUS_NAMESERVER_HOST);

        using Il2cppStringNewFn = Il2cppString * (*)(const char*);
        auto strNew = reinterpret_cast<Il2cppStringNewFn>(GA + RR::Methods::Il2cpp::il2cpp_string_new);
        if (!strNew) {
            std::cout << TAG_WARN() << "il2cpp_string_new is null - cannot rewrite URL" << std::endl;
            return SendRequest_O(request);
        }
        Il2cppString* NewUriString = spoof_call(RetAddr, strNew, newUrl.c_str());
        if (!NewUriString) {
            std::cout << TAG_WARN() << "il2cpp_string_new returned null" << std::endl;
            return SendRequest_O(request);
        }

        using ObjectGetClassFn = void* (*)(void*);
        auto getClass = reinterpret_cast<ObjectGetClassFn>(GA + RR::Methods::Il2cpp::il2cpp_object_get_class);
        if (!getClass) { return SendRequest_O(request); }

        void* UriClass = spoof_call(RetAddr, getClass, uri);
        if (!UriClass) { return SendRequest_O(request); }

        using ObjectNewFn = void* (*)(void*);
        auto objNew = reinterpret_cast<ObjectNewFn>(GA + RR::Methods::Il2cpp::il2cpp_object_new);
        if (!objNew) { return SendRequest_O(request); }

        void* NewUri = spoof_call(RetAddr, objNew, UriClass);
        if (!NewUri) { return SendRequest_O(request); }

        using UriCtor = void (*)(void*, Il2cppString*);
        auto uriCtor = reinterpret_cast<UriCtor>(GA + RR::Methods::System::Uri::ctor);
        if (!uriCtor) { return SendRequest_O(request); }

        spoof_call(RetAddr, uriCtor, NewUri, NewUriString);
        set<void*>(request, 16, NewUri);

        std::cout << TAG_NET() << "REWRITTEN: " << newUrl << std::endl;
    }

    return SendRequest_O(request);
}

// Hook for HTTP Receive
bool (*Receive_O)(void*, int32_t, bool) = nullptr;
bool Receive_H(void* self, int32_t forceReadRawContentLength, bool readPayloadData) {
    if (Receive_O) {
        return Receive_O(self, forceReadRawContentLength, readPayloadData);
    }
    return false;
}

// Hook for GetDataAsText
::Il2cppString* (*GetDataAsText_O)(void*) = nullptr;
::Il2cppString* GetDataAsText_H(void* self) {
    ::Il2cppString* result = nullptr;
    if (GetDataAsText_O) {
        result = GetDataAsText_O(self);
    }
    if (result) {
        auto readStd = [](void* s) -> std::string {
            int32_t len = *reinterpret_cast<int32_t*>(reinterpret_cast<uintptr_t>(s) + 16);
            if (len <= 0 || len > 65536) return "";
            auto data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(s) + 20);
            return WStringToString(data, len);
            };
        auto readLegacy = [](::Il2cppString* s) -> std::string {
            int32_t len = s->Length;
            if (len <= 0 || len > 65536) return "";
            auto data = reinterpret_cast<const wchar_t*>(reinterpret_cast<uintptr_t>(s) + 4);
            return WStringToString(data, len);
            };

        std::string body = readStd(result);
        if (body.empty()) body = readLegacy(result);

        std::cout << TAG_NET() << "RESPONSE: " << (body.size() > 500 ? body.substr(0, 500) + "...(truncated)" : body) << std::endl;
    }
    else {
        std::cout << TAG_WARN() << "GetDataAsText returned null response\n";
    }
    return result;
}

thread_local bool g_inReceive = false;
thread_local bool g_inGetDataAsText = false;