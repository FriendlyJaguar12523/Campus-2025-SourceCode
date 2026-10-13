#include "UIThemes.h"
#include "ServerConfiguration.h"
#include "../../Discord/json.hpp"
#include "../../Utils/obf.h"
#include <winhttp.h>
#include <chrono>
#include <mutex>
#include <string>
#include <thread>

#pragma comment(lib, "winhttp.lib")

namespace UIThemes {

    static std::mutex g_mtx;
    static Theme g_theme = [] {
        // Until the server answers: the Campus gradient (purple -> magenta -> orange -> gold).
        Theme t;
        t.stops[0] = { 0.475f, 0.106f, 0.553f, 1.0f };
        t.stops[1] = { 0.749f, 0.161f, 0.400f, 1.0f };
        t.stops[2] = { 0.976f, 0.365f, 0.141f, 1.0f };
        t.stops[3] = { 0.980f, 0.600f, 0.071f, 1.0f };
        t.accent = { 1.0f, 0.6f, 0.1f, 1.0f };
        return t;
    }();
    static std::string g_lastBody;
    static std::atomic<uint64_t> g_account{ 0 };
    static std::atomic<bool> g_polling{ false };

    Theme Current() {
        std::lock_guard<std::mutex> lk(g_mtx);
        return g_theme;
    }

    // GET https://<server>/api/campus/theme/<id> - "" on any failure.
    static std::string Fetch(uint64_t accountId) {
        std::string out;
        std::wstring host(CAMPUS_API_HOST, CAMPUS_API_HOST + strlen(CAMPUS_API_HOST));
        std::wstring path = L"/api/campus/theme/" + std::to_wstring(accountId);
        HINTERNET s = WinHttpOpen(L"CampusPatch/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, nullptr, nullptr, 0);
        if (!s) return out;
        WinHttpSetTimeouts(s, 8000, 8000, 8000, 8000);
        HINTERNET c = WinHttpConnect(s, CampusHostW(host).c_str(), CampusPortOf(host), 0);
        HINTERNET r = c ? WinHttpOpenRequest(c, L"GET", path.c_str(), nullptr, nullptr, nullptr, WINHTTP_FLAG_SECURE) : nullptr;
        if (r) {
            DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID | SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
            WinHttpSetOption(r, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));
            // The patch signature: this request is the patch's own, not the game's, so it has to
            // carry the header the server's patch gate checks.
            std::wstring header = std::wstring(L"X-Campus-Patch: ") + (const wchar_t*)skCrypt(L"CAMPUS-2025-2h7Fc20FKokap9Ah3t") + L"\r\n";
            if (WinHttpSendRequest(r, header.c_str(), (DWORD)-1L, nullptr, 0, 0, 0) && WinHttpReceiveResponse(r, nullptr)) {
                DWORD status = 0, sz = sizeof(status);
                WinHttpQueryHeaders(r, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
                char buf[2048];
                DWORD got = 0;
                while (WinHttpReadData(r, buf, sizeof(buf), &got) && got > 0) out.append(buf, got);
                if (status != 200) out.clear();
            }
            WinHttpCloseHandle(r);
        }
        if (c) WinHttpCloseHandle(c);
        WinHttpCloseHandle(s);
        return out;
    }

    // Make Rec Room repaint its UI with the new colours (resize the game window by a pixel and back).
    static void ForceRedraw() {
        HWND w = FindWindowA(nullptr, "Campus");
        if (!w) w = FindWindowA(nullptr, "Rec Room");
        if (!w) w = FindWindowA(nullptr, "Unity Player");
        if (!w) return;
        RECT rc;
        if (!GetWindowRect(w, &rc)) return;
        int ww = rc.right - rc.left, wh = rc.bottom - rc.top;
        SetWindowPos(w, nullptr, 0, 0, ww + 1, wh, SWP_NOMOVE | SWP_NOZORDER);
        SetWindowPos(w, nullptr, 0, 0, ww, wh, SWP_NOMOVE | SWP_NOZORDER);
    }

    static bool Apply(const std::string& body) {
        try {
            auto j = nlohmann::json::parse(body);
            if (!j.value("ok", false)) return false;
            Theme t;
            t.enabled = j.value("enabled", true);
            t.tone = j.value("tone", 0);
            t.campus = j.value("preset", std::string("campus")) == "campus";
            auto col = [](const nlohmann::json& a) {
                Color c{ 1, 1, 1, 1 };
                if (a.is_array() && a.size() >= 3) { c.r = a[0].get<float>(); c.g = a[1].get<float>(); c.b = a[2].get<float>(); }
                return c;
            };
            auto& stops = j["stops"];
            if (!stops.is_array() || stops.size() != 4) return false;
            for (int i = 0; i < 4; i++) t.stops[i] = col(stops[i]);
            t.accent = col(j["accent"]);
            std::lock_guard<std::mutex> lk(g_mtx);
            g_theme = t;
            return true;
        }
        catch (...) { return false; }
    }

    static std::mutex g_refreshMtx;    // the poll and SetAccount can both call Refresh

    static void Refresh() {
        std::lock_guard<std::mutex> lk(g_refreshMtx);
        uint64_t acc = g_account.load();
        if (!acc) return;
        std::string body = Fetch(acc);
        if (body.empty() || body == g_lastBody) return;     // unreachable, or nothing changed
        if (!Apply(body)) return;
        g_lastBody = body;
        Theme t = Current();
        printf("[UIThemes] account %llu: %s\n", (unsigned long long)acc, t.enabled ? "gradient from the server" : "gradient off (Rec Room colours)");
        ForceRedraw();
    }

    void SetAccount(uint64_t accountId) {
        if (!accountId) return;
        uint64_t prev = g_account.exchange(accountId);
        if (prev != accountId) { std::lock_guard<std::mutex> lk(g_refreshMtx); g_lastBody.clear(); }
        if (g_polling.exchange(true)) {                      // already polling: just re-read now
            std::thread(Refresh).detach();
            return;
        }
        std::thread([] {
            for (;;) {
                Refresh();
                std::this_thread::sleep_for(std::chrono::seconds(30));
            }
        }).detach();
    }

}
