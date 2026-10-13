#include "Steam.h"
#include "Settings.h"
#include "Util.h"
#include <windows.h>
#include <cstring>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;

static constexpr uint64_t kSteamId64Base = 76561197960265728ULL;   // individual account, public universe
static constexpr const wchar_t* kAppId = L"471710";                 // Rec Room - what the game runs as

uint64_t LocalSteamId()
{
    DWORD user = 0, size = sizeof(user);
    if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Valve\\Steam\\ActiveProcess", L"ActiveUser",
                     RRF_RT_REG_DWORD, nullptr, &user, &size) != ERROR_SUCCESS || user == 0)
        return 0;
    return kSteamId64Base + user;
}

// steam_api64's flat C API (the SDK the game ships exports SteamAPI_SteamUser_v020)
using InitFn = bool(__cdecl*)();
using ShutdownFn = void(__cdecl*)();
using RunCallbacksFn = void(__cdecl*)();
using UserFn = void*(__cdecl*)();
using GetSteamIdFn = uint64_t(__cdecl*)(void*);
// Newer SDKs added a 4th argument (the identity the ticket is for); passing null suits both - an
// older steam_api simply ignores the extra register.
using TicketFn = uint32_t(__cdecl*)(void*, void*, int, uint32_t*, const void*);

const void* ResourceData(int id, size_t& size);   // Tex.cpp
static constexpr int kSteamApiRes = 220;            // Launcher.rc: the steam_api64.dll the game zip ships

static fs::path GameSteamApi(const std::string& gameDir)
{
    return U8Path(gameDir) / L"Recroom_Release_Data" / L"Plugins" / L"x86_64" / L"steam_api64.dll";
}

static bool WriteWhole(const fs::path& dest, const void* data, size_t n)
{
    std::error_code ec;
    fs::create_directories(dest.parent_path(), ec);
    fs::path tmp = dest; tmp += L".campus-tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        out.write(static_cast<const char*>(data), (std::streamsize)n);
        out.flush();
        if (!out) { out.close(); fs::remove(tmp, ec); return false; }
    }
    if (!MoveFileExW(tmp.c_str(), dest.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) { fs::remove(tmp, ec); return false; }
    return true;
}

// Present and a believable DLL: not cut short, starts with MZ and has a PE header. (A DIFFERENT but intact
// copy is left alone - a game update may ship a newer Steam SDK than the one the launcher carries.)
static bool LooksIntact(const fs::path& p, size_t embeddedSize)
{
    std::error_code ec;
    if (!fs::is_regular_file(p, ec)) return false;
    auto size = fs::file_size(p, ec);
    if (ec || size < embeddedSize / 2) return false;
    std::ifstream f(p, std::ios::binary);
    unsigned char head[0x40]{};
    if (!f.read(reinterpret_cast<char*>(head), sizeof head) || head[0] != 'M' || head[1] != 'Z') return false;
    uint32_t pe = *reinterpret_cast<uint32_t*>(head + 0x3C);
    if (pe == 0 || pe > size - 4) return false;
    f.seekg(pe);
    char sig[4]{};
    return f.read(sig, 4) && std::memcmp(sig, "PE\0\0", 4) == 0;
}

bool EnsureSteamApi(const std::string& gameDir, std::string& note)
{
    note.clear();
    size_t n = 0;
    const void* mine = ResourceData(kSteamApiRes, n);
    if (!mine || !n || gameDir.empty()) return true;           // nothing to restore from
    fs::path dll = GameSteamApi(gameDir);
    if (LooksIntact(dll, n)) return true;
    std::error_code ec;
    const bool missing = !fs::exists(dll, ec);
    if (WriteWhole(dll, mine, n)) { note = missing ? "The game's steam_api64.dll was missing - put it back." : "The game's steam_api64.dll was damaged - replaced it."; return true; }
    note = "The game's steam_api64.dll is " + std::string(missing ? "missing" : "damaged") + " and the launcher couldn't write a new one to " + PathU8(dll.parent_path()) +
           " (is the game running, or is the folder read-only?).";
    return false;
}

