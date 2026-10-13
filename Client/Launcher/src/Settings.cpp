#include "Settings.h"
#include "Util.h"
#include "json.hpp"
#include <windows.h>
#include <shlobj.h>
#include <ctime>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;
using json = nlohmann::json;

static std::string g_dataOverride;
void SetAppDataDir(const std::string& dir) { g_dataOverride = dir; }

std::string AppDataDir()
{
    PWSTR p = nullptr;
    fs::path dir;
    if (!g_dataOverride.empty())
    {
        std::error_code ec;
        fs::create_directories(U8Path(g_dataOverride), ec);
        return g_dataOverride;
    }
#ifdef CAMPUS_DEBUG
    // the debug build never shares the real launcher's settings / session / log
    static const wchar_t* kDir = L"CampusLauncher-Debug";
#else
    static const wchar_t* kDir = L"CampusLauncher";
#endif
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &p))) { dir = fs::path(p) / kDir; CoTaskMemFree(p); }
    else dir = fs::temp_directory_path() / kDir;
    std::error_code ec;
    fs::create_directories(dir, ec);
    return PathU8(dir);
}

static json ReadJson(const fs::path& f)
{
    std::ifstream in(f, std::ios::binary);
    if (!in) return json();
    std::string s((std::istreambuf_iterator<char>(in)), {});
    return json::parse(s, nullptr, false);
}

static void WriteJson(const fs::path& f, const json& j)
{
    fs::path tmp = f; tmp += L".tmp";
    { std::ofstream o(tmp, std::ios::binary | std::ios::trunc); o << j.dump(1); }
    std::error_code ec;
    fs::rename(tmp, f, ec);
}

static fs::path SettingsFile() { return U8Path(AppDataDir()) / L"settings.json"; }
static fs::path SessionFile() { return U8Path(AppDataDir()) / L"session.json"; }

void Settings::Load()
{
    json j = ReadJson(SettingsFile());
    if (!j.is_object()) return;
    gameDir = j.value("gameDir", gameDir);
    mode = j.value("mode", mode);
    windowMode = j.value("windowMode", windowMode);
    resolution = j.value("resolution", resolution);
    closeOnPlay = j.value("closeOnPlay", closeOnPlay);
    gameSha = j.value("gameSha", gameSha);
    lastSeenBuild = j.value("lastSeenBuild", lastSeenBuild);
    channel = j.value("channel", channel);
    if (channel != "beta" && channel != "dev" && channel != "2026") channel = "public";
    gameDir2026 = j.value("gameDir2026", gameDir2026);
    devServer = j.value("devServer", devServer);
    devServerDir = j.value("devServerDir", devServerDir);
    devNetDir = j.value("devNetDir", devNetDir);
    if (devServer.empty()) devServer = "localhost:44334";
    uiGradient.clear();
    if (j.contains("uiGradient") && j["uiGradient"].is_array() && j["uiGradient"].size() == 12)
        for (auto& v : j["uiGradient"]) uiGradient.push_back(v.is_number() ? v.get<float>() : 0.0f);
}

void Settings::Save() const
{
    WriteJson(SettingsFile(), json{ {"gameDir", gameDir}, {"mode", mode}, {"windowMode", windowMode},
                                    {"resolution", resolution}, {"closeOnPlay", closeOnPlay}, {"uiGradient", uiGradient}, {"gameSha", gameSha},
                                    {"lastSeenBuild", lastSeenBuild}, {"channel", channel}, {"devServer", devServer},
                                    {"devServerDir", devServerDir}, {"devNetDir", devNetDir}, {"gameDir2026", gameDir2026} });
}

bool Session::Valid() const { return !token.empty() && expires > (int64_t)std::time(nullptr) + 60; }

bool Session::Load()
{
    json j = ReadJson(SessionFile());
    if (!j.is_object()) return false;
    token = j.value("token", ""); username = j.value("username", "");
    id = j.value("id", (uint64_t)0); expires = j.value("expires", (int64_t)0);
    return Valid();
}

void Session::Save() const
{
    WriteJson(SessionFile(), json{ {"token", token}, {"username", username}, {"id", id}, {"expires", expires} });
}

void Session::Clear()
{
    token.clear(); username.clear(); id = 0; expires = 0;
    std::error_code ec;
    fs::remove(SessionFile(), ec);
}
