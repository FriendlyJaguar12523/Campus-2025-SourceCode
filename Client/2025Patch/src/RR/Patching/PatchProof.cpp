#include "PatchProof.h"
#include "PatchProofKey.h"   // generated: the key for THIS build (release_app.py newkey)
#include "ServerConfiguration.h"
#include "../../Utils/obf.h"
#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <atomic>
#include <cstdio>
#include <ctime>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "bcrypt.lib")

namespace PatchProof {

    static std::string Hex(const unsigned char* p, size_t n) {
        static const char* d = "0123456789abcdef";
        std::string s;
        s.reserve(n * 2);
        for (size_t i = 0; i < n; i++) { s += d[p[i] >> 4]; s += d[p[i] & 15]; }
        return s;
    }

    // SHA-256 (key empty) or HMAC-SHA256 (key set) of data, as hex. "" on failure.
    static std::string Digest(const std::string& data, const std::string& key) {
        BCRYPT_ALG_HANDLE alg = nullptr;
        BCRYPT_HASH_HANDLE h = nullptr;
        unsigned char out[32];
        std::string res;
        if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, key.empty() ? 0 : BCRYPT_ALG_HANDLE_HMAC_FLAG) != 0) return res;
        if (BCryptCreateHash(alg, &h, nullptr, 0, key.empty() ? nullptr : (PUCHAR)key.data(), (ULONG)key.size(), 0) == 0 &&
            BCryptHashData(h, (PUCHAR)data.data(), (ULONG)data.size(), 0) == 0 &&
            BCryptFinishHash(h, out, sizeof out, 0) == 0)
            res = Hex(out, sizeof out);
        if (h) BCryptDestroyHash(h);
        BCryptCloseAlgorithmProvider(alg, 0);
        return res;
    }

    // The SHA-256 of this DLL's own file - which build this is. Read once.
    static std::string OwnSha() {
        static std::string sha;
        static std::once_flag once;
        std::call_once(once, [] {
            HMODULE self = nullptr;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                    (LPCWSTR)&OwnSha, &self)) return;
            wchar_t path[MAX_PATH * 2];
            DWORD n = GetModuleFileNameW(self, path, (DWORD)(sizeof path / sizeof path[0]));
            if (!n || n >= sizeof path / sizeof path[0]) return;
            std::ifstream f(path, std::ios::binary);
            std::string bytes((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
            if (!bytes.empty()) sha = Digest(bytes, "");
        });
        return sha;
    }

    // This PC's signed-in Steam account (the game logs in with it), or 0.
    static unsigned long long SteamId() {
        DWORD user = 0, size = sizeof(user);
        if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam\\ActiveProcess", L"ActiveUser",
                         RRF_RT_REG_DWORD, nullptr, &user, &size) != ERROR_SUCCESS || user == 0)
            return 0;
        return 76561197960265728ULL + user;
    }

    // The launcher-launch token the Campus launcher stuffs into CAMPUS_LAUNCH_TOKEN before it
    // CreateProcess's the game. The server validates it with LauncherLaunchProofs and refuses
    // logins without a fresh one (a legit patch running outside the launcher). Snapshot once at
    // first call: the env var is set only when the launcher spawned us, and nothing legitimate
    // changes it mid-process.
    static std::string LaunchToken() {
        static std::string tok;
        static std::once_flag once;
        std::call_once(once, [] {
            DWORD n = GetEnvironmentVariableA("CAMPUS_LAUNCH_TOKEN", nullptr, 0);
            if (n == 0 || n > 4096) return;
            std::string buf(n, '\0');
            DWORD got = GetEnvironmentVariableA("CAMPUS_LAUNCH_TOKEN", buf.data(), n);
            if (got == 0 || got >= n) return;
            buf.resize(got);
            tok = std::move(buf);
        });
        return tok;
    }

    // Minimal JSON string escaper for the one field we embed. Covers " \ and control bytes.
    static std::string JsonEscape(const std::string& s) {
        std::string r;
        r.reserve(s.size() + 2);
        for (char c : s) {
            switch (c) {
                case '"': r += "\\\""; break;
                case '\\': r += "\\\\"; break;
                case '\n': r += "\\n"; break;
                case '\r': r += "\\r"; break;
                case '\t': r += "\\t"; break;
                default:
                    if ((unsigned char)c < 0x20) { char b[8]; snprintf(b, sizeof b, "\\u%04x", c); r += b; }
                    else r += c;
            }
        }
        return r;
    }

    bool Send() {
        std::string sha = OwnSha();
        if (sha.empty()) { printf("[patchproof] couldn't read this patch's own file\n"); return false; }
        long long ts = (long long)time(nullptr);
        unsigned long long steam = SteamId();
        std::string msg = std::to_string(ts) + "|" + sha + "|" + std::to_string(steam);
        std::string mac = Digest(msg, std::string((const char*)skCrypt(CAMPUS_PATCH_PROOF_KEY)));
        if (mac.empty()) return false;
        std::string tok = LaunchToken();
        std::string body = "{\"ts\":" + std::to_string(ts) + ",\"sha\":\"" + sha + "\",\"steam\":\"" + std::to_string(steam) +
                           "\",\"mac\":\"" + mac + "\"";
        if (!tok.empty()) body += ",\"launchToken\":\"" + JsonEscape(tok) + "\"";
        body += "}";

        bool ok = false;
        DWORD status = 0;
        std::wstring host(CAMPUS_API_HOST, CAMPUS_API_HOST + strlen(CAMPUS_API_HOST));
        HINTERNET s = WinHttpOpen(L"CampusPatch/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY, nullptr, nullptr, 0);
        if (!s) return false;
        WinHttpSetTimeouts(s, 4000, 4000, 4000, 4000);
        HINTERNET c = WinHttpConnect(s, CampusHostW(host).c_str(), CampusPortOf(host), 0);
        HINTERNET r = c ? WinHttpOpenRequest(c, L"POST", L"/api/patch/proof", nullptr, nullptr, nullptr, WINHTTP_FLAG_SECURE) : nullptr;
        if (r) {
            DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID | SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
            WinHttpSetOption(r, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));
            const wchar_t* hdr = L"Content-Type: application/json\r\n";
            if (WinHttpSendRequest(r, hdr, (DWORD)-1L, (LPVOID)body.data(), (DWORD)body.size(), (DWORD)body.size(), 0) &&
                WinHttpReceiveResponse(r, nullptr)) {
                DWORD sz = sizeof(status);
                WinHttpQueryHeaders(r, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &sz, WINHTTP_NO_HEADER_INDEX);
                ok = status == 200;
            }
            WinHttpCloseHandle(r);
        }
        if (c) WinHttpCloseHandle(c);
        WinHttpCloseHandle(s);
        printf("[patchproof] %s (HTTP %lu, launchToken %s)%s\n", ok ? "accepted" : "REFUSED", status,
               tok.empty() ? "ABSENT - the launcher didn't start this game" : "sent",
               status == 409 ? " - this patch is out of date; restart the game from the Campus launcher" : "");
        return ok;
    }

    void BeforeLogin() {
        static std::atomic<long long> last{ 0 };
        long long now = (long long)time(nullptr);
        if (now - last.load() < 15) return;      // the game may retry the login request; one proof covers them
        last = now;
        Send();
    }
}