bool GetSteamTicket(const std::string& gameDir, std::string& hex, uint64_t& steamId, std::string& error)
{
    hex.clear(); steamId = 0; error.clear();
    std::string restoreNote;
    EnsureSteamApi(gameDir, restoreNote);                  // a missing / cut-short copy is the usual reason this link failed
    fs::path dll = GameSteamApi(gameDir);
    std::error_code ec;
    if (!fs::is_regular_file(dll, ec)) dll = U8Path(gameDir) / L"steam_api64.dll";
    if (!fs::is_regular_file(dll, ec))
    {
        // Couldn't put it back in the game folder: use the launcher's own copy for the link (the game still needs its own)
        size_t n = 0;
        const void* mine = ResourceData(kSteamApiRes, n);
        fs::path own = U8Path(AppDataDir()) / L"steam" / L"steam_api64.dll";
        if (mine && n && (fs::is_regular_file(own, ec) || WriteWhole(own, mine, n))) dll = own;
        else { error = restoreNote.empty() ? "the game's steam_api64.dll wasn't found" : restoreNote; return false; }
    }

    // the Steam API learns which app it is from these (the game has steam_appid.txt next to its exe)
    SetEnvironmentVariableW(L"SteamAppId", kAppId);
    SetEnvironmentVariableW(L"SteamGameId", kAppId);
    HMODULE h = LoadLibraryExW(dll.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    auto cleanEnv = [] { SetEnvironmentVariableW(L"SteamAppId", nullptr); SetEnvironmentVariableW(L"SteamGameId", nullptr); };
    if (!h) { DWORD le = GetLastError(); cleanEnv(); error = "couldn't load steam_api64.dll (Windows error " + std::to_string(le) + ") - an antivirus may be blocking it; press Check files, or add the game folder to its exclusions"; return false; }

    auto init = (InitFn)GetProcAddress(h, "SteamAPI_Init");
    auto shutdown = (ShutdownFn)GetProcAddress(h, "SteamAPI_Shutdown");
    auto runCallbacks = (RunCallbacksFn)GetProcAddress(h, "SteamAPI_RunCallbacks");
    auto getId = (GetSteamIdFn)GetProcAddress(h, "SteamAPI_ISteamUser_GetSteamID");
    auto getTicket = (TicketFn)GetProcAddress(h, "SteamAPI_ISteamUser_GetAuthSessionTicket");
    UserFn user = nullptr;
    for (const char* name : { "SteamAPI_SteamUser_v023", "SteamAPI_SteamUser_v022", "SteamAPI_SteamUser_v021", "SteamAPI_SteamUser_v020" })
        if ((user = (UserFn)GetProcAddress(h, name)) != nullptr) break;
    if (!init || !shutdown || !getId || !getTicket || !user)
    {
        FreeLibrary(h); cleanEnv();
        error = "the game's steam_api64.dll is a version the launcher doesn't know";
        return false;
    }

    bool ok = false;
    if (!init()) error = "Steam isn't running, or this Steam account doesn't have Rec Room in its library. Start Steam, sign in, and add Rec Room (it's free) at store.steampowered.com/app/471710";
    else
    {
        if (void* u = user())
        {
            steamId = getId(u);
            unsigned char buf[4096];
            uint32_t len = 0;
            if (getTicket(u, buf, (int)sizeof buf, &len, nullptr) != 0 && len > 0 && len <= sizeof buf)
            {
                static const char* digits = "0123456789abcdef";
                hex.reserve(len * 2);
                for (uint32_t i = 0; i < len; i++) { hex += digits[buf[i] >> 4]; hex += digits[buf[i] & 15]; }
                ok = true;
            }
            else error = "Steam didn't give a ticket";
            if (runCallbacks) for (int i = 0; i < 5; i++) { runCallbacks(); Sleep(50); }
        }
        else error = "Steam didn't answer";
        shutdown();
    }
    // steam_api64 stays loaded: unloading it after SteamAPI_Init can leave Steam's own threads
    // running code that's gone. Init/Shutdown can run again on the next link.
    cleanEnv();
    return ok;
}
