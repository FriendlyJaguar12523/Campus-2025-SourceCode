#include "App.h"
#include "HostsFix.h"
#include "LauncherKey.h"
#include "Steam.h"
#include "Theme.h"
#include "Update.h"
#include "Util.h"
#include "Version.h"
#include "imgui.h"
#include "imgui_stdlib.h"
#include <windows.h>
#include <bcrypt.h>
#include <shellapi.h>
#include <shlobj.h>
#include <algorithm>
#include <cfloat>
#include <cmath>
#include <ctime>
#include <filesystem>
#include <fstream>

namespace fs = std::filesystem;

static constexpr int IDR_HERO = 201;
static constexpr int IDR_HERO_MASK = 202;

// page colours (the top bar sits on the darkest)
static constexpr ImU32 kBar     = IM_COL32(15, 15, 18, 255);
static constexpr ImU32 kPage    = IM_COL32(21, 21, 25, 255);
static constexpr ImU32 kCard    = IM_COL32(31, 31, 36, 255);
static constexpr ImU32 kCardHov = IM_COL32(39, 39, 45, 255);
static constexpr ImU32 kLine    = IM_COL32(255, 255, 255, 18);
static constexpr ImU32 kWhite   = IM_COL32(255, 255, 255, 255);
static constexpr ImU32 kSoft    = IM_COL32(170, 170, 178, 255);

static void OpenUrl(const std::string& url) { ShellExecuteW(nullptr, L"open", Widen(url).c_str(), nullptr, nullptr, SW_SHOWNORMAL); }

static ImFont* BoldF() { return ui::Bold() ? ui::Bold() : ImGui::GetFont(); }
static ImVec2 TextSize(ImFont* f, float size, const char* s) { return f->CalcTextSizeA(size, FLT_MAX, 0, s); }

static ImU32 Rgb(const float c[3], float alpha = 1.0f)
{
    auto b = [](float v) { return (int)std::clamp(v * 255.0f + 0.5f, 0.0f, 255.0f); };
    return IM_COL32(b(c[0]), b(c[1]), b(c[2]), (int)(alpha * 255));
}

// The gradient the patch draws: 4 evenly spaced stops (UIGradient.h rampAt).
static void RampAt(const float stops[4][3], float t, float out[3])
{
    t = std::clamp(t, 0.0f, 1.0f);
    float f = t * 3.0f;
    int i = std::min(2, (int)f);
    float k = f - i;
    for (int j = 0; j < 3; j++) out[j] = stops[i][j] * (1 - k) + stops[i + 1][j] * k;
}

static void Ramp(ImDrawList* dl, ImVec2 a, ImVec2 b, const float stops[4][3], float r = 0, float alpha = 1.0f)
{
    if (alpha < 1.0f)   // faded: solid, then the card's colour over it (see-through slices would show their seams)
    {
        Ramp(dl, a, b, stops, r);
        dl->AddRectFilled(a, b, (kCard & 0x00FFFFFF) | ((ImU32)((1.0f - alpha) * 255) << 24), std::min(r, (b.x - a.x) / 4));
        return;
    }
    float w = b.x - a.x;
    r = std::min(r, w / 4);
    float c0[3], c1[3];
    if (r > 0)
    {
        dl->AddRectFilled(a, ImVec2(a.x + r + 0.5f, b.y), Rgb(stops[0], alpha), r, ImDrawFlags_RoundCornersLeft);
        dl->AddRectFilled(ImVec2(b.x - r - 0.5f, a.y), b, Rgb(stops[3], alpha), r, ImDrawFlags_RoundCornersRight);
    }
    const int n = 24;
    float x0 = a.x + r, x1 = b.x - r, sw = (x1 - x0) / n;
    for (int i = 0; i < n; i++)
    {
        float l = x0 + sw * i, rr = i == n - 1 ? x1 : l + sw + 0.5f;
        RampAt(stops, (l - a.x) / w, c0);
        RampAt(stops, (rr - a.x) / w, c1);
        dl->AddRectFilledMultiColor(ImVec2(l, a.y), ImVec2(rr, b.y), Rgb(c0, alpha), Rgb(c1, alpha), Rgb(c1, alpha), Rgb(c0, alpha));
    }
}

// Cover-crop a picture into a rectangle (like CSS background-size: cover).
static void Cover(ImDrawList* dl, const Tex& t, ImVec2 a, ImVec2 b, ImU32 tint = IM_COL32_WHITE)
{
    float br = (b.x - a.x) / (b.y - a.y), ir = (float)t.w / t.h;
    ImVec2 uv0(0, 0), uv1(1, 1);
    if (ir > br) { float k = br / ir; uv0.x = (1 - k) / 2; uv1.x = 1 - uv0.x; }
    else { float k = ir / br; uv0.y = (1 - k) / 2; uv1.y = 1 - uv0.y; }
    dl->AddImage((ImTextureID)t.Id(), a, b, uv0, uv1, tint);
}

// A row of choices in one rounded pill; the chosen one is filled white. Returns true when changed.
static bool Pill(const char* id, int* value, const char* const labels[], int count, ImVec2 size, bool enabled = true)
{
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 p = ImGui::GetCursorScreenPos();
    float r = size.y / 2, inset = 3, w = (size.x - inset * 2) / count;
    dl->AddRectFilled(p, ImVec2(p.x + size.x, p.y + size.y), IM_COL32(0, 0, 0, 150), r);
    dl->AddRect(p, ImVec2(p.x + size.x, p.y + size.y), IM_COL32(255, 255, 255, 34), r);
    bool changed = false;
    ImGui::PushID(id);
    for (int i = 0; i < count; i++)
    {
        ImVec2 a(p.x + inset + w * i, p.y + inset), b(a.x + w, p.y + size.y - inset);
        ImGui::SetCursorScreenPos(a);
        ImGui::BeginDisabled(!enabled);
        ImGui::PushID(i);
        bool click = ImGui::InvisibleButton("##o", ImVec2(w, b.y - a.y));
        bool hov = ImGui::IsItemHovered();
        ImGui::PopID();
        ImGui::EndDisabled();
        bool on = *value == i;
        if (on) dl->AddRectFilled(a, b, enabled ? IM_COL32(245, 245, 247, 255) : IM_COL32(150, 150, 150, 255), (b.y - a.y) / 2);
        else if (hov && enabled) dl->AddRectFilled(a, b, IM_COL32(255, 255, 255, 22), (b.y - a.y) / 2);
        ImFont* f = BoldF();
        float fs = ImGui::GetFontSize() * 0.95f;
        ImVec2 ts = TextSize(f, fs, labels[i]);
        dl->AddText(f, fs, ImVec2((a.x + b.x - ts.x) / 2, (a.y + b.y - ts.y) / 2), on ? IM_COL32(20, 20, 24, 255) : enabled ? IM_COL32(225, 225, 230, 255) : IM_COL32(120, 120, 120, 255), labels[i]);
        if (click && enabled && !on) { *value = i; changed = true; }
    }
    ImGui::PopID();
    ImGui::SetCursorScreenPos(p);
    ImGui::Dummy(size);
    return changed;
}

// On/off switch.
static bool Switch(const char* id, bool* v)
{
    float h = ImGui::GetFrameHeight() * 0.95f, w = h * 1.8f;
    ImVec2 p = ImGui::GetCursorScreenPos();
    bool click = ImGui::InvisibleButton(id, ImVec2(w, h));
    if (click) *v = !*v;
    bool hov = ImGui::IsItemHovered();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    if (*v) ui::GradientRect(dl, ImVec2(p.x + h / 2, p.y), ImVec2(p.x + w - h / 2, p.y + h));
    ImU32 bg = *v ? ui::Grad(0.5f) : hov ? IM_COL32(80, 80, 88, 255) : IM_COL32(62, 62, 70, 255);
    if (*v)
    {
        dl->AddCircleFilled(ImVec2(p.x + h / 2, p.y + h / 2), h / 2, ui::Grad(0.0f), 24);
        dl->AddCircleFilled(ImVec2(p.x + w - h / 2, p.y + h / 2), h / 2, ui::Grad(1.0f), 24);
    }
    else dl->AddRectFilled(p, ImVec2(p.x + w, p.y + h), bg, h / 2);
    float k = h / 2 - 3;
    dl->AddCircleFilled(ImVec2(*v ? p.x + w - h / 2 : p.x + h / 2, p.y + h / 2), k, kWhite, 24);
    return click;
}

// Small uppercase heading over a group of things.
static void Heading(const char* text)
{
    ImFont* f = BoldF();
    float fs = ImGui::GetFontSize() * 0.8f;
    ImVec2 p = ImGui::GetCursorScreenPos();
    ImGui::GetWindowDrawList()->AddText(f, fs, p, IM_COL32(135, 135, 145, 255), text);
    ImGui::Dummy(ImVec2(0, fs + 8));
}

// Plain text that acts as a link.
static bool Link(const char* label)
{
    ImVec2 p = ImGui::GetCursorScreenPos();
    const char* end = strstr(label, "##");
    std::string text(label, end ? end : label + strlen(label));
    ImVec2 ts = ImGui::CalcTextSize(text.c_str());
    bool click = ImGui::InvisibleButton(label, ts);
    bool hov = ImGui::IsItemHovered();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    dl->AddText(p, hov ? kWhite : ui::Grad(0.55f, 1.35f), text.c_str());
    if (hov) { dl->AddLine(ImVec2(p.x, p.y + ts.y), ImVec2(p.x + ts.x, p.y + ts.y), kWhite); ImGui::SetMouseCursor(ImGuiMouseCursor_Hand); }
    return click;
}

static bool Btn(const char* label, ImVec2 size = ImVec2(0, 0))
{
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 6);
    ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 0);
    ImGui::PushStyleColor(ImGuiCol_Button, IM_COL32(48, 48, 55, 255));
    ImGui::PushStyleColor(ImGuiCol_ButtonHovered, IM_COL32(60, 60, 68, 255));
    ImGui::PushStyleColor(ImGuiCol_ButtonActive, IM_COL32(72, 72, 80, 255));
    bool r = ImGui::Button(label, size);
    ImGui::PopStyleColor(3);
    ImGui::PopStyleVar(2);
    return r;
}

static std::string Gb(uint64_t b)
{
    char t[32];
    if (b < 1000000000ull) snprintf(t, sizeof t, "%.0f MB", b / 1e6);
    else snprintf(t, sizeof t, "%.2f GB", b / 1e9);
    return t;
}

bool UiTheme::operator==(const UiTheme& o) const
{
    if (enabled != o.enabled || preset != o.preset) return false;
    if (preset != "custom") return true;            // a preset is just its id
    return memcmp(stops, o.stops, sizeof stops) == 0 && memcmp(accent, o.accent, sizeof accent) == 0;
}

static bool ReadRgb(const nlohmann::json& a, float out[3])
{
    if (!a.is_array() || a.size() < 3) return false;
    for (int i = 0; i < 3; i++) out[i] = a[i].is_number() ? a[i].get<float>() : 0.0f;
    return true;
}

// ---------------------------------------------------------------- start / stop

void App::Init(void* hwnd, const std::string& serverOverride)
{
    hwnd_ = hwnd;
    S.Load();
    if (!serverOverride.empty()) S.server = serverOverride;
    while (!S.server.empty() && S.server.back() == '/') S.server.pop_back();
    launcherDir_ = PathU8(U8Path(ExePath()).parent_path());
    session_.Load();

    size_t n = 0;
    if (const void* p = ResourceData(IDR_HERO, n))
        if (DecodeImage(p, n, heroPx_, heroW_, heroH_)) hero_.LoadPixels(heroPx_.data(), heroW_, heroH_);
    if (const void* p = ResourceData(IDR_HERO_MASK, n))
    {
        int mw = 0, mh = 0;
        if (!DecodeImage(p, n, heroMask_, mw, mh) || mw != heroW_ || mh != heroH_) heroMask_.clear();
    }

    // the exe the last update replaced is still running for a moment after it started this one
    if (!RemoveOldExe()) oldExeAt_ = GetTickCount64() + 5000;
    std::error_code ec;
    fs::remove_all(U8Path(AppDataDir()) / L"update", ec);

    // until the server answers, the Campus colours
    static const float campus[4][3] = { { .475f, .106f, .553f }, { .749f, .161f, .400f }, { .976f, .365f, .141f }, { .980f, .600f, .071f } };
    memcpy(custom_.stops, campus, sizeof campus);
    custom_.accent[0] = 1; custom_.accent[1] = .6f; custom_.accent[2] = .1f;
    custom_.preset = "custom";
    memcpy(theme_.stops, campus, sizeof campus);
    memcpy(theme_.accent, custom_.accent, sizeof theme_.accent);

    // Did we just self-update? lastSeenBuild is this exe's LV_VERSION_BUILD from the previous
    // run; a lower value means an auto-update happened between then and now. Remember the old
    // version for the "Updated to X" chip, then write this build so the chip only shows once.
    if (S.lastSeenBuild > 0 && S.lastSeenBuild < LV_VERSION_BUILD)
    {
        int oldMajor = S.lastSeenBuild / 100, oldMinor = S.lastSeenBuild % 100;
        char buf[32]; snprintf(buf, sizeof buf, "%d.%02d", oldMajor, oldMinor);
        justUpdatedFromVer_ = buf;
        justUpdatedAt_ = GetTickCount64();
        Log("Launcher auto-updated: " + justUpdatedFromVer_ + " -> " LV_VERSION_STR);
    }
    S.lastSeenBuild = LV_VERSION_BUILD;
    S.Save();

    Log("Campus launcher " LV_VERSION_STR LV_BUILD_TAG " - server " + S.server);
    if (IsGameDir(S.gameDir)) Log("Game folder: " + S.gameDir);
    else if (!noFind_) FindGame();
    if (IsGameDir2026(S.gameDir2026)) Log("Rec Room 2026 folder: " + S.gameDir2026);
    else if (!noFind_) Find2026();
    // Does the OS resolver see the Campus hostname? If not, the game would fail at BootSequence
    // with "error code: summer" (NameResolutionFailure) and the launcher offers a hosts-file
    // fix. Runs on its own thread so a slow / hung ISP resolver can't stall startup.
    CheckDns();
    CheckForUpdate();
    std::string url = Url("/api/launcher/v1/theme/presets");
    presetsJob_ = RunNet([url](NetJob&) { return HttpGet(url, ""); });
    if (session_.Valid()) { RefreshMe(); LoadTheme(); FetchGameInfo(); }
}

void App::Shutdown()
{
    // closed straight after a colour change (before it saved by itself): save it now
    auto finish = [&] {
        for (int i = 0; i < 80 && saveJob_ && !saveJob_->done; i++) Sleep(50);
        if (saveJob_ && saveJob_->done && saveJob_->result.Ok()) themeSaved_ = saving_;
        saveJob_.reset();
    };
    if (themeLoaded_ && session_.Valid())
    {
        finish();                                                    // one already on its way
        if (!(theme_ == themeSaved_)) { SaveTheme(); finish(); }
    }
    hero_.Release(); avatar_.Release();
    S.Save();
}

void App::Log(const std::string& s)
{
    std::time_t t = std::time(nullptr);
    std::tm tm{};
    localtime_s(&tm, &t);
    char ts[16];
    std::strftime(ts, sizeof ts, "%H:%M:%S", &tm);
    log_.push_back(std::string(ts) + "  " + s);
#ifdef CAMPUS_DEBUG
    OutputDebugStringA((std::string("[launcher] ") + s + "\n").c_str());      // shows in Visual Studio's Output window
#endif
    if (log_.size() > 500) log_.erase(log_.begin());
    std::ofstream f(U8Path(AppDataDir()) / L"launcher.log", std::ios::app);
    f << ts << "  " << s << "\n";
}

void App::FindGame()
{
    findState_ = std::make_shared<std::atomic<int>>(0);
    findResult_ = std::make_shared<std::string>();
    auto state = findState_; auto result = findResult_; std::string dir = launcherDir_;
    std::thread([state, result, dir] { *result = FindGameDir(dir); *state = 1; }).detach();
    status_ = "Looking for the game..."; statusBad_ = false;
}

void App::CheckFiles()
{
    { std::string note; EnsureSteamApi(S.gameDir, note); if (!note.empty()) Log(note); }
    missing_ = MissingFiles(S.gameDir);
    if (missing_.empty()) { status_ = "All the game's files are there."; statusBad_ = false; }
    else
    {
        status_ = std::to_string(missing_.size()) + " game file(s) missing: ";
        for (size_t i = 0; i < missing_.size(); i++) status_ += (i ? ", " : "") + missing_[i];
        statusBad_ = true;
    }
    Log(status_);
}

void App::ChooseFolder()
{
    std::string picked = PickFolder(hwnd_, S.gameDir);
    if (picked.empty()) return;
    if (IsGameDir(picked)) { S.gameDir = picked; S.Save(); cleaned_ = false; status_ = "Ready to play."; statusBad_ = false; Log("Game folder: " + picked); }
    else { status_ = "That folder doesn't have Recroom_Release.exe in it."; statusBad_ = true; }
}

// The 2026 client (developer accounts, the "2026" channel): the Steam libraries' steamapps\common\RecRoom, or whatever folder was chosen.
void App::Find2026()
{
    std::string dir = FindGameDir2026();
    if (dir.empty()) { Log("Rec Room 2026 not found in the Steam libraries - choose its folder in Settings > Developer"); return; }
    S.gameDir2026 = dir; S.Save();
    Log("Found Rec Room 2026: " + dir);
}

void App::Choose2026Folder()
{
    std::string picked = PickFolder2026(hwnd_, S.gameDir2026);
    if (picked.empty()) return;
    if (IsGameDir2026(picked)) { S.gameDir2026 = picked; S.Save(); status_ = "Ready to play."; statusBad_ = false; Log("Rec Room 2026 folder: " + picked); }
    else { status_ = "That folder doesn't have RecRoom.exe, GameAssembly.dll, Referee.dll and UnityPlayer.dll in it."; statusBad_ = true; }
}

// ---------------------------------------------------------------- server work

void App::CheckForUpdate()
{
    updPhase_ = 1;
    updNextCheckAt_ = GetTickCount64() + 5 * 60 * 1000;
    std::string url = Url("/api/launcher/v1/app");
    updJob_ = RunNet([url](NetJob&) { return HttpGet(url, ""); });
}

void App::SignIn()
{
    loginError_.clear();
    std::string url = Url("/api/launcher/v1/login");
    std::string body = json{ {"username", loginUser_}, {"password", loginPass_} }.dump();
    loginJob_ = RunNet([url, body](NetJob&) { return HttpPostJson(url, "", body); });
}

void App::SignOut(const std::string& why)
{
    if (!session_.username.empty()) Log("Signed out @" + session_.username + (why.empty() ? "" : " - " + why));
    session_.Clear();
    me_ = json();
    avatar_.Release();
    avatarUrl_.clear();
    loginPass_.clear();
    loginError_ = why;
    themeLoaded_ = false; themeJob_.reset(); saveJob_.reset(); themeMsg_.clear();
    S.uiGradient.clear(); S.Save();                  // the next person starts in the Campus colours
    steamTried_ = false; steamBad_ = false; steamMsg_.clear(); steamJob_.reset();
    page_ = Page::Play;
}

void App::RefreshMe()
{
    if (meJob_ || !session_.Valid()) return;
    std::string url = Url("/api/launcher/v1/me"), token = session_.token;
    meJob_ = RunNet([url, token](NetJob&) { return HttpGet(url, token); });
    meAt_ = GetTickCount64();
}

void App::LoadTheme()
{
    if (themeJob_ || !session_.Valid()) return;
    themeMsg_.clear();
    std::string url = Url("/api/launcher/v1/theme"), token = session_.token;
    themeJob_ = RunNet([url, token](NetJob&) { return HttpGet(url, token); });
}

// The server lets a PC login in only when its Steam id came through the launcher (the normal-player
// whitelist): /me lists the ones this account added.
bool App::SteamWhitelisted() const
{
    uint64_t local = LocalSteamId();
    if (!local || !me_.is_object()) return false;
    for (auto& v : me_.value("launcher_steam", json::array()))
        if (v.is_string() && v.get<std::string>() == std::to_string(local)) return true;
    return false;
}

// HMAC-SHA256(key, data) → lower-case hex, "" on failure. Backed by bcrypt (CNG), the same
// hasher PatchProof uses on the patch side.
static std::string HmacSha256Hex(const std::string& key, const std::string& data)
{
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE h = nullptr;
    unsigned char out[32];
    std::string res;
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG) != 0) return res;
    if (BCryptCreateHash(alg, &h, nullptr, 0, (PUCHAR)key.data(), (ULONG)key.size(), 0) == 0 &&
        BCryptHashData(h, (PUCHAR)data.data(), (ULONG)data.size(), 0) == 0 &&
        BCryptFinishHash(h, out, sizeof out, 0) == 0)
    {
        static const char* hex = "0123456789abcdef";
        res.reserve(64);
        for (unsigned char b : out) { res += hex[b >> 4]; res += hex[b & 15]; }
    }
    if (h) BCryptDestroyHash(h);
    BCryptCloseAlgorithmProvider(alg, 0);
    return res;
}

std::string App::MintLaunchToken() const
{
    if (!session_.Valid() || session_.id == 0) return "";
    uint64_t steam = LocalSteamId();   // 0 if Steam isn't signed in - still allowed, the token just has steam=0
    int64_t ts = (int64_t)std::time(nullptr);
    std::string acc = std::to_string(session_.id);
    std::string sid = std::to_string(steam);
    std::string tss = std::to_string(ts);
    std::string msg = "launch|" + acc + "|" + sid + "|" + tss;
    std::string mac = HmacSha256Hex(CAMPUS_LAUNCHER_LAUNCH_KEY, msg);
    if (mac.empty()) return "";
    return acc + "|" + sid + "|" + tss + "|" + mac;
}

// Fire-and-forget DNS probe for the Campus server host. getaddrinfo blocks on failure so we
// take the hit on a worker thread; the UI reads dnsState_ atomically. Rechecks are safe (the
// probe is cheap) so FixDns also calls this once the hosts write is in.
void App::CheckDns()
{
    dnsState_ = 0;
    std::thread([this]() {
        // every name the game needs (ns. / api. / cdn. as well as camprec.net): one that fails is "error code: summer" in game
        std::string bad;
        for (int i = 0; i < hostsfix::kProbeCount; i++)
            if (!hostsfix::CanResolve(hostsfix::kHosts[i])) bad += (bad.empty() ? "" : ", ") + std::string(hostsfix::kHosts[i]);
        bool ok = bad.empty();
        dnsState_ = ok ? 1 : 2;
        if (!ok) Log("DNS: can't resolve " + bad + " - offering the hosts-file fix.");
        else      Log("DNS: camprec.net, ns., api. and cdn. all resolve OK.");
    }).detach();
}

// Elevate (UAC) + write the Campus hosts entries + flush DNS + re-check. Runs on a worker so
// the main thread can keep drawing; dnsFixing_ is up for the whole thing so the UI shows a
// spinner / disables the button.
void App::FixDns()
{
    if (dbg_.dnsBanner) { dnsFixResult_ = "(debug) the banner is simulated - the hosts file was not touched."; return; }
    if (dnsFixing_.exchange(true)) return;
    dnsFixResult_.clear();
    std::thread([this]() {
        bool ok = hostsfix::RelaunchElevatedAndFix(hostsfix::kServerIp);
        if (ok)
        {
            // Give Windows a moment to pick up the new hosts entries, then re-probe.
            Sleep(400);
            dnsFixResult_ = std::string("Campus servers added to the Windows hosts file. You can sign in now.");
            Log(dnsFixResult_);
            CheckDns();
        }
        else
        {
            dnsFixResult_ = "Couldn't update the Windows hosts file (admin permission not given, or write refused). You can also add these lines to C:\\Windows\\System32\\drivers\\etc\\hosts by hand:   " + [] { std::string r; for (int i = 0; i < hostsfix::kHostsCount; i++) r += std::string(i ? "   /   " : "") + hostsfix::kServerIp + "  " + hostsfix::kHosts[i]; return r; }();
            Log(dnsFixResult_);
            dnsState_ = 2;
        }
        dnsFixing_ = false;
    }).detach();
}

// The account is banned when the server's /me says so. Mirrors the website's site-wide ban
// banner so the launcher stops the game from launching rather than letting it start and
// bounce the player to their dorm.
bool App::AccountBanned() const
{
    if (dbg_.ban) return true;
    return me_.is_object() && me_.value("banned", false);
}

// A readable "This account is banned [permanently | for N h N min]. Reason: ..." line, built
// from the ban object in /me ({ permanent, remaining_seconds, until, reason }). Falls back to
// the plain "banned" line when the server didn't send the detail.
std::string App::BanLine() const
{
    if (!AccountBanned()) return "";
    if (dbg_.ban == 1) return "This account is banned for 2 h 14 min. Reason: Debug test ban";
    if (dbg_.ban == 2) return "This account is banned permanently. Reason: Debug test ban";
    std::string head = "This account is banned";
    std::string tail;
    if (me_.contains("ban") && me_["ban"].is_object())
    {
        const auto& ban = me_["ban"];
        if (ban.value("permanent", false)) head += " permanently";
        else
        {
            int s = ban.value("remaining_seconds", 0);
            if (s > 0)
            {
                char buf[64];
                if (s < 60) snprintf(buf, sizeof buf, " for %d sec", s);
                else if (s < 3600) snprintf(buf, sizeof buf, " for %d min", s / 60);
                else if (s < 86400) snprintf(buf, sizeof buf, " for %d h %d min", s / 3600, (s % 3600) / 60);
                else snprintf(buf, sizeof buf, " for %d d %d h", s / 86400, (s % 86400) / 3600);
                head += buf;
            }
        }
        std::string reason = ban.value("reason", std::string());
        if (!reason.empty()) tail = "  -  " + reason;
    }
    return head + "." + tail;
}

void App::LinkSteam()
{
    if (steamJob_ || !session_.Valid() || !IsGameDir(S.gameDir)) return;
    steamTried_ = true; steamBad_ = false;
    steamMsg_ = "Linking your Steam account...";
    std::string url = Url("/api/launcher/v1/steam"), token = session_.token, dir = S.gameDir;
    steamJob_ = RunNet([url, token, dir](NetJob&) {
        std::string hex, err;
        uint64_t id = 0;
        if (!GetSteamTicket(dir, hex, id, err)) { HttpResult r; r.error = err; return r; }
        return HttpPostJson(url, token, nlohmann::json{ {"ticket", hex} }.dump());
    });
}

void App::SaveTheme()
{
    if (saveJob_ || !session_.Valid()) return;
    saving_ = theme_;
    json body = { {"enabled", theme_.enabled}, {"preset", theme_.preset} };
    if (theme_.preset == "custom")
    {
        json stops = json::array();
        for (auto& s : theme_.stops) stops.push_back({ s[0], s[1], s[2] });
        body["stops"] = stops;
        body["accent"] = { theme_.accent[0], theme_.accent[1], theme_.accent[2] };
    }
    std::string url = Url("/api/launcher/v1/theme"), token = session_.token, b = body.dump();
    saveJob_ = RunNet([url, token, b](NetJob&) { return HttpPostJson(url, token, b); });
}

void App::FetchImage(Tex& tex, std::string& loadedUrl, std::shared_ptr<NetJob>& job, const std::string& path, int width)
{
    if (path.empty()) { if (!loadedUrl.empty()) { tex.Release(); loadedUrl.clear(); } return; }
    std::string url = Url(path + "?width=" + std::to_string(width) + "&cropSquare=1");
    if (url == loadedUrl || job) return;
    loadedUrl = url;
    job = RunNet([url](NetJob&) { return HttpGet(url, ""); });
}

bool App::IsDev() const
{
    return dbg_.devAccount || (session_.Valid() && me_.is_object() && me_.value("dev", false));
}

std::string App::Channel() const
{
    return IsDev() && (S.channel == "beta" || S.channel == "dev" || S.channel == "2026") ? S.channel : std::string("public");
}

bool App::Busy() const
{
    return (playPhase_ == 1 || playPhase_ == 2 || playPhase_ == 4) || (launch_ && !launch_->done) || install_ != nullptr || playAfterSteam_;
}

std::string App::DisplayName() const
{
    std::string n = me_.is_object() ? me_.value("display_name", std::string()) : std::string();
    return n.empty() ? session_.username : n;
}

void App::FetchGameInfo()
{
    if (gameInfoJob_ || !session_.Valid()) return;
    std::string url = Url("/api/launcher/v1/game"), token = session_.token;
    gameInfoJob_ = RunNet([url, token](NetJob&) { return HttpGet(url, token); });
}

// INSTALL (choose a folder; the zip's Campus2025\ folder is made in it) or UPDATE / repair (over the
// game folder this launcher uses).
void App::InstallGame(bool update)
{
    if (!session_.Valid() || gameSha_.empty() || install_) return;
    if (GameRunning()) { status_ = "Close Campus first."; statusBad_ = true; return; }
    std::string parent;
    if (update && IsGameDir(S.gameDir)) parent = PathU8(U8Path(S.gameDir).parent_path());
    else
    {
        parent = !testInstallDir_.empty() ? testInstallDir_ : PickFolder(hwnd_, S.gameDir.empty() ? std::string() : PathU8(U8Path(S.gameDir).parent_path()));
        if (parent.empty()) return;
        update = false;
    }
    // room for the download plus what it unpacks to (an update overwrites the files already there)
    uint64_t have = 0;
    std::error_code ec;
    fs::path partial = U8Path(parent) / L"Campus.download.zip";
    if (fs::is_regular_file(partial, ec)) have = fs::file_size(partial, ec);
    uint64_t need = (gameSize_ > have ? gameSize_ - have : 0) + (update ? 0 : gameUnpacked_) + (512ull << 20);
    uint64_t free = FreeBytes(parent);
    if (free && free < need)
    {
        status_ = "Not enough space there: Campus needs about " + Gb(need) + " free (" + Gb(free) + " free now).";
        statusBad_ = true;
        return;
    }
    InstallOptions o;
    o.url = Url("/api/launcher/v1/game/" + gameSha_);
    o.token = session_.token;
    o.sha = gameSha_;
    o.size = gameSize_;
    o.parentDir = parent;
    install_ = std::make_shared<InstallJob>();
    installUpdate_ = update;
    speedAt_ = 0; speed_ = 0; speedGot_ = 0;
    StartInstall(o, install_);
    status_ = update ? "Updating Campus..." : "Installing Campus...";
    statusBad_ = false;
    page_ = Page::Play;
    Log(std::string(update ? "Updating" : "Installing") + " the game in " + parent + (have ? " (resuming at " + Gb(have) + ")" : ""));
}

void App::Play()
{
    if (!session_.Valid()) { status_ = "Sign in to play."; statusBad_ = true; return; }
    // Hard block: a banned account can't launch Campus from here. The website does the same.
    // The user can still sign out and sign in to a different account.
    if (AccountBanned()) { status_ = BanLine(); statusBad_ = true; return; }
    // A developer's saved beta / dev / 2026 choice only counts once the server has said this IS a developer account (/me): wait for it
    // rather than quietly playing the public patch.
    if ((S.channel == "beta" || S.channel == "dev" || S.channel == "2026") && !me_.is_object() && !dbg_.devAccount)
    {
        RefreshMe();
        playAfterSteam_ = false;
        status_ = "Checking your account - press PLAY again in a moment."; statusBad_ = true;
        return;
    }
    const bool g26 = Is2026();
    if (g26)
    {
        if (!IsGameDir2026(S.gameDir2026)) { status_ = "Choose the Rec Room 2026 folder (RecRoom.exe) in Settings > Developer."; statusBad_ = true; return; }
        if (GameRunning2026()) { status_ = "Rec Room 2026 is already running."; statusBad_ = true; return; }
    }
    else
    {
        if (!IsGameDir(S.gameDir)) { status_ = "Choose the game folder first."; statusBad_ = true; return; }
        if (GameRunning()) { status_ = "Campus is already running."; statusBad_ = true; return; }
    }
    // The server turns away a game started by an old launcher (no launch token), so be sure this one is
    // current: if the last answer from the server is older than 30 s, ask again first. The check then
    // calls Play() again when it says "up to date", or replaces this exe and restarts it.
    if (updPhase_ != 2 && GetTickCount64() - updCheckedAt_ > 30000)
    {
        if (updPhase_ != 1) CheckForUpdate();
        playAfterUpdateCheck_ = true;
        status_ = "Checking for a newer launcher..."; statusBad_ = false;
        return;
    }
    // PC play needs this Steam account on the normal-player whitelist: prove it to the server first
    if (!steamCheckedForPlay_ && LocalSteamId() && me_.is_object() && !SteamWhitelisted())
    {
        if (!steamJob_) LinkSteam();
        playAfterSteam_ = true;
        status_ = "Checking your Steam account..."; statusBad_ = false;
        return;
    }
    if (g26)
    {
        auto miss = Missing2026Files(S.gameDir2026);
        if (!miss.empty())
        {
            status_ = "Rec Room 2026 files missing: ";
            for (size_t i = 0; i < miss.size(); i++) status_ += (i ? ", " : "") + miss[i];
            statusBad_ = true;
            return;
        }
    }
    else
    {
        { std::string note; EnsureSteamApi(S.gameDir, note); if (!note.empty()) Log(note); }    // the game's own Steam login needs it
        missing_ = MissingFiles(S.gameDir);
        if (!missing_.empty()) { CheckFiles(); return; }
    }
    S.Save();
    if (themeLoaded_ && !(theme_ == themeSaved_)) SaveTheme();     // the game should start with the new colours
    playPhase_ = 1;
    status_ = "Getting the Campus patch..."; statusBad_ = false;
    std::string url = Url("/api/launcher/v1/patch" + ChannelQuery()), token = session_.token;
    if (Channel() != "public") Log("Release channel: " + Channel() + (Channel() == "dev" ? " (the game will use " + S.devServer + ")" :
                                                                        g26 ? " (Rec Room 2026 in " + S.gameDir2026 + ", live Campus servers)" : ""));
    patchJob_ = RunNet([url, token](NetJob&) { return HttpGet(url, token); });
}

LocalServerOptions App::LocalOpts() const
{
    LocalServerOptions o;
    o.serverDir = S.devServerDir; o.netDir = S.devNetDir; o.devServer = S.devServer;
    o.launcherDir = launcherDir_; o.logDir = AppDataDir();
    return o;
}

// start the game with the patch that was just downloaded (the Dev channel: once its local servers are up)
void App::LaunchGame()
{
    LaunchOptions o;
    o.gameDir = GameDirNow(); o.vr = S.mode == 1; o.windowMode = S.windowMode; o.resolution = S.resolution;
    o.patchDll = patchPath_;
    if (Channel() == "dev") { o.nsHost = S.devServer; o.photonHost = "127.0.0.1"; }      // the developer's own server
    // Launcher-launch token for the login gate - "accountId|steamId|ts|hmac" signed
    // with CAMPUS_LAUNCHER_LAUNCH_KEY. Game inherits it via CAMPUS_LAUNCH_TOKEN env var.
    o.launchToken = MintLaunchToken();
    if (o.launchToken.empty()) Log("WARNING: couldn't mint a launcher-launch token - the server may refuse this login");
    launch_ = std::make_shared<LaunchJob>();
    launchLogSeen_ = 0;
    if (Is2026()) StartGame2026(o, launch_); else StartGame(o, launch_);
    playPhase_ = 3;
    Log(std::string(Is2026() ? "Starting Rec Room 2026 with the Campus 2026 patch (" : "Starting Campus (") + (o.vr ? "VR" : "desktop") + ")");
}

void App::Poll()
{
    auto expired = [&](const HttpResult& r) {
        if (r.status != 401) return false;
        SignOut(r.Message());
        return true;
    };

    if (oldExeAt_ && GetTickCount64() > oldExeAt_) oldExeAt_ = RemoveOldExe() ? 0 : GetTickCount64() + 15000;

    // ---- the launcher itself
    if (updJob_ && updJob_->done)
    {
        auto job = updJob_; updJob_.reset();
        const HttpResult& r = job->result;
        if (updPhase_ == 1)
        {
            json j = r.Ok() ? json::parse(r.body, nullptr, false) : json();
            int build = j.is_object() ? j.value("build", 0) : 0;
            std::string sha = j.is_object() ? j.value("sha256", std::string()) : std::string();
            uint64_t size = j.is_object() ? j.value("size", (uint64_t)0) : 0;
            if (!r.Ok()) Log("Update check failed: " + r.Message());
            updCheckedAt_ = GetTickCount64();
            if (r.Ok()) online_ = true;
            updNextCheckAt_ = updCheckedAt_ + (r.Ok() ? 5 * 60 * 1000 : 3000);   // offline: try again in 3 s
#ifdef CAMPUS_DEBUG
            // a debug build never replaces itself with the released exe - it only says what the server has
            const bool canSelfUpdate = false;
            if (build > LV_VERSION_BUILD) Log("Debug build: the server has launcher " + j.value("version", std::string("?")) + " - not self-updating");
#else
            const bool canSelfUpdate = true;
#endif
            if (canSelfUpdate && build > LV_VERSION_BUILD && sha.size() == 64)
            {
                playAfterUpdateCheck_ = false;      // the update replaces this exe and restarts it
                updError_.clear();
                updVersion_ = j.value("version", std::to_string(build / 100) + "." + std::to_string(build % 100));
                std::string url = Url("/api/launcher/v1/app/" + sha);
                fs::path dir = U8Path(AppDataDir()) / L"update";
                std::error_code ec; fs::create_directories(dir, ec);
                std::string path = PathU8(dir / Widen(sha + ".exe"));
                updJob_ = RunNet([url, path, sha, size](NetJob& nj) {
                    nj.total = size;
                    HttpResult res = HttpDownload(url, "", path, &nj.sent, &nj.total);
                    if (res.Ok())
                    {
                        if (Sha256File(path) != sha) res.error = "the download was damaged (it doesn't match the server's checksum)";
                        else res.body = path;
                    }
                    return res;
                });
                updPhase_ = 2;
                Log("Launcher " + updVersion_ + " is out (this is " LV_VERSION_STR ") - updating");
            }
            else
            {
                updLatest_ = r.Ok() && build <= LV_VERSION_BUILD; updPhase_ = 3;
                if (playAfterUpdateCheck_) { playAfterUpdateCheck_ = false; Play(); }   // up to date: carry on with PLAY
            }
        }
        else
        {
            std::string err;
            if (!r.Ok()) err = r.Message();
            else if ((ReleaseSingleInstance(), ApplyUpdate(r.body, err)))
            {
                Log("Updated to " + updVersion_ + " - restarting");
                if (err.empty()) { quit_ = true; return; }
            }
            if (!err.empty()) { updError_ = err; Log("Update failed: " + err); status_ = "Couldn't update to " + updVersion_ + "."; statusBad_ = true; }
            updPhase_ = 3;
        }
    }
    // A launcher left open for hours still has to hear about a new release: ask again every 5 minutes.
    // Not while the game runs (an update restarts the launcher) and not in the middle of a download.
    if (!updJob_ && updPhase_ == 3 && !GameRunning() && !GameRunning2026() && GetTickCount64() > updNextCheckAt_) CheckForUpdate();

    // ---- finding the game
    if (findState_ && *findState_ == 1)
    {
        std::string dir = *findResult_;
        findState_.reset(); findResult_.reset();
        if (!dir.empty())
        {
            S.gameDir = dir; S.Save();
            Log("Found the game: " + dir);
            status_ = "Ready to play."; statusBad_ = false;
        }
        else { status_ = "Couldn't find the game - choose its folder."; statusBad_ = true; Log("Game not found"); }
    }
    if (!cleaned_ && IsGameDir(S.gameDir))
    {
        cleaned_ = true;
        if (!GameRunning()) CleanPatchCopies(S.gameDir);   // a copy left by a crash, or the old shipped CampusPatch.dll
    }
    if (!cleaned2026_ && IsGameDir2026(S.gameDir2026))
    {
        cleaned2026_ = true;
        if (!GameRunning2026()) CleanPatchCopies(S.gameDir2026);   // a 2026 patch copy left by a crash
    }

    // ---- account
    if (loginJob_ && loginJob_->done)
    {
        auto job = loginJob_; loginJob_.reset();
        const HttpResult& r = job->result;
        json j = json::parse(r.body, nullptr, false);
        if (r.Ok() && j.is_object() && j.value("ok", false))
        {
            session_.token = j.value("token", "");
            session_.username = j.value("username", loginUser_);
            session_.id = j.value("id", (uint64_t)0);
            session_.expires = j.value("expires", (int64_t)0);
            session_.Save();
            loginPass_.clear();
            Log("Signed in as @" + session_.username);
            RefreshMe();
            LoadTheme();
            FetchGameInfo();
        }
        else loginError_ = r.Message();
    }
    if (meJob_ && meJob_->done)
    {
        auto job = meJob_; meJob_.reset();
        const HttpResult& r = job->result;
        if (!expired(r))
        {
            json j = json::parse(r.body, nullptr, false);
            if (r.Ok() && j.is_object()) me_ = j;
            else if (!r.Ok()) Log("Couldn't load your account: " + r.Message());
        }
    }
    if (session_.Valid() && GetTickCount64() - meAt_ > 60000 && GetForegroundWindow() == (HWND)hwnd_) RefreshMe();
    if (me_.is_object()) FetchImage(avatar_, avatarUrl_, avatarJob_, me_.value("image", std::string()), 128);
    if (avatarJob_ && avatarJob_->done)
    {
        if (avatarJob_->result.Ok()) avatar_.Load(avatarJob_->result.body.data(), avatarJob_->result.body.size());
        avatarJob_.reset();
    }

    // ---- Steam: link this PC's Steam account to the Campus account (once per sign-in)
    if (!steamTried_ && !steamJob_ && session_.Valid() && me_.is_object() && !Busy() && IsGameDir(S.gameDir))
    {
        steamTried_ = true;
        uint64_t local = LocalSteamId();
        bool linked = false;
        for (auto& v : me_.value("steam_ids", json::array()))
            if (v.is_string() && v.get<std::string>() == std::to_string(local)) linked = true;
        // not linked to the account, or not yet on the normal-player whitelist: prove it now
        if (local && (!linked || !SteamWhitelisted()) && !GameRunning()) LinkSteam();     // the game holds Steam's attention while it runs
    }
    if (steamJob_ && steamJob_->done)
    {
        auto job = steamJob_; steamJob_.reset();
        const HttpResult& r = job->result;
        if (!expired(r))
        {
            json j = json::parse(r.body, nullptr, false);
            // 200, or 409 (Steam proven - so whitelisted - but already tied to another account)
            if ((r.Ok() && j.is_object() && j.value("ok", false)) || r.status == 409)
            {
                if (me_.is_object())
                {
                    if (!me_["launcher_steam"].is_array()) me_["launcher_steam"] = json::array();
                    if (!SteamWhitelisted()) me_["launcher_steam"].push_back(std::to_string(LocalSteamId()));
                }
                if (r.Ok())
                {
                    steamMsg_ = "Linked to this PC's Steam account.";
                    steamBad_ = false;
                    Log("Steam " + j.value("steam_id", std::string()) + " linked to @" + session_.username);
                }
                else { steamMsg_ = "Couldn't link Steam: " + r.Message(); steamBad_ = true; Log(steamMsg_); }
                meJob_.reset(); RefreshMe();
            }
            else
            {
                steamMsg_ = "Couldn't link Steam: " + r.Message();
                steamBad_ = true;
                Log(steamMsg_);
            }
        }
        else playAfterSteam_ = false;
        if (playAfterSteam_)            // carry on with PLAY (if Steam couldn't be proven the server says why at login)
        {
            playAfterSteam_ = false;
            steamCheckedForPlay_ = true;
            Play();
            steamCheckedForPlay_ = false;
        }
    }

    // ---- the game download
    if (gameInfoJob_ && gameInfoJob_->done)
    {
        auto job = gameInfoJob_; gameInfoJob_.reset();
        const HttpResult& r = job->result;
        if (!expired(r))
        {
            json j = json::parse(r.body, nullptr, false);
            if (r.Ok() && j.is_object() && j.value("ok", false))
            {
                gameSha_ = j.value("sha256", std::string());
                gameSize_ = j.value("size", (uint64_t)0);
                gameUnpacked_ = j.value("unpacked", (uint64_t)0);
            }
            else gameSha_.clear();
        }
    }
    if (install_ && install_->done)
    {
        std::string err, dir;
        {
            std::lock_guard<std::mutex> lk(install_->mtx);
            err = install_->error; dir = install_->gameDir;
        }
        if (install_->ok)
        {
            S.gameDir = dir; S.gameSha = gameSha_; S.Save();
            EnsurePatchFolder(dir);
            cleaned_ = false; missing_.clear();
            status_ = installUpdate_ ? "Campus is up to date." : "Campus is installed - ready to play.";
            statusBad_ = false;
            Log(std::string(installUpdate_ ? "Updated" : "Installed") + " the game: " + dir);
        }
        else if (install_->cancel)
        {
            status_ = "Paused - press " + std::string(installUpdate_ ? "UPDATE" : "INSTALL") + " to carry on where it stopped.";
            statusBad_ = false;
            Log("Game download paused");
        }
        else
        {
            status_ = "Couldn't " + std::string(installUpdate_ ? "update" : "install") + " Campus: " + err;
            statusBad_ = true;
            Log(status_);
        }
        install_.reset();
    }

    // ---- the in-game UI gradient
    if (presetsJob_ && presetsJob_->done)
    {
        auto job = presetsJob_; presetsJob_.reset();
        json j = json::parse(job->result.body, nullptr, false);
        if (job->result.Ok() && j.is_object() && j["presets"].is_array())
        {
            presets_.clear();
            for (auto& p : j["presets"])
            {
                UiPreset ps;
                ps.id = p.value("id", std::string());
                ps.name = p.value("name", ps.id);
                bool ok = !ps.id.empty() && p["stops"].is_array() && p["stops"].size() == 4 && ReadRgb(p["accent"], ps.accent);
                for (int i = 0; ok && i < 4; i++) ok = ReadRgb(p["stops"][i], ps.stops[i]);
                if (ok) presets_.push_back(ps);
            }
        }
        else Log("Couldn't load the colour presets: " + job->result.Message());
    }
    if (themeJob_ && themeJob_->done)
    {
        auto job = themeJob_; themeJob_.reset();
        const HttpResult& r = job->result;
        if (!expired(r))
        {
            json j = json::parse(r.body, nullptr, false);
            UiTheme t;
            bool ok = r.Ok() && j.is_object() && j.value("ok", false) && j["stops"].is_array() && j["stops"].size() == 4;
            if (ok)
            {
                t.enabled = j.value("enabled", true);
                t.preset = j.value("preset", std::string("campus"));
                for (int i = 0; i < 4; i++) ReadRgb(j["stops"][i], t.stops[i]);
                if (!ReadRgb(j["accent"], t.accent)) memcpy(t.accent, t.stops[3], sizeof t.accent);
                theme_ = themeSaved_ = t;
                if (t.preset == "custom") custom_ = t;
                themeLoaded_ = true;
            }
            else { themeMsg_ = "Couldn't load your colours: " + r.Message(); themeMsgBad_ = true; }
        }
    }
    if (saveJob_ && saveJob_->done)
    {
        auto job = saveJob_; saveJob_.reset();
        const HttpResult& r = job->result;
        if (!expired(r))
        {
            if (r.Ok())
            {
                themeSaved_ = saving_;
                themeMsg_ = "Saved to your account - the game picks it up within 30 seconds.";
                themeMsgBad_ = false;
                Log(std::string("UI colours saved: ") + (saving_.enabled ? saving_.preset : "off"));
            }
            else { themeMsg_ = "Couldn't save: " + r.Message(); themeMsgBad_ = true; themeChangedAt_ = ImGui::GetTime() + 10; }   // try again in a bit
        }
    }
    // saves by itself a moment after the last change (not while a colour is being dragged)
    if (themeLoaded_ && !saveJob_ && !(theme_ == themeSaved_) && ImGui::GetTime() - themeChangedAt_ > 0.8 && !ImGui::IsMouseDown(0))
        SaveTheme();

    // ---- play
    if (patchJob_ && patchJob_->done)
    {
        auto job = patchJob_; patchJob_.reset();
        const HttpResult& r = job->result;
        if (expired(r)) { playPhase_ = 0; status_ = "Sign in again to play."; statusBad_ = true; }
        else if (playPhase_ == 1)
        {
            json j = json::parse(r.body, nullptr, false);
            std::string sha = r.Ok() && j.is_object() ? j.value("sha256", std::string()) : std::string();
            if (sha.size() != 64) { playPhase_ = 0; status_ = r.Message(); statusBad_ = true; Log("Patch: " + r.Message()); }
            else
            {
                patchSha_ = sha;
                patchPath_ = PatchCopyPath(GameDirNow(), !Is2026());
                std::string url = Url("/api/launcher/v1/patch/" + sha + ChannelQuery()), token = session_.token, path = patchPath_;
                uint64_t size = j.value("size", (uint64_t)0);
                patchJob_ = RunNet([url, token, path, sha, size](NetJob& nj) {
                    nj.total = size;
                    HttpResult res = HttpDownload(url, token, path, &nj.sent, &nj.total);
                    if (res.Ok() && Sha256File(path) != sha) res.error = "the patch download was damaged - try again";
                    if (!res.Ok()) DeleteFileW(U8Path(path).c_str());
                    return res;
                });
                playPhase_ = 2;
                status_ = "Downloading the Campus patch...";
            }
        }
        else
        {
            std::error_code ec;
            bool there = fs::is_regular_file(U8Path(patchPath_), ec);
            if (!r.Ok() || !there)
            {
                playPhase_ = 0;
                status_ = !there ? "Your antivirus removed the Campus patch. Add " + PatchFolder(GameDirNow()) +
                                   " to its exclusions (Settings > Antivirus), then press PLAY again."
                                 : "Couldn't download the patch: " + r.Message() + ". If your antivirus blocked it, add the PATCH folder to its exclusions (Settings > Antivirus).";
                statusBad_ = true;
                Log(status_);
            }
            else if (Channel() == "dev")
            {
                // the Dev channel plays against the developer's OWN server: start it (and CampusNet) first if they are not already up
                playPhase_ = 4;
                status_ = "Starting your local server..."; statusBad_ = false;
                LocalServerOptions lo = LocalOpts();
                localJob_ = RunNet([lo](NetJob&) {
                    HttpResult res;
                    std::string err = StartLocalServers(lo);
                    if (err.empty()) res.status = 200; else res.error = err;
                    return res;
                });
            }
            else LaunchGame();
        }
    }
    if (playPhase_ == 4 && localJob_)
    {
        std::string st = LocalServersStatus();
        if (!st.empty()) status_ = st;
        if (localJob_->done)
        {
            auto job = localJob_; localJob_.reset();
            if (job->result.Ok()) LaunchGame();
            else
            {
                playPhase_ = 0;
                status_ = job->result.error; statusBad_ = true;
                Log("Local servers: " + status_);
                DeleteFileW(U8Path(patchPath_).c_str());       // the patch copy was downloaded for a launch that isn't happening
            }
        }
    }
    if (launch_)
    {
        std::vector<std::string> lines;
        {
            std::lock_guard<std::mutex> lk(launch_->mtx);
            for (; launchLogSeen_ < launch_->log.size(); launchLogSeen_++) lines.push_back(launch_->log[launchLogSeen_]);
        }
        for (auto& l : lines) Log(l);
        std::string st = launch_->Status();
        if (!st.empty()) { status_ = st; statusBad_ = launch_->done && !launch_->ok; }
        if (launch_->done && launch_->ok && S.closeOnPlay) hideWindow_ = true;   // stays alive to delete the patch copy
        if (launch_->exited)
        {
            if (launch_->ok) { status_ = "Ready to play."; statusBad_ = false; }
            launch_.reset();
            playPhase_ = 0;
            // the Dev channel started the developer's own servers for this game: stop them again now it has closed (only the ones it
            // started; a server that was already running is left alone). The launcher waits for that before it exits.
            if (LocalServersStartedByUs() && !stopJob_)
            {
                LocalServerOptions lo = LocalOpts();
                status_ = "Stopping your local servers..."; statusBad_ = false;
                stopJob_ = RunNet([lo](NetJob&) { StopLocalServersWeStarted(lo); HttpResult r; r.status = 200; return r; });
            }
            if (hideWindow_ && !stopJob_) quit_ = true;
        }
    }
    if (stopJob_ && stopJob_->done)
    {
        stopJob_.reset();
        status_ = "Ready to play."; statusBad_ = false;
        Log("Your local servers were stopped.");
        if (hideWindow_) quit_ = true;
    }
}

// ---------------------------------------------------------------- drawing

std::string App::Ellipsize(const std::string& s, float width) const
{
    if (ImGui::CalcTextSize(s.c_str()).x <= width) return s;
    std::string t = s;
    while (t.size() > 4 && ImGui::CalcTextSize(("..." + t).c_str()).x > width) t.erase(0, 1);
    return "..." + t;
}

bool App::IsCaption(float x, float y) const
{
    if (y < 0 || y >= barH_) return false;
    for (auto& r : hotX_) if (x >= r.first && x < r.second) return false;
    return true;
}

void App::ShowPage(const std::string& name)
{
    page_ = name == "customize" ? Page::Customize : name == "settings" ? Page::Settings : name == "debug" ? Page::Debug : Page::Play;
}

// The launcher is drawn in the player's in-game gradient (the Campus one when it's off, they chose
// Campus, or nobody is signed in). Until the server answers, the one this PC saw last.
void App::FollowGradient()
{
    std::vector<float> want;
    if (session_.Valid())
    {
        if (themeLoaded_)
        {
            if (theme_.enabled && theme_.preset != "campus")
                for (auto& st : theme_.stops) want.insert(want.end(), st, st + 3);
            if (want != S.uiGradient) { S.uiGradient = want; S.Save(); }
        }
        else want = S.uiGradient;
    }
    if (want == drawnIn_) return;
    drawnIn_ = want;
    if (want.size() == 12)
    {
        float stops[4][3];
        memcpy(stops, want.data(), sizeof stops);
        ui::SetGradient(stops);
    }
    else ui::ResetGradient();
}

// The art is repainted only when it's on screen (dragging the colour wheel changes the gradient
// every frame, and a repaint is a few milliseconds).
void App::EnsureHero()
{
    if (heroIn_ == drawnIn_ && heroPainted_) return;
    heroIn_ = drawnIn_; heroPainted_ = true;
    if (drawnIn_.size() == 12)
    {
        float stops[4][3];
        memcpy(stops, drawnIn_.data(), sizeof stops);
        PaintHero(stops);
    }
    else PaintHero(nullptr);
}

// The art is drawn in the Campus colours (purple -> magenta -> red -> orange). Repaint its backdrop
// and the logo's letters (HeroMask.png, made offline from a smooth fit of the backdrop - the
// characters' skin is as saturated as the backdrop, so colour alone can't tell them apart) in the
// player's gradient, by where each pixel's hue sits on the Campus one. Light and shadow are kept.
void App::PaintHero(const float (*stops)[3])
{
    if (heroPx_.empty()) return;
    if (!stops || heroMask_.empty()) { hero_.LoadPixels(heroPx_.data(), heroW_, heroH_); return; }
    std::vector<uint8_t> px(heroPx_);
    for (size_t i = 0; i + 3 < px.size(); i += 4)
    {
        float w = heroMask_[i] / 255.0f;
        if (w <= 0) continue;
        float r = px[i] / 255.0f, g = px[i + 1] / 255.0f, b = px[i + 2] / 255.0f;
        float mx = std::max(r, std::max(g, b)), mn = std::min(r, std::min(g, b)), d = mx - mn;
        float h = 0;
        if (d > 0)
        {
            if (mx == r) h = std::fmod((g - b) / d, 6.0f);
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }
        float hu = h < 120 ? h + 360 : h;                  // purple 275 .. orange 40 (= 400), unwrapped
        float c[3];
        RampAt(stops, (hu - 275) / 130, c);
        float shade = std::clamp(mx / 0.9f, 0.0f, 1.15f);
        for (int j = 0; j < 3; j++)
        {
            float o = px[i + j] / 255.0f, n = std::clamp(c[j] * shade, 0.0f, 1.0f);
            px[i + j] = (uint8_t)std::clamp((o + (n - o) * w) * 255.0f + 0.5f, 0.0f, 255.0f);
        }
    }
    hero_.LoadPixels(px.data(), heroW_, heroH_);
}

void App::Frame()
{
    Poll();
    if (quit_) return;
    FollowGradient();
    const float u = ImGui::GetFontSize();
    const ImGuiViewport* vp = ImGui::GetMainViewport();
    ImGui::SetNextWindowPos(vp->WorkPos);
    ImGui::SetNextWindowSize(vp->WorkSize);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 0);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowBorderSize, 0);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0, 0));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0, 0));
    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImGui::ColorConvertU32ToFloat4(kPage));
    ImGui::Begin("Launcher", nullptr, ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove | ImGuiWindowFlags_NoResize |
                 ImGuiWindowFlags_NoBringToFrontOnFocus | ImGuiWindowFlags_NoSavedSettings);
    ImGui::PopStyleColor();
    ImGui::PopStyleVar(4);

    barH_ = std::round(u * 3.6f);
    DrawTopBar(barH_);

    // DNS-fallback banner: when the OS can't resolve camprec.net (usually an ISP blocking
    // / null-routing No-IP), nothing the launcher does afterwards will reach the server. The
    // banner says so and offers a one-click hosts-file fix. Shown on every page, above the
    // ban banner, so it wins while the user still can't even sign in.
    float dnsH = 0;
    if (dnsState_ == 2 || dbg_.dnsBanner)
    {
        dnsH = std::round(u * 2.6f);
        ImDrawList* dl = ImGui::GetWindowDrawList();
        ImVec2 wp = ImGui::GetWindowPos();
        float ww = ImGui::GetWindowWidth();
        ImVec2 a(wp.x, wp.y + barH_), b(wp.x + ww, wp.y + barH_ + dnsH);
        // amber/orange - "attention needed", not "banned"
        dl->AddRectFilledMultiColor(a, b, IM_COL32(146, 76, 10, 255), IM_COL32(180, 102, 20, 255), IM_COL32(180, 102, 20, 255), IM_COL32(146, 76, 10, 255));
        dl->AddLine(ImVec2(a.x, b.y - 1), ImVec2(b.x, b.y - 1), IM_COL32(0, 0, 0, 70));
        std::string head = "CAN'T REACH CAMPUS";
        std::string body = dnsFixing_
            ? "Writing the fix (Windows is asking for admin permission)..."
            : dnsFixResult_.empty()
                ? "Your DNS can't resolve the Campus server. This is usually your ISP blocking the hostname."
                : dnsFixResult_;
        ImFont* hf = BoldF();
        float hs = u * 0.95f;
        ImVec2 hts = hf->CalcTextSizeA(hs, FLT_MAX, 0, head.c_str());
        float left = wp.x + u * 1.0f;
        float cy = a.y + (dnsH - hs) * 0.5f;
        dl->AddText(hf, hs, ImVec2(left, cy), IM_COL32(255, 230, 200, 255), head.c_str());
        float tx = left + hts.x + u * 0.9f;
        dl->AddCircleFilled(ImVec2(tx - u * 0.45f, a.y + dnsH * 0.5f), 2.5f, IM_COL32(255, 220, 170, 180));
        // Fix button on the right
        ImVec2 btnSz(std::round(u * 11), std::round(u * 1.9f));
        ImVec2 btnP(b.x - u * 1.0f - btnSz.x, a.y + (dnsH - btnSz.y) * 0.5f);
        ImGui::PushClipRect(ImVec2(tx, a.y), ImVec2(btnP.x - u * 0.6f, b.y), true);
        dl->AddText(ImVec2(tx, a.y + (dnsH - hs) * 0.5f), IM_COL32(255, 245, 230, 255), body.c_str());
        ImGui::PopClipRect();
        ImGui::SetCursorScreenPos(btnP);
        const char* label = dnsFixing_ ? "Working..." : "Fix connection";
        if (ui::PrimaryButton((std::string(label) + "##dnsfix").c_str(), btnSz, !dnsFixing_)) FixDns();
    }

    // Just-updated banner: when the exe we're running has a higher LV_VERSION_BUILD than the
    // one Settings remembers, we got here via a self-update. Say so loudly for ~30 seconds, or
    // until the player clicks X; after that the "just updated from X.XX" tail stays on the
    // Play sub-text + Settings version line but the banner is gone.
    float updH = 0;
    bool showUpd = !justUpdatedFromVer_.empty();
    if (showUpd && justUpdatedAt_ > 0 && GetTickCount64() - justUpdatedAt_ > 30000) showUpd = false;
    if (showUpd)
    {
        updH = std::round(u * 2.2f);
        ImDrawList* dl = ImGui::GetWindowDrawList();
        ImVec2 wp = ImGui::GetWindowPos();
        float ww = ImGui::GetWindowWidth();
        ImVec2 a(wp.x, wp.y + barH_ + dnsH), b(wp.x + ww, wp.y + barH_ + dnsH + updH);
        // deep green gradient so it reads as "all good" without stealing attention
        dl->AddRectFilledMultiColor(a, b, IM_COL32(26, 110, 52, 255), IM_COL32(38, 140, 72, 255), IM_COL32(38, 140, 72, 255), IM_COL32(26, 110, 52, 255));
        dl->AddLine(ImVec2(a.x, b.y - 1), ImVec2(b.x, b.y - 1), IM_COL32(0, 0, 0, 70));
        std::string head = "LAUNCHER UPDATED";
        std::string body = "Updated " + justUpdatedFromVer_ + " -> " LV_VERSION_STR ". You are on the newest Campus launcher.";
        ImFont* hf = BoldF();
        float hs = u * 0.9f;
        ImVec2 hts = hf->CalcTextSizeA(hs, FLT_MAX, 0, head.c_str());
        float left = wp.x + u * 1.0f;
        float cy = a.y + (updH - hs) * 0.5f;
        dl->AddText(hf, hs, ImVec2(left, cy), IM_COL32(220, 255, 230, 255), head.c_str());
        float tx = left + hts.x + u * 0.9f;
        dl->AddCircleFilled(ImVec2(tx - u * 0.45f, a.y + updH * 0.5f), 2.5f, IM_COL32(220, 255, 220, 180));
        // close X on the right so it doesn't block anyone who doesn't care
        float xSz = u * 1.4f;
        ImVec2 xp(b.x - u * 0.9f - xSz, a.y + (updH - xSz) * 0.5f);
        ImGui::PushClipRect(ImVec2(tx, a.y), ImVec2(xp.x - u * 0.4f, b.y), true);
        dl->AddText(ImVec2(tx, a.y + (updH - hs) * 0.5f), IM_COL32(240, 255, 240, 255), body.c_str());
        ImGui::PopClipRect();
        ImGui::SetCursorScreenPos(xp);
        if (ImGui::InvisibleButton("##dismissUpd", ImVec2(xSz, xSz)))
        {
            // Backdate the show time past the 30s window so the banner closes next frame.
            // The "just updated" tail on the Play sub-text and in Settings stays regardless -
            // justUpdatedFromVer_ itself is what the Ready-to-play sub-text reads.
            unsigned long long now = GetTickCount64();
            justUpdatedAt_ = now > 30001 ? now - 30001 : 1;
        }
        bool xHov = ImGui::IsItemHovered();
        ImU32 xCol = xHov ? IM_COL32(255, 255, 255, 255) : IM_COL32(230, 255, 230, 200);
        float cx = xp.x + xSz * 0.5f, cyX = xp.y + xSz * 0.5f, r = xSz * 0.28f;
        dl->AddLine(ImVec2(cx - r, cyX - r), ImVec2(cx + r, cyX + r), xCol, 2.0f);
        dl->AddLine(ImVec2(cx - r, cyX + r), ImVec2(cx + r, cyX - r), xCol, 2.0f);
    }

    // Site-wide ban banner, mirroring the website: a red strip across the top of every page
    // with "This account is banned [permanently | for N h N min]. Reason: ..." so a banned
    // player can't miss why PLAY is locked. The banner also eats banH_ off the content area.
    float banH = 0;
    if (session_.Valid() && AccountBanned())
    {
        banH = std::round(u * 2.4f);
        ImDrawList* dl = ImGui::GetWindowDrawList();
        ImVec2 wp = ImGui::GetWindowPos();
        float ww = ImGui::GetWindowWidth();
        ImVec2 a(wp.x, wp.y + barH_ + dnsH + updH), b(wp.x + ww, wp.y + barH_ + dnsH + updH + banH);
        // deep red -> warmer red gradient so it reads as "danger" without being pure flat colour
        dl->AddRectFilledMultiColor(a, b, IM_COL32(132, 20, 28, 255), IM_COL32(168, 32, 44, 255), IM_COL32(168, 32, 44, 255), IM_COL32(132, 20, 28, 255));
        dl->AddLine(ImVec2(a.x, b.y - 1), ImVec2(b.x, b.y - 1), IM_COL32(0, 0, 0, 70));
        std::string line = BanLine();
        if (line.empty()) line = "This account is banned.";
        std::string head = "ACCOUNT BANNED";
        ImFont* hf = BoldF();
        float hs = u * 0.95f, ts = u * 0.95f;
        ImVec2 hts = hf->CalcTextSizeA(hs, FLT_MAX, 0, head.c_str());
        float left = wp.x + u * 1.0f;
        float cy = a.y + (banH - hs) * 0.5f;
        dl->AddText(hf, hs, ImVec2(left, cy), IM_COL32(255, 220, 220, 255), head.c_str());
        float tx = left + hts.x + u * 0.9f;
        // separator pip
        dl->AddCircleFilled(ImVec2(tx - u * 0.45f, a.y + banH * 0.5f), 2.5f, IM_COL32(255, 180, 180, 180));
        // body text, clipped so a long reason can't spill under the window buttons
        float rightPad = u * 1.0f;
        ImGui::PushClipRect(ImVec2(tx, a.y), ImVec2(b.x - rightPad, b.y), true);
        float ty = a.y + (banH - ts) * 0.5f;
        dl->AddText(ImVec2(tx, ty), IM_COL32(255, 240, 240, 255), line.c_str());
        ImGui::PopClipRect();
    }

    ImVec2 avail = ImGui::GetContentRegionAvail();
    avail.y -= (banH + dnsH + updH);
    ImGui::SetCursorPos(ImVec2(0, barH_ + dnsH + updH + banH));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImGui::ColorConvertU32ToFloat4(kPage));
    bool signedIn = session_.Valid() && !dbg_.signedOut;
    if ((!online_ || dbg_.offline || dbg_.connecting) && page_ != Page::Debug)
    {
        // No answer from the Campus server yet: nothing else loads (not even Settings) until there is one.
        ImGui::BeginChild("offline", avail, 0, ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse);
        DrawOffline();
        ImGui::EndChild();
    }
    else if (!signedIn && page_ != Page::Settings && page_ != Page::Debug)
    {
        ImGui::BeginChild("signin", avail, 0, ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse);
        DrawSignIn();
        ImGui::EndChild();
    }
    else if (page_ == Page::Play)
    {
        ImGui::BeginChild("play", avail, 0, ImGuiWindowFlags_NoScrollbar | ImGuiWindowFlags_NoScrollWithMouse);
        DrawPlay();
        ImGui::EndChild();
    }
    else
    {
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(u * 2.4f, u * 1.8f));
        ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(8, 8));
        ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 6);
        ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(10, 6));
        ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 0);
        ImGui::PushStyleColor(ImGuiCol_FrameBg, IM_COL32(36, 36, 42, 255));
        ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, IM_COL32(44, 44, 50, 255));
        ImGui::PushStyleColor(ImGuiCol_FrameBgActive, IM_COL32(48, 48, 56, 255));
        ImGui::BeginChild(page_ == Page::Customize ? "customize" : page_ == Page::Debug ? "debug" : "settings", avail, ImGuiChildFlags_AlwaysUseWindowPadding);
        if (page_ == Page::Customize) DrawCustomize(); else if (page_ == Page::Debug) DrawDebug(); else DrawSettings();
        ImGui::EndChild();
        ImGui::PopStyleColor(3);
        ImGui::PopStyleVar(5);
    }
    ImGui::PopStyleColor();
    ImGui::End();

    DrawLog();
    DrawUpdating();
}

// The debug panel's FAKE game install: ~14 s "downloading", 2 s "checking", 6 s "unpacking", then it is "installed".
void App::DebugInstallState(int& phase, uint64_t& got, uint64_t& total)
{
    total = gameSize_ ? gameSize_ : 3800000000ull;
    double t = ImGui::GetTime() - dbg_.installStart;
    if (t < 14)      { phase = 0; got = (uint64_t)(total * (t / 14)); }
    else if (t < 16) { phase = 1; got = total; }
    else if (t < 22) { phase = 2; got = (uint64_t)(total * ((t - 16) / 6)); }
    else { dbg_.installing = false; dbg_.noGame = false; dbg_.gameUpdate = false; phase = 2; got = total; Log("(debug) the fake install finished"); }
}

#ifdef CAMPUS_DEBUG
// --dbg a,b,c on the command line starts with those states already pretended (handy for screenshots / quick looks).
void App::DebugPreset(const std::string& list)
{
    size_t i = 0;
    while (i <= list.size())
    {
        size_t j = list.find(',', i); if (j == std::string::npos) j = list.size();
        std::string k = list.substr(i, j - i);
        if (k == "offline") dbg_.offline = true;
        else if (k == "connecting") dbg_.connecting = true;
        else if (k == "dns") dbg_.dnsBanner = true;
        else if (k == "updating") dbg_.launcherUpdating = true;
        else if (k == "nogame") dbg_.noGame = true;
        else if (k == "gameupdate") dbg_.gameUpdate = true;
        else if (k == "running") dbg_.running = true;
        else if (k == "ban") dbg_.ban = 1;
        else if (k == "permban") dbg_.ban = 2;
        else if (k == "signedout") dbg_.signedOut = true;
        else if (k == "panel") dbg_.panelOpen = true;
        else if (k == "nopanel") dbg_.panelOpen = false;
        else if (k == "installing") { dbg_.noGame = true; dbg_.installing = true; dbg_.installStart = 0; }
        else if (k == "copylog") CopyPlayerLog(false);       // Settings > Game log, pressed for a test run
        else if (k == "copylogprev") CopyPlayerLog(true);
        else if (k == "ensuresteam") { std::string note; bool ok = EnsureSteamApi(S.gameDir, note); Log(std::string("(debug) EnsureSteamApi -> ") + (ok ? "ok" : "FAILED") + (note.empty() ? "" : ": " + note)); }
        i = j + 1;
    }
}

// The DEBUG tab: pretend states so every screen can be looked at without really having them (no internet, a
// launcher or game update, a ban, ...). Nothing here talks to the server or changes a file - installs, the
// DNS fix and the update are all simulated. Switch to another tab to look at the result; the pretend states stay on.
void App::DrawDebug()
{
    const float u = ImGui::GetFontSize();
    auto row = [&](const char* id, const char* label, const char* hint, bool* v)
    {
        ImGui::PushID(id);
        float y = ImGui::GetCursorPosY();
        Switch("##sw", v);
        ImGui::SameLine(0, u * 0.8f);
        ImGui::SetCursorPosY(y);
        ImGui::TextUnformatted(label);
        if (hint && *hint) { ImGui::SameLine(0, u * 0.7f); ImGui::TextDisabled("%s", hint); }
        ImGui::Dummy(ImVec2(0, u * 0.35f));
        ImGui::PopID();
    };
    ImGui::PushFont(BoldF(), u * 1.5f);
    ImGui::TextUnformatted("Debug");
    ImGui::PopFont();
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(kSoft));
    ImGui::TextUnformatted("Pretend a state, then open Play / Customize / Settings to see it. Nothing real changes.");
    ImGui::PopStyleColor();
    ImGui::Dummy(ImVec2(0, u * 1.0f));

    Heading("NETWORK");
    row("off", "No internet", "- the \"Can't connect to Campus\" screen replaces every page", &dbg_.offline);
    row("con", "Connecting", "- the \"Connecting to Campus...\" screen", &dbg_.connecting);
    row("dns", "DNS banner", "- the \"Fix connection\" strip (the fix is simulated)", &dbg_.dnsBanner);

    Heading("LAUNCHER UPDATE");
    row("upd", "Launcher is updating", "- the progress box (loops, nothing is downloaded)", &dbg_.launcherUpdating);
    if (Btn("Show 'just updated' banner")) { justUpdatedFromVer_ = "1.14"; justUpdatedAt_ = GetTickCount64(); }
    ImGui::SameLine();
    if (Btn("Show 'update failed'")) { updError_ = "debug: the download was damaged"; status_ = "Couldn't update to 9.99."; statusBad_ = true; }
    ImGui::Dummy(ImVec2(0, u * 0.9f));

    Heading("GAME");
    bool noGame = dbg_.noGame, gameUpd = dbg_.gameUpdate;
    row("ng", "Game not installed", "- DOWNLOAD button and the faint \"Already have build installed\" link", &noGame);
    row("gu", "Game update available", "- UPDATE button", &gameUpd);
    if (noGame != dbg_.noGame) { dbg_.noGame = noGame; dbg_.installing = false; if (noGame) dbg_.gameUpdate = false; }
    if (gameUpd != dbg_.gameUpdate) { dbg_.gameUpdate = gameUpd; dbg_.installing = false; if (gameUpd) dbg_.noGame = false; }
    ImGui::TextDisabled("On the Play tab, press DOWNLOAD / UPDATE: a fake download, check and unpack runs (about 20 s).");
    ImGui::Dummy(ImVec2(0, u * 0.35f));
    row("run", "Campus is running", "- the RUNNING state", &dbg_.running);
    ImGui::Dummy(ImVec2(0, u * 0.5f));

    Heading("ACCOUNT");
    static const char* bans[] = { "Not banned", "Timed ban", "Permanent ban" };
    ImGui::PushID("ban");
    Pill("banpill", &dbg_.ban, bans, 3, ImVec2(std::round(u * 27), std::round(u * 2.1f)));
    ImGui::PopID();
    ImGui::Dummy(ImVec2(0, u * 0.5f));
    row("so", "Signed out", "- the sign-in screen", &dbg_.signedOut);
    ImGui::Dummy(ImVec2(0, u * 0.5f));

    Heading("STATUS LINE");
    if (Btn("Show an error ('Can't start')")) { status_ = "debug: this is what an error message looks like."; statusBad_ = true; }
    ImGui::SameLine();
    if (Btn("Clear")) { status_ = "Ready to play."; statusBad_ = false; updError_.clear(); justUpdatedFromVer_.clear(); }
    ImGui::Dummy(ImVec2(0, u * 1.0f));
    if (Btn("Reset everything")) { dbg_ = DebugOverrides{}; status_ = "Ready to play."; statusBad_ = false; updError_.clear(); justUpdatedFromVer_.clear(); }
    ImGui::Dummy(ImVec2(0, u * 0.9f));

    Heading("REAL STATE");
    ImGui::TextDisabled("Server: %s    Update check phase: %d    This build: " LV_VERSION_STR LV_BUILD_TAG, online_ ? "answered" : "not answering", updPhase_);
    ImGui::TextDisabled("Settings folder: %s", AppDataDir().c_str());
}
#else
void App::DrawDebug() {}
#endif

// Shown instead of every page until the Campus server has answered once (online_).
void App::DrawOffline()
{
    const float u = ImGui::GetFontSize();
    const ImVec2 sz = ImGui::GetContentRegionAvail();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    const ImVec2 p = ImGui::GetWindowPos();
    const bool checking = dbg_.connecting || (!dbg_.offline && (updPhase_ == 0 || updPhase_ == 1));
    const char* head = checking ? "Connecting to Campus..." : "Can't connect to Campus";
    const char* l1 = checking ? "Checking your internet connection." : "Campus needs an internet connection to load.";
    const char* l2 = checking ? "" : "Check your connection - the launcher keeps trying and will open by itself.";

    ImFont* hf = BoldF();
    const float hs = u * 1.6f, bs = u * 0.95f;
    float y = p.y + sz.y * 0.5f - u * 3.2f;
    ImVec2 ts = TextSize(hf, hs, head);
    dl->AddText(hf, hs, ImVec2(p.x + (sz.x - ts.x) * 0.5f, y), kWhite, head);
    y += hs + u * 0.9f;
    ImVec2 t1 = TextSize(ImGui::GetFont(), bs, l1);
    dl->AddText(ImGui::GetFont(), bs, ImVec2(p.x + (sz.x - t1.x) * 0.5f, y), kSoft, l1);
    if (*l2)
    {
        y += bs + u * 0.4f;
        ImVec2 t2 = TextSize(ImGui::GetFont(), bs, l2);
        dl->AddText(ImGui::GetFont(), bs, ImVec2(p.x + (sz.x - t2.x) * 0.5f, y), kSoft, l2);
    }
    if (!checking)
    {
        ImVec2 bsz(std::round(u * 10), std::round(u * 2.4f));
        ImGui::SetCursorScreenPos(ImVec2(p.x + (sz.x - bsz.x) * 0.5f, y + bs + u * 1.6f));
        if (ui::PrimaryButton("Try again##offline", bsz)) CheckForUpdate();
    }
}

void App::DrawTopBar(float h)
{
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 p = ImGui::GetWindowPos();
    float w = ImGui::GetWindowWidth(), u = ImGui::GetFontSize();
    hotX_.clear();
    dl->AddRectFilled(p, ImVec2(p.x + w, p.y + h), kBar);
    dl->AddLine(ImVec2(p.x, p.y + h - 1), ImVec2(p.x + w, p.y + h - 1), IM_COL32(255, 255, 255, 14));

    float x = p.x + u * 0.6f;

    // pages
    bool signedIn = session_.Valid();
    struct Tab { const char* label; Page page; };
    std::vector<Tab> tabs;
    if (signedIn) { tabs.push_back({ "PLAY", Page::Play }); tabs.push_back({ "CUSTOMIZE", Page::Customize }); tabs.push_back({ "SETTINGS", Page::Settings }); }
#ifdef CAMPUS_DEBUG
    tabs.push_back({ "DEBUG", Page::Debug });      // the Debug build's extra tab: pretend states (see DrawDebug)
#endif
    if (!tabs.empty())
    {
        ImFont* f = BoldF();
        float fs = u * 0.92f;
        for (auto& t : tabs)
        {
            ImVec2 ts = TextSize(f, fs, t.label);
            float tw = ts.x + u * 2.0f;
            ImGui::SetCursorScreenPos(ImVec2(x, p.y));
            ImGui::PushID(t.label);
            bool click = ImGui::InvisibleButton("##tab", ImVec2(tw, h - 1));
            bool hov = ImGui::IsItemHovered();
            ImGui::PopID();
            if (click) page_ = t.page;
            bool on = page_ == t.page;
            ImU32 col = on ? kWhite : hov ? IM_COL32(215, 215, 222, 255) : IM_COL32(140, 140, 150, 255);
            float tx = x + (tw - ts.x) / 2;
            dl->AddText(f, fs, ImVec2(tx, p.y + (h - ts.y) / 2), col, t.label);
            if (on) ui::GradientRect(dl, ImVec2(tx, p.y + h - 3), ImVec2(tx + ts.x, p.y + h));
            hotX_.push_back({ x - p.x, x - p.x + tw });
            x += tw;
        }
    }

    // window buttons (the window has no Windows frame)
    float bw = std::round(u * 3.0f), r = p.x + w;
    for (int i = 0; i < 2; i++)
    {
        bool close = i == 0;
        ImVec2 a(r - bw, p.y), b(r, p.y + h - 1);
        ImGui::SetCursorScreenPos(a);
        ImGui::PushID(i);
        bool click = ImGui::InvisibleButton("##wb", ImVec2(bw, h - 1));
        bool hov = ImGui::IsItemHovered(), held = ImGui::IsItemActive();
        ImGui::PopID();
        if (hov) dl->AddRectFilled(a, b, close ? (held ? IM_COL32(150, 30, 22, 255) : IM_COL32(196, 43, 28, 255)) : IM_COL32(255, 255, 255, held ? 30 : 18));
        ImVec2 c((a.x + b.x) / 2, (a.y + b.y) / 2);
        float s = u * 0.36f;
        ImU32 col = hov ? kWhite : IM_COL32(190, 190, 198, 255);
        if (close) { dl->AddLine(ImVec2(c.x - s, c.y - s), ImVec2(c.x + s, c.y + s), col, 1.2f); dl->AddLine(ImVec2(c.x - s, c.y + s), ImVec2(c.x + s, c.y - s), col, 1.2f); }
        else dl->AddLine(ImVec2(c.x - s, c.y), ImVec2(c.x + s, c.y), col, 1.2f);
        if (click)
        {
            if (close) PostMessageW((HWND)hwnd_, WM_CLOSE, 0, 0);
            else ShowWindow((HWND)hwnd_, SW_MINIMIZE);
        }
        hotX_.push_back({ a.x - p.x, b.x - p.x });
        r -= bw;
    }

    // the account
    if (signedIn)
    {
        std::string name = DisplayName();
        std::string sub = me_.is_object() ? "Level " + std::to_string(me_.value("level", 0)) : "@" + session_.username;
        ImFont* f = BoldF();
        float av = std::round(h * 0.56f), nameW = std::min(u * 11, TextSize(f, u, name.c_str()).x);
        float subW = ImGui::CalcTextSize(sub.c_str()).x;
        float cw = av + u * 0.7f + std::max(nameW, subW) + u * 1.8f;
        ImVec2 a(r - u * 0.8f - cw, p.y + (h - av) / 2 - 4), b(r - u * 0.8f, p.y + (h + av) / 2 + 4);
        ImGui::SetCursorScreenPos(a);
        bool click = ImGui::InvisibleButton("##acct", ImVec2(b.x - a.x, b.y - a.y));
        bool hov = ImGui::IsItemHovered() || ImGui::IsPopupOpen("acctmenu");
        if (hov) dl->AddRectFilled(ImVec2(a.x - 6, a.y), b, IM_COL32(255, 255, 255, 14), 8);
        ImVec2 ac(a.x, p.y + (h - av) / 2);
        if (avatar_.Valid()) dl->AddImageRounded((ImTextureID)avatar_.Id(), ac, ImVec2(ac.x + av, ac.y + av), ImVec2(0, 0), ImVec2(1, 1), IM_COL32_WHITE, av / 2);
        else
        {
            dl->AddCircleFilled(ImVec2(ac.x + av / 2, ac.y + av / 2), av / 2, ui::Grad(0.45f), 32);
            char ini[2] = { name.empty() ? '?' : (char)toupper((unsigned char)name[0]), 0 };
            ImVec2 is = TextSize(f, u, ini);
            dl->AddText(f, u, ImVec2(ac.x + (av - is.x) / 2, ac.y + (av - is.y) / 2), kWhite, ini);
        }
        float tx = ac.x + av + u * 0.7f;
        ImGui::PushClipRect(ImVec2(tx, p.y), ImVec2(tx + nameW, p.y + h), true);
        dl->AddText(f, u, ImVec2(tx, p.y + h / 2 - u - 1), kWhite, name.c_str());
        ImGui::PopClipRect();
        dl->AddText(ImVec2(tx, p.y + h / 2 + 1), IM_COL32(140, 140, 150, 255), sub.c_str());
        // chevron
        float cx = b.x - u * 0.7f, cy = p.y + h / 2, k = u * 0.22f;
        dl->AddTriangleFilled(ImVec2(cx - k, cy - k * 0.5f), ImVec2(cx + k, cy - k * 0.5f), ImVec2(cx, cy + k * 0.6f), IM_COL32(150, 150, 160, 255));
        hotX_.push_back({ a.x - 6 - p.x, b.x - p.x });
        if (click) ImGui::OpenPopup("acctmenu");

        // developer accounts: Public / Beta / Dev (localhost), right beside the profile
        if (IsDev())
        {
            static const char* chans[] = { "Public", "Beta", "Dev (localhost)", "2026" };
            int cur = S.channel == "beta" ? 1 : S.channel == "dev" ? 2 : S.channel == "2026" ? 3 : 0;
            float cwd = std::round(u * 8.6f), fh = ImGui::GetFrameHeight();
            ImVec2 ca(a.x - u * 1.4f - cwd, p.y + (h - fh) / 2 - 1);
            ImGui::SetCursorScreenPos(ca);
            ImGui::SetNextItemWidth(cwd);
            ImGui::BeginDisabled(Busy() || (launch_ && launch_->done && launch_->ok));
            if (ImGui::BeginCombo("##chan", chans[cur]))
            {
                for (int i = 0; i < 4; i++)
                    if (ImGui::Selectable(chans[i], cur == i)) { S.channel = i == 1 ? "beta" : i == 2 ? "dev" : i == 3 ? "2026" : "public"; S.Save(); }
                ImGui::EndCombo();
            }
            ImGui::EndDisabled();
            ImGui::SetItemTooltip("Which patch PLAY uses (developer accounts only).\nPublic: what everyone gets.  Beta: the beta patch.\nDev: the dev patch, and PLAY starts your own server on this PC (%s) and plays on it.\n2026: Rec Room 2026 with the Campus 2026 patch, on the live Campus servers.", S.devServer.c_str());
            hotX_.push_back({ ca.x - p.x, ca.x + cwd - p.x });
        }

        ImGui::SetNextWindowPos(ImVec2(b.x, p.y + h + 6), ImGuiCond_Always, ImVec2(1, 0));
        ImGui::SetNextWindowSize(ImVec2(std::max(u * 15, b.x - a.x + 6), 0));
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(6, 6));
        ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(0, 2));
        ImGui::PushStyleVar(ImGuiStyleVar_PopupRounding, 8);
        ImGui::PushStyleColor(ImGuiCol_PopupBg, IM_COL32(28, 28, 33, 252));
        ImGui::PushStyleColor(ImGuiCol_Border, IM_COL32(255, 255, 255, 22));
        ImGui::PushStyleColor(ImGuiCol_HeaderHovered, IM_COL32(255, 255, 255, 20));
        if (ImGui::BeginPopup("acctmenu"))
        {
            ImGui::Dummy(ImVec2(0, 2));
            ImGui::SetCursorPosX(ImGui::GetCursorPosX() + 8);
            ImGui::PushFont(f, u);
            ImGui::TextUnformatted(name.c_str());
            ImGui::PopFont();
            ImGui::SetCursorPosX(ImGui::GetCursorPosX() + 8);
            ImGui::TextDisabled("@%s", session_.username.c_str());
            if (me_.is_object() && me_.value("tokens", -1LL) >= 0)
            {
                ImGui::SetCursorPosX(ImGui::GetCursorPosX() + 8);
                long long tk = me_.value("tokens", 0LL);
                int gifts = me_.value("gifts", 0);
                std::string line = std::to_string(tk) + " tokens";
                if (gifts > 0) line += "  -  " + std::to_string(gifts) + (gifts == 1 ? " gift" : " gifts") + " waiting";
                ImGui::TextDisabled("%s", line.c_str());
            }
            ImGui::Dummy(ImVec2(0, 4));
            ImGui::Separator();
            ImGui::Dummy(ImVec2(0, 2));
            ImVec2 rs(0, u * 1.9f);
            auto item = [&](const char* label) {
                ImGui::PushStyleVar(ImGuiStyleVar_SelectableTextAlign, ImVec2(0, 0.5f));
                bool c = ImGui::Selectable(("    " + std::string(label)).c_str(), false, 0, rs);
                ImGui::PopStyleVar();
                return c;
            };
            if (item("View my profile")) OpenUrl(S.server + "/user/" + session_.username);
            if (item("Settings")) page_ = Page::Settings;
            ImGui::BeginDisabled(Busy());
            if (item("Sign out")) SignOut();
            ImGui::EndDisabled();
            ImGui::EndPopup();
        }
        ImGui::PopStyleColor(3);
        ImGui::PopStyleVar(3);
    }
    ImGui::SetCursorScreenPos(ImVec2(p.x, p.y + h));
}

void App::DrawPlay()
{
    const float u = ImGui::GetFontSize();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 a = ImGui::GetWindowPos(), sz = ImGui::GetWindowSize(), b(a.x + sz.x, a.y + sz.y);

    // the art, full bleed, fading into the action bar
    EnsureHero();
    if (hero_.Valid()) Cover(dl, hero_, a, b);
    else ui::GradientRect(dl, a, b);
    float fade = a.y + sz.y * 0.50f;
    dl->AddRectFilledMultiColor(ImVec2(a.x, fade), ImVec2(b.x, b.y - u * 6.5f), IM_COL32(12, 12, 15, 0), IM_COL32(12, 12, 15, 0), IM_COL32(12, 12, 15, 225), IM_COL32(12, 12, 15, 225));
    dl->AddRectFilled(ImVec2(a.x, b.y - u * 6.5f), b, IM_COL32(12, 12, 15, 225));
    dl->AddRectFilledMultiColor(a, ImVec2(b.x, a.y + u * 2.5f), IM_COL32(0, 0, 0, 90), IM_COL32(0, 0, 0, 90), IM_COL32(0, 0, 0, 0), IM_COL32(0, 0, 0, 0));

    const float pad = u * 2.2f, bottom = b.y - pad;
    bool locked = Busy() || UpdPhase() == 2;
    const bool g26 = Is2026();      // the 2026 channel: another game (RecRoom.exe), found in Steam or chosen in Settings > Developer
    bool haveGame = g26 ? IsGameDir2026(S.gameDir2026) : (IsGameDir(S.gameDir) && !dbg_.noGame);
    // (debug panel) a game build to offer when the server hasn't told us one
    const std::string shaNow = (gameSha_.empty() && (dbg_.noGame || dbg_.gameUpdate)) ? std::string("debug") : gameSha_;
    const uint64_t sizeNow = gameSize_ ? gameSize_ : 3800000000ull;

    // ---- right: Desktop / VR and PLAY
    ImVec2 playSz(std::round(u * 13), std::round(u * 3.4f));
    ImVec2 pa(b.x - pad - playSz.x, bottom - playSz.y);
    bool running = (launch_ && launch_->done && launch_->ok) || dbg_.running;
    int iph = 0; uint64_t igot = 0, itotal = 0;       // the install's phase / progress (real, or the debug panel's fake)
    if (install_) { iph = install_->phase; igot = install_->got; itotal = install_->total; }
    else if (dbg_.installing) DebugInstallState(iph, igot, itotal);
    bool installing = install_ != nullptr || dbg_.installing;      // (the fake one may just have finished)
    bool canInstall = !g26 && !haveGame && !findState_ && !shaNow.empty() && !locked && UpdPhase() != 2;
    bool updateAvail = haveGame && !g26 && !shaNow.empty() && ((!S.gameSha.empty() && S.gameSha != gameSha_) || dbg_.gameUpdate);
    bool banned = AccountBanned();
    bool canPlay = !banned && !locked && UpdPhase() != 2 && haveGame && (g26 || !findState_) && !running;
    // No game folder means DOWNLOAD, period - a disabled PLAY next to "Can't start yet" is a
    // worse answer than a DOWNLOAD button that waits a second for the server's game info before
    // it enables (canInstall still gates on gameSha_, so the click is only live once we know
    // what to fetch). The faint "Already have build installed" link under DOWNLOAD covers the
    // player who really does have the files and just picked the wrong folder.
    // A banned account overrides every other state: BANNED, disabled, and the sub-text below
    // carries the time left + reason, same as the website's site-wide ban banner.
    std::string label = banned ? "BANNED" :
                        installing ? (installUpdate_ ? "UPDATING" : "INSTALLING") :
                        playPhase_ == 1 || playPhase_ == 2 || playPhase_ == 4 || playAfterSteam_ ? "GETTING READY" : launch_ && !launch_->done ? "STARTING" : running ? "RUNNING" :
                        !haveGame ? (g26 ? "FIND GAME" : "DOWNLOAD") : updateAvail ? "UPDATE" : "PLAY";
    ImGui::SetCursorScreenPos(pa);
    ImGui::PushFont(BoldF(), u * 1.35f);
    if (ui::PrimaryButton((label + "##play").c_str(), playSz, label == "DOWNLOAD" ? canInstall : label == "FIND GAME" ? (!locked && !banned && UpdPhase() != 2) : canPlay))
    {
        if (g26 && !haveGame) Choose2026Folder();
        else if (dbg_.noGame || dbg_.gameUpdate) { dbg_.installing = true; dbg_.installStart = ImGui::GetTime(); }   // debug: a FAKE install
        else if (label == "DOWNLOAD") InstallGame(false);
        else if (label == "UPDATE") InstallGame(true);
        else Play();
    }
    ImGui::PopFont();

    // First launch, no game installed yet: faint link under DOWNLOAD for players who already have the files.
    // The InstallJob auto-sets S.gameDir to the extracted folder (the one with Recroom_Release.exe), so after
    // Download completes the Settings page's Game Folder points at it with no extra step. For players who
    // already have the build, ChooseFolder() picks their existing one.
    if (label == "DOWNLOAD" && !installing && !running)
    {
        const char* alt = "Already have build installed";
        ImVec2 ats = ImGui::CalcTextSize(alt);
        ImVec2 lp(pa.x + (playSz.x - ats.x) * 0.5f, pa.y + playSz.y + u * 0.6f);
        ImGui::SetCursorScreenPos(lp);
        bool altClicked = ImGui::InvisibleButton("##alreadyhavebuild", ats);
        bool altHov = ImGui::IsItemHovered();
        ImU32 altCol = altHov ? IM_COL32(195, 195, 205, 255) : IM_COL32(130, 130, 140, 190);
        dl->AddText(lp, altCol, alt);
        if (altHov) dl->AddLine(ImVec2(lp.x, lp.y + ats.y), ImVec2(lp.x + ats.x, lp.y + ats.y), altCol);
        if (altHov) ImGui::SetMouseCursor(ImGuiMouseCursor_Hand);
        if (altClicked) ChooseFolder();
    }
    if (installing || (playPhase_ == 2 && patchJob_))
    {
        uint64_t got = installing ? igot : patchJob_->sent.load(), total = installing ? itotal : patchJob_->total.load();
        float k = total ? std::clamp((float)got / total, 0.0f, 1.0f) : 0.0f;
        ImVec2 ba(pa.x, bottom + 6), bb(pa.x + playSz.x, bottom + 9);
        dl->AddRectFilled(ba, bb, IM_COL32(255, 255, 255, 30), 2);
        ui::GradientRect(dl, ba, ImVec2(ba.x + playSz.x * k, bb.y));
    }

    ImVec2 pillSz(std::round(u * 11), std::round(u * 2.3f));
    ImVec2 pp(pa.x - u * 1.4f - pillSz.x, pa.y + (playSz.y - pillSz.y) / 2 - u * 0.55f);
    ImGui::SetCursorScreenPos(pp);
    static const char* modes[] = { "Desktop", "VR" };
    if (Pill("mode", &S.mode, modes, 2, pillSz, !locked && !running)) S.Save();
    std::string how = S.mode == 1 ? "Start SteamVR first" :
                      std::string(S.windowMode == 1 ? "Fullscreen" : S.windowMode == 2 ? "Borderless" : "Windowed") + "  -  " +
                      kResolutions[std::clamp(S.resolution, 0, kResolutionCount - 1)].label;
    ImVec2 hs = ImGui::CalcTextSize(how.c_str());
    dl->AddText(ImVec2(pp.x + (pillSz.x - hs.x) / 2, pp.y + pillSz.y + 6), IM_COL32(150, 150, 160, 255), how.c_str());

    // ---- left: what's going on
    std::string head, sub;
    bool bad = false;
    if (UpdPhase() == 2) { head = "Updating the launcher"; sub = "Version " + UpdVersionShown() + " is on its way."; }
    else if (installing)
    {
        head = (installUpdate_ || dbg_.gameUpdate) ? "Updating Campus" : "Installing Campus";
        int ph = iph;
        uint64_t got = igot, total = itotal;
        if (ph == 0)
        {
            double now = ImGui::GetTime();
            if (speedAt_ == 0 || got < speedGot_) { speedAt_ = now; speedGot_ = got; }
            else if (now - speedAt_ >= 1.0) { speed_ = (got - speedGot_) / (now - speedAt_); speedAt_ = now; speedGot_ = got; }
            sub = "Downloading " + Gb(got) + " of " + Gb(total);
            if (speed_ > 0)
            {
                char t[64];
                snprintf(t, sizeof t, "  -  %.1f MB/s", speed_ / 1e6);
                sub += t;
                if (total > got) { int mins = (int)((total - got) / speed_ / 60) + 1; sub += "  -  about " + std::to_string(mins) + " min left"; }
            }
        }
        else if (ph == 1) sub = "Checking the download...";
        else sub = "Unpacking  " + std::to_string(total ? (int)(100.0 * got / total) : 0) + "%";
    }
    else if (g26 && !haveGame)
    {
        head = "Rec Room 2026 not found";
        bad = statusBad_;
        sub = statusBad_ ? status_ : "Choose the folder with RecRoom.exe (build 20260323.14) - it is looked for in your Steam libraries.";
    }
    else if (!haveGame && findState_) { head = "Looking for Campus"; sub = "Finding the game folder on this PC..."; }
    else if (!haveGame)
    {
        head = "Install Campus";
        bad = statusBad_;
        if (statusBad_)
            // Bad folder picked / something else wrong: say what, but keep DOWNLOAD as the way out.
            sub = status_ + "  Press DOWNLOAD to install Campus, or try the link below.";
        else if (!shaNow.empty())
            sub = "Download the game (" + Gb(sizeNow) + ") - or press 'Already have build installed' if you have it.";
        else
            // DOWNLOAD is already shown; it stays disabled while this message is up.
            sub = "Getting the game info from the server...";
    }
    else if (updateAvail && !statusBad_ && playPhase_ == 0 && !launch_) { head = "Update available"; sub = "A new version of Campus is ready (" + Gb(sizeNow) + " download)."; }
    else if (playPhase_ == 1 || playPhase_ == 2 || playPhase_ == 4 || playAfterSteam_)
    {
        head = "Getting ready";
        sub = status_;
        if (playPhase_ == 2 && patchJob_ && patchJob_->total) sub += "  " + std::to_string((int)(100.0 * patchJob_->sent / patchJob_->total)) + "%";
    }
    else if (launch_ && !launch_->done) { head = "Starting Campus"; sub = status_; }
    else if (running) { head = "Campus is running"; sub = "Have fun! The launcher tidies up when the game closes."; }
    else if (statusBad_) { head = "Can't start yet"; sub = status_; bad = true; }
    else
    {
        head = g26 ? "Rec Room 2026 - ready" : "Ready to play";
        sub = "Signed in as @" + session_.username + "   -   Launcher " LV_VERSION_STR LV_BUILD_TAG
            + (!justUpdatedFromVer_.empty() ? " (just updated from " + justUpdatedFromVer_ + ")"
                                            : updLatest_ ? " (up to date)" : "");
    }
    // Ban headline wins over everything except an in-flight install / launcher-updating state
    // (the latter two already own the sub, and the ban still blocks PLAY via canPlay / Play()).
    if (banned && !installing && UpdPhase() != 2)
    {
        head = "Account banned";
        sub = BanLine();
        bad = true;
    }
    float leftW = pp.x - a.x - pad - u * 2;
    ImFont* f = BoldF();
    float hsz = u * 1.75f;
    // On first launch (gameSha_ known) the faint "Already have build installed" link under the big
    // DOWNLOAD button covers the "pick existing folder" path, so skip the duplicate left-side button.
    // Keep it on the "Game not found" error path where gameSha_ is empty and there is no DOWNLOAD.
    bool folderBtn = !g26 && !haveGame && !findState_ && !installing && shaNow.empty();
    bool cancelBtn = installing && iph == 0;
    float blockH = hsz + u * 0.4f + u + (folderBtn || cancelBtn ? u * 2.8f : 0);
    float ty = bottom - playSz.y / 2 - blockH / 2;
    dl->AddText(f, hsz, ImVec2(a.x + pad, ty), kWhite, head.c_str());
    ImGui::PushClipRect(ImVec2(a.x + pad, ty), ImVec2(a.x + pad + leftW, b.y), true);
    dl->AddText(ImVec2(a.x + pad, ty + hsz + u * 0.4f), bad ? ui::kBad : IM_COL32(175, 175, 185, 255), Ellipsize(sub, leftW).c_str());
    ImGui::PopClipRect();
    if (!sub.empty() && ImGui::IsMouseHoveringRect(ImVec2(a.x + pad, ty + hsz), ImVec2(a.x + pad + leftW, ty + hsz + u * 1.6f)) &&
        ImGui::CalcTextSize(sub.c_str()).x > leftW)
        ImGui::SetTooltip("%s", sub.c_str());
    if (folderBtn)
    {
        ImGui::SetCursorScreenPos(ImVec2(a.x + pad, ty + hsz + u * 0.4f + u + u * 0.8f));
        if (Btn(shaNow.empty() ? "Choose game folder..." : "I already have it - choose folder...")) ChooseFolder();
    }
    if (cancelBtn)
    {
        ImGui::SetCursorScreenPos(ImVec2(a.x + pad, ty + hsz + u * 0.4f + u + u * 0.8f));
        if (Btn("Pause")) { if (install_) install_->cancel = true; else dbg_.installing = false; }
    }
}

void App::DrawSignIn()
{
    const float u = ImGui::GetFontSize();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    ImVec2 a = ImGui::GetWindowPos(), sz = ImGui::GetWindowSize(), b(a.x + sz.x, a.y + sz.y);
    EnsureHero();
    if (hero_.Valid()) Cover(dl, hero_, a, b);
    dl->AddRectFilled(a, b, IM_COL32(10, 10, 13, 205));

    static float cardH = u * 22;
    float cw = std::round(u * 23);
    ImGui::SetCursorPos(ImVec2((sz.x - cw) / 2, std::max(u, (sz.y - cardH) / 2)));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, IM_COL32(22, 22, 27, 248));
    ImGui::PushStyleColor(ImGuiCol_Border, IM_COL32(255, 255, 255, 22));
    ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 12);
    ImGui::PushStyleVar(ImGuiStyleVar_ChildBorderSize, 1);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(u * 2, u * 1.8f));
    ImGui::PushStyleVar(ImGuiStyleVar_ItemSpacing, ImVec2(6, 6));
    ImGui::PushStyleVar(ImGuiStyleVar_FrameRounding, 7);
    ImGui::PushStyleVar(ImGuiStyleVar_FramePadding, ImVec2(u * 0.75f, u * 0.6f));
    ImGui::PushStyleVar(ImGuiStyleVar_FrameBorderSize, 1);
    ImGui::PushStyleColor(ImGuiCol_FrameBg, IM_COL32(13, 13, 16, 255));
    ImGui::PushStyleColor(ImGuiCol_FrameBgHovered, IM_COL32(16, 16, 20, 255));
    ImGui::PushStyleColor(ImGuiCol_Border, IM_COL32(255, 255, 255, 28));
    ImGui::BeginChild("card", ImVec2(cw, 0), ImGuiChildFlags_Borders | ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_AlwaysUseWindowPadding, ImGuiWindowFlags_NoScrollbar);
    ImDrawList* cdl = ImGui::GetWindowDrawList();
    float w = ImGui::GetContentRegionAvail().x;
    ImGui::PushFont(BoldF(), u * 1.35f);
    ImGui::TextUnformatted("Sign in to Campus");
    ImGui::PopFont();
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(kSoft));
    ImGui::TextUnformatted("Use your Campus account to play.");
    ImGui::PopStyleColor();
    ImGui::Dummy(ImVec2(0, u * 0.4f));

    bool busy = loginJob_ != nullptr;
    ImGui::BeginDisabled(busy);
    ImGui::SetNextItemWidth(-1);
    bool enter = ImGui::InputTextWithHint("##user", "Username", &loginUser_, ImGuiInputTextFlags_EnterReturnsTrue);
    if (ImGui::IsWindowAppearing() && loginUser_.empty()) ImGui::SetKeyboardFocusHere(-1);
    ImGui::SetNextItemWidth(-1);
    enter |= ImGui::InputTextWithHint("##pass", "Password", &loginPass_, ImGuiInputTextFlags_Password | ImGuiInputTextFlags_EnterReturnsTrue);
    ImGui::EndDisabled();
    ImGui::Dummy(ImVec2(0, u * 0.3f));
    bool ready = !busy && !loginUser_.empty() && !loginPass_.empty();
    ImGui::PushFont(BoldF(), u * 1.05f);
    if (ui::PrimaryButton(busy ? "SIGNING IN...##in" : "SIGN IN##in", ImVec2(-1, std::round(u * 2.8f)), ready) || (enter && ready)) SignIn();
    ImGui::PopFont();
    if (!loginError_.empty())
    {
        ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(ui::kBad));
        ImGui::TextWrapped("%s", loginError_.c_str());
        ImGui::PopStyleColor();
    }
    ImGui::Dummy(ImVec2(0, u * 0.5f));
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(kSoft));
    ImGui::TextUnformatted("No account yet?");
    ImGui::PopStyleColor();
    ImGui::SameLine(0, 6);
    if (Link("Create one##signup")) OpenUrl(S.server + "/signup");
    float sw = ImGui::CalcTextSize("Settings").x;
    ImGui::SameLine(ImGui::GetWindowWidth() - ImGui::GetStyle().WindowPadding.x - sw);
    if (Link("Settings##set")) page_ = Page::Settings;
    ImGui::EndChild();
    cardH = ImGui::GetItemRectSize().y;
    ImGui::PopStyleColor(3);
    ImGui::PopStyleVar(7);
    ImGui::PopStyleColor(2);
}

void App::DrawCustomize()
{
    const float u = ImGui::GetFontSize();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    float W = ImGui::GetContentRegionAvail().x;

    // ---- heading + the switch
    ImVec2 top = ImGui::GetCursorScreenPos();
    ImGui::PushFont(BoldF(), u * 1.7f);
    ImGui::TextUnformatted("In-game colours");
    ImGui::PopFont();
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(kSoft));
    ImGui::PushTextWrapPos(ImGui::GetCursorPosX() + W * 0.62f);
    ImGui::TextWrapped("Recolours the gradients and highlights across Rec Room's menus. It's saved to your account, so it follows you to any PC - and a change shows in game within 30 seconds, no restart needed.");
    ImGui::PopTextWrapPos();
    ImGui::PopStyleColor();
    if (themeLoaded_)
    {
        const char* st = theme_.enabled ? "On" : "Off";
        float swW = ImGui::GetFrameHeight() * 0.95f * 1.8f, stW = ImGui::CalcTextSize("Off").x;
        ImVec2 back = ImGui::GetCursorScreenPos();
        ImGui::SetCursorScreenPos(ImVec2(top.x + W - swW - stW - 10, top.y + u * 0.45f));
        ImGui::AlignTextToFramePadding();
        ImGui::TextUnformatted(st);
        ImGui::SameLine(0, 10);
        if (Switch("##enabled", &theme_.enabled)) themeChangedAt_ = ImGui::GetTime();
        ImGui::SetCursorScreenPos(back);
    }
    ImGui::Dummy(ImVec2(0, u * 0.6f));

    if (!themeLoaded_)
    {
        if (themeJob_) ImGui::TextDisabled("Loading your colours...");
        else
        {
            ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(ui::kBad));
            ImGui::TextWrapped("%s", themeMsg_.empty() ? "Couldn't load your colours." : themeMsg_.c_str());
            ImGui::PopStyleColor();
            if (Btn("Try again")) LoadTheme();
        }
        return;
    }
    if (presets_.empty() && !presetsJob_)
    {
        std::string url = Url("/api/launcher/v1/theme/presets");
        presetsJob_ = RunNet([url](NetJob&) { return HttpGet(url, ""); });
    }

    const float gap = u * 2.0f, leftW = std::floor((W - gap) * 0.57f), rightW = W - gap - leftW;
    ImVec2 colTop = ImGui::GetCursorScreenPos();

    // ---- left: presets
    ImGui::BeginDisabled(!theme_.enabled);
    Heading("PRESETS");
    const int cols = 3;
    const float cg = u * 0.7f, cardW = std::floor((leftW - cg * (cols - 1)) / cols), cardH = std::round(u * 4.4f);
    int count = (int)presets_.size() + 1;
    ImVec2 g0 = ImGui::GetCursorScreenPos();
    for (int i = 0; i < count; i++)
    {
        bool isCustom = i == (int)presets_.size();
        const float (*stops)[3] = isCustom ? custom_.stops : presets_[i].stops;
        const std::string& id = isCustom ? custom_.preset : presets_[i].id;
        const char* name = isCustom ? "Custom" : presets_[i].name.c_str();
        ImVec2 ca(g0.x + (cardW + cg) * (i % cols), g0.y + (cardH + cg) * (i / cols)), cb(ca.x + cardW, ca.y + cardH);
        ImGui::SetCursorScreenPos(ca);
        ImGui::PushID(i);
        bool click = ImGui::InvisibleButton("##preset", ImVec2(cardW, cardH));
        bool hov = ImGui::IsItemHovered();
        ImGui::PopID();
        bool on = theme_.preset == id;
        float alpha = theme_.enabled ? 1.0f : 0.35f;
        dl->AddRectFilled(ca, cb, hov && theme_.enabled ? kCardHov : kCard, 9);
        float in = 6;
        Ramp(dl, ImVec2(ca.x + in, ca.y + in), ImVec2(cb.x - in, cb.y - u * 1.75f), stops, 6, alpha);
        dl->AddText(ImVec2(ca.x + in + 4, cb.y - u * 1.75f + (u * 1.75f - u) / 2), on ? kWhite : IM_COL32(190, 190, 198, (int)(255 * alpha)), name);
        if (on) dl->AddRect(ca, cb, theme_.enabled ? kWhite : IM_COL32(255, 255, 255, 90), 9.0f, ImDrawFlags_None, 2.0f);
        if (click && !on)
        {
            if (isCustom) { theme_ = custom_; theme_.enabled = true; }
            else
            {
                theme_.preset = presets_[i].id;
                memcpy(theme_.stops, presets_[i].stops, sizeof theme_.stops);
                memcpy(theme_.accent, presets_[i].accent, sizeof theme_.accent);
            }
            themeChangedAt_ = ImGui::GetTime();
        }
    }
    float leftBottom = g0.y + (cardH + cg) * ((count + cols - 1) / cols) - cg;
    if (presets_.empty())
    {
        ImGui::SetCursorScreenPos(ImVec2(g0.x, g0.y + cardH + cg));
        ImGui::TextDisabled(presetsJob_ ? "Loading the presets..." : "Couldn't load the presets - only Custom for now.");
        leftBottom += u * 1.5f;
    }

    // ---- right: preview + the colours (a group, so each new line starts at its left edge)
    float rx = colTop.x + leftW + gap;
    ImGui::SetCursorScreenPos(ImVec2(rx, colTop.y));
    ImGui::BeginGroup();
    Heading("PREVIEW");
    {
        ImVec2 pa = ImGui::GetCursorScreenPos(), pb(pa.x + rightW, pa.y + std::round(u * 8.6f));
        dl->AddRectFilled(pa, pb, IM_COL32(236, 236, 240, 255), 10);     // Rec Room's menus are light
        float in = u * 0.8f;
        if (theme_.enabled) Ramp(dl, ImVec2(pa.x + in, pa.y + in), ImVec2(pb.x - in, pa.y + in + u * 2.6f), theme_.stops, 7);
        else dl->AddRectFilled(ImVec2(pa.x + in, pa.y + in), ImVec2(pb.x - in, pa.y + in + u * 2.6f), IM_COL32(64, 186, 226, 255), 7);
        ImFont* f = BoldF();
        dl->AddText(f, u * 1.1f, ImVec2(pa.x + in + u * 0.8f, pa.y + in + (u * 2.6f - u * 1.1f) / 2), kWhite, "Campus");
        // an accent button and a highlighted row
        float by = pa.y + in + u * 3.4f;
        ImU32 acc = theme_.enabled ? Rgb(theme_.accent) : IM_COL32(255, 140, 26, 255);
        ImVec2 b0(pa.x + in, by), b1(pa.x + in + u * 6.5f, by + u * 2.0f);
        dl->AddRectFilled(b0, b1, acc, u);
        ImVec2 ts = TextSize(f, u * 0.9f, "Join");
        dl->AddText(f, u * 0.9f, ImVec2((b0.x + b1.x - ts.x) / 2, (b0.y + b1.y - ts.y) / 2), kWhite, "Join");
        ImVec2 r0(b1.x + u * 0.7f, by), r1(pb.x - in, by + u * 2.0f);
        dl->AddRectFilled(r0, r1, IM_COL32(210, 210, 216, 255), u);
        dl->AddText(f, u * 0.9f, ImVec2(r0.x + u * 0.9f, (r0.y + r1.y - ts.y) / 2), IM_COL32(70, 70, 80, 255), "Friends");
        float ly = by + u * 2.8f;
        dl->AddRectFilled(ImVec2(pa.x + in, ly), ImVec2(pb.x - in, ly + 5), IM_COL32(205, 205, 212, 255), 3);
        if (theme_.enabled) Ramp(dl, ImVec2(pa.x + in, ly), ImVec2(pa.x + in + (pb.x - pa.x - in * 2) * 0.62f, ly + 5), theme_.stops, 2);
        else dl->AddRectFilled(ImVec2(pa.x + in, ly), ImVec2(pa.x + in + (pb.x - pa.x - in * 2) * 0.62f, ly + 5), acc, 3);
        ImGui::Dummy(ImVec2(rightW, pb.y - pa.y));
        if (!theme_.enabled) ImGui::TextDisabled("Off - Rec Room's own colours.");
    }
    ImGui::Dummy(ImVec2(0, u * 0.5f));
    Heading("COLOURS");
    {
        static const char* names[] = { "1", "2", "3", "4", "Accent" };
        const float sg = u * 0.6f, sw = std::floor((rightW - 4 * sg) / 5), sh = std::round(sw * 0.62f);
        float y0 = ImGui::GetCursorScreenPos().y;
        swatchY_ = y0 + sh / 2;
        for (int i = 0; i < 5; i++)
        {
            float* c = i < 4 ? theme_.stops[i] : theme_.accent;
            ImVec2 sa(rx + (sw + sg) * i, y0), sb(sa.x + sw, sa.y + sh);
            ImGui::SetCursorScreenPos(sa);
            ImGui::PushID(i);
            if (ImGui::InvisibleButton("##sw", ImVec2(sw, sh))) editSlot_ = editSlot_ == i ? -1 : i;
            bool hov = ImGui::IsItemHovered() && theme_.enabled;
            float alpha = theme_.enabled ? 1.0f : 0.35f;
            dl->AddRectFilled(sa, sb, Rgb(c, alpha), 8);
            bool sel = editSlot_ == i;
            dl->AddRect(sa, sb, sel ? kWhite : hov ? IM_COL32(255, 255, 255, 200) : IM_COL32(255, 255, 255, 30), 8.0f, ImDrawFlags_None, sel || hov ? 2.0f : 1.0f);
            ImVec2 ns = ImGui::CalcTextSize(names[i]);
            dl->AddText(ImVec2(sa.x + (sw - ns.x) / 2, sb.y + 5), IM_COL32(140, 140, 150, 255), names[i]);
            ImGui::PopID();
        }
        ImGui::SetCursorScreenPos(ImVec2(rx, y0 + sh + u * 1.7f));
        ImGui::PushTextWrapPos(rx - ImGui::GetWindowPos().x + rightW);
        ImGui::TextDisabled("Click a colour to change it on the wheel. Changing a preset's colours makes it your Custom one.");
        ImGui::PopTextWrapPos();
    }
    ImGui::EndGroup();
    ImGui::EndDisabled();
    float rightBottom = ImGui::GetCursorScreenPos().y;

    // ---- the colour wheel for the chosen swatch, in a card over the presets (a child window
    // draws above everything else on the page)
    if (editSlot_ >= 0 && theme_.enabled)
    {
        static const char* titles[] = { "Gradient colour 1", "Gradient colour 2", "Gradient colour 3", "Gradient colour 4", "Accent - buttons and highlights" };
        float* c = editSlot_ < 4 ? theme_.stops[editSlot_] : theme_.accent;
        static float panelH = u * 20;
        const float panelW = std::round(u * 17);
        ImVec2 back = ImGui::GetCursorScreenPos();
        float py = std::clamp(swatchY_ - panelH / 2, colTop.y, std::max(colTop.y, ImGui::GetWindowPos().y + ImGui::GetWindowHeight() - panelH - u));
        ImVec2 pp(rx - u * 1.0f - panelW, py);
        ImGui::SetCursorScreenPos(pp);
        ImGui::PushStyleColor(ImGuiCol_ChildBg, IM_COL32(30, 30, 36, 255));
        ImGui::PushStyleColor(ImGuiCol_Border, IM_COL32(255, 255, 255, 34));
        ImGui::PushStyleVar(ImGuiStyleVar_ChildRounding, 10);
        ImGui::PushStyleVar(ImGuiStyleVar_ChildBorderSize, 1);
        ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(u * 1.1f, u * 1.0f));
        ImGui::BeginChild("wheel", ImVec2(panelW, 0), ImGuiChildFlags_Borders | ImGuiChildFlags_AutoResizeY | ImGuiChildFlags_AlwaysUseWindowPadding, ImGuiWindowFlags_NoScrollbar);
        ImGui::PushFont(BoldF(), u);
        ImGui::TextUnformatted(titles[editSlot_]);
        ImGui::PopFont();
        ImGui::SetNextItemWidth(-1);
        if (ImGui::ColorPicker3("##wheel", c, ImGuiColorEditFlags_PickerHueWheel | ImGuiColorEditFlags_DisplayHex | ImGuiColorEditFlags_NoSidePreview))
        {
            theme_.preset = "custom";                  // editing a preset starts your own from it
            custom_ = theme_;
            themeChangedAt_ = ImGui::GetTime();
        }
        ImGui::Dummy(ImVec2(0, 2));
        bool done = Btn("Done", ImVec2(-1, 0));
        ImGui::EndChild();
        ImVec2 ca = ImGui::GetItemRectMin(), cb = ImGui::GetItemRectMax();
        panelH = cb.y - ca.y;
        ImGui::PopStyleVar(3);
        ImGui::PopStyleColor(2);
        ImGui::SetCursorScreenPos(back);
        // Done, Escape, or a click anywhere but the card and the swatches closes it
        bool outside = ImGui::IsMouseClicked(0) && !ImGui::IsMouseHoveringRect(ca, cb, false) &&
                       !ImGui::IsMouseHoveringRect(ImVec2(rx, swatchY_ - u * 3), ImVec2(rx + rightW, swatchY_ + u * 3), false);
        if (done || outside || ImGui::IsKeyPressed(ImGuiKey_Escape)) editSlot_ = -1;
    }

    // ---- footer: what happened to the last save
    ImGui::SetCursorScreenPos(ImVec2(colTop.x, std::max(leftBottom, rightBottom) + u * 1.2f));
    ImVec2 lp = ImGui::GetCursorScreenPos();
    dl->AddLine(lp, ImVec2(lp.x + W, lp.y), kLine);
    ImGui::Dummy(ImVec2(0, u * 0.6f));
    bool dirty = !(theme_ == themeSaved_);
    const char* msg = saveJob_ ? "Saving..." : dirty ? "Unsaved changes..." : themeMsg_.empty() ? "Saved to your account." : themeMsg_.c_str();
    ImGui::AlignTextToFramePadding();
    ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(themeMsgBad_ && !saveJob_ ? ui::kBad : saveJob_ || dirty ? kSoft : ui::kGood));
    ImGui::TextUnformatted(msg);
    ImGui::PopStyleColor();
    if (themeMsgBad_ && !saveJob_)
    {
        ImGui::SameLine(0, 12);
        if (Btn("Retry")) SaveTheme();
    }
    float rw = ImGui::CalcTextSize("Back to Campus colours").x + 20;
    ImGui::SameLine(colTop.x - ImGui::GetWindowPos().x + W - rw);
    ImGui::BeginDisabled(theme_.preset == "campus" && theme_.enabled);
    if (Btn("Back to Campus colours", ImVec2(rw, 0)))
    {
        theme_.enabled = true;
        theme_.preset = "campus";
        for (auto& p : presets_) if (p.id == "campus") { memcpy(theme_.stops, p.stops, sizeof theme_.stops); memcpy(theme_.accent, p.accent, sizeof theme_.accent); }
        themeChangedAt_ = ImGui::GetTime();
    }
    ImGui::EndDisabled();
}

// Rec Room writes its log to %APPDATA%\..\LocalLow\Against Gravity\Rec Room\Player.log and, when it starts again,
// moves the last one to Player-prev.log. Copy one of them next to the launcher as CampusPlayerLog<time>.log so a
// player can send it in. The game keeps writing to Player.log while it runs, so open it with every sharing flag.
void App::CopyPlayerLog(bool previous)
{
    playerLogFile_.clear();
    playerLogBad_ = true;

    PWSTR low = nullptr;
    fs::path src;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppDataLow, 0, nullptr, &low)) && low) src = fs::path(low);
    CoTaskMemFree(low);
    if (src.empty()) { playerLogMsg_ = "Couldn't find your LocalLow folder."; Log(playerLogMsg_); return; }
    src = src / L"Against Gravity" / L"Rec Room" / (previous ? L"Player-prev.log" : L"Player.log");

    HANDLE in = CreateFileW(src.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (in == INVALID_HANDLE_VALUE)
    {
        playerLogMsg_ = std::string("There is no ") + (previous ? "previous " : "") + "game log yet (" + PathU8(src) + "). Start the game once, then try again.";
        Log(playerLogMsg_);
        return;
    }

    std::time_t t = std::time(nullptr);
    std::tm tm{};
    localtime_s(&tm, &t);
    wchar_t stamp[32];
    wcsftime(stamp, 32, L"%Y-%m-%d_%H-%M-%S", &tm);
    std::wstring name = std::wstring(previous ? L"CampusPlayerLogPrev" : L"CampusPlayerLog") + stamp + L".log";

    // next to the launcher; if that folder can't be written to (Program Files...), the launcher's settings folder
    fs::path dirs[2] = { U8Path(launcherDir_), U8Path(AppDataDir()) };
    fs::path dest;
    HANDLE out = INVALID_HANDLE_VALUE;
    for (auto& d : dirs)
    {
        dest = d / name;
        out = CreateFileW(dest.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (out != INVALID_HANDLE_VALUE) break;
    }
    if (out == INVALID_HANDLE_VALUE)
    {
        CloseHandle(in);
        playerLogMsg_ = "Couldn't create a file next to the launcher or in its settings folder.";
        Log(playerLogMsg_);
        return;
    }

    std::vector<char> buf(1 << 20);
    unsigned long long total = 0;
    bool ok = true;
    for (;;)
    {
        DWORD got = 0;
        if (!ReadFile(in, buf.data(), (DWORD)buf.size(), &got, nullptr)) { ok = false; break; }
        if (got == 0) break;
        DWORD put = 0;
        if (!WriteFile(out, buf.data(), got, &put, nullptr) || put != got) { ok = false; break; }
        total += got;
    }
    CloseHandle(in);
    CloseHandle(out);
    if (!ok)
    {
        std::error_code ec;
        fs::remove(dest, ec);
        playerLogMsg_ = "Copying the game log failed - is the disk full?";
        Log(playerLogMsg_);
        return;
    }

    playerLogBad_ = false;
    playerLogFile_ = PathU8(dest);
    char sz[48];
    snprintf(sz, sizeof sz, "%.1f MB", total / 1048576.0);
    playerLogMsg_ = std::string("Saved ") + sz + " as " + PathU8(dest.filename()) + " in " + PathU8(dest.parent_path());
    Log(playerLogMsg_);
}

void App::DrawSettings()
{
    const float u = ImGui::GetFontSize();
    ImDrawList* dl = ImGui::GetWindowDrawList();
    float W = ImGui::GetContentRegionAvail().x;
    const float col = std::round(u * 12);
    bool signedIn = session_.Valid();
    bool locked = Busy() || findState_;

    if (!signedIn)
    {
        if (Link("< Back to sign in##back")) page_ = Page::Play;
        ImGui::Dummy(ImVec2(0, 4));
    }
    ImGui::PushFont(BoldF(), u * 1.7f);
    ImGui::TextUnformatted("Settings");
    ImGui::PopFont();
    ImGui::Dummy(ImVec2(0, u * 0.4f));

    auto section = [&](const char* title) {
        ImGui::Dummy(ImVec2(0, u * 0.5f));
        ImVec2 p = ImGui::GetCursorScreenPos();
        dl->AddLine(p, ImVec2(p.x + W, p.y), kLine);
        ImGui::Dummy(ImVec2(0, u * 0.7f));
        Heading(title);
    };
    auto label = [&](const char* text) {
        ImGui::AlignTextToFramePadding();
        ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(kSoft));
        ImGui::TextUnformatted(text);
        ImGui::PopStyleColor();
        ImGui::SameLine(col);
    };

    section("GAME");
    label("Game folder");
    std::string dir = findState_ ? std::string("Looking for the game...") : S.gameDir.empty() ? std::string("Not found") : S.gameDir;
    ImGui::TextUnformatted(Ellipsize(dir, W - col).c_str());
    if (ImGui::IsItemHovered() && !S.gameDir.empty()) ImGui::SetTooltip("%s", S.gameDir.c_str());
    ImGui::SetCursorPosX(col);
    ImGui::BeginDisabled(locked);
    if (Btn("Change...")) ChooseFolder();
    ImGui::SameLine();
    ImGui::BeginDisabled(!IsGameDir(S.gameDir));
    if (Btn("Check files")) CheckFiles();
    ImGui::EndDisabled();
    ImGui::SameLine();
    if (Btn("Find it for me")) { S.gameDir.clear(); FindGame(); }
    ImGui::EndDisabled();
    if (!gameSha_.empty() && signedIn)
    {
        ImGui::SetCursorPosX(col);
        ImGui::BeginDisabled(locked);
        bool have = IsGameDir(S.gameDir);
        if (Btn(have ? "Repair (download the game again)" : "Install Campus...")) InstallGame(have);
        ImGui::EndDisabled();
        ImGui::SameLine();
        ImGui::AlignTextToFramePadding();
        ImGui::TextDisabled("%s download, %s on disk", Gb(gameSize_).c_str(), Gb(gameUnpacked_).c_str());
    }
    if (statusBad_ && !Busy())
    {
        ImGui::SetCursorPosX(col);
        ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(ui::kBad));
        ImGui::PushTextWrapPos(W);
        ImGui::TextWrapped("%s", status_.c_str());
        ImGui::PopTextWrapPos();
        ImGui::PopStyleColor();
    }
    if (IsDev())
    {
        label("2026 game folder");
        {
            std::string d26 = S.gameDir2026.empty() ? std::string("Not found") : S.gameDir2026;
            ImGui::TextUnformatted(Ellipsize(d26, W - col).c_str());
            if (ImGui::IsItemHovered() && !S.gameDir2026.empty()) ImGui::SetTooltip("%s", S.gameDir2026.c_str());
            ImGui::SetCursorPosX(col);
            ImGui::BeginDisabled(locked);
            if (Btn("Change...##g26")) Choose2026Folder();
            ImGui::SameLine();
            if (Btn("Find it for me##g26")) { S.gameDir2026.clear(); Find2026(); }
            ImGui::EndDisabled();
            ImGui::SetCursorPosX(col);
            ImGui::PushTextWrapPos(W);
            ImGui::TextDisabled("The folder with RecRoom.exe of Rec Room 20260323.14 (Steam manifest 8867414017562658247). The patch is downloaded into its PATCH "
                                "folder for each launch and deleted when the game closes.");
            ImGui::PopTextWrapPos();
        }
    }
    label("Antivirus");
    if (IsGameDir(S.gameDir))
    {
        std::string pf = PatchFolder(S.gameDir);
        ImGui::TextUnformatted(Ellipsize(pf, W - col).c_str());
        ImGui::SetCursorPosX(col);
        if (Btn("Copy path")) ImGui::SetClipboardText(pf.c_str());
        ImGui::SameLine();
        if (Btn("Open folder"))
        {
            EnsurePatchFolder(S.gameDir);
            ShellExecuteW(nullptr, L"open", U8Path(pf).wstring().c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        }
        ImGui::SetCursorPosX(col);
        ImGui::PushTextWrapPos(W);
        ImGui::TextDisabled("The Campus patch is downloaded into this folder every time you play. If your antivirus deletes or blocks it, "
                            "add this folder to its exclusions (Windows Security: Virus & threat protection > Manage settings > Exclusions).");
        ImGui::PopTextWrapPos();
    }
    else ImGui::TextDisabled("Install or choose the game first.");

    if (IsDev())
    {
        section("DEVELOPER");
        label("Release channel");
        static const char* chans[] = { "Public", "Beta", "Dev", "2026" };
        int cur = S.channel == "beta" ? 1 : S.channel == "dev" ? 2 : S.channel == "2026" ? 3 : 0;
        ImGui::BeginDisabled(locked);
        if (Pill("chan", &cur, chans, 4, ImVec2(u * 26, ImGui::GetFrameHeight() * 1.15f), !locked))
        {
            S.channel = cur == 1 ? "beta" : cur == 2 ? "dev" : cur == 3 ? "2026" : "public";
            S.Save();
        }
        ImGui::EndDisabled();
        ImGui::SetCursorPosX(col);
        ImGui::PushTextWrapPos(W);
        ImGui::TextDisabled("Public: the patch everyone gets.  Beta: the beta patch (PushChannel.bat).  Dev: the dev patch, and the game talks to YOUR server "
                            "on this PC - start it with the CAMPUS_LOCAL_DEV=1 environment variable, then press PLAY.  2026: Rec Room 2026 (the 2026 game folder above) with the "
                            "Campus 2026 patch, on the live Campus servers.");
        ImGui::PopTextWrapPos();
        label("Dev server");
        static char dev[128];
        static bool devInit = false;
        if (!devInit) { snprintf(dev, sizeof dev, "%s", S.devServer.c_str()); devInit = true; }
        ImGui::SetNextItemWidth(u * 14);
        if (ImGui::InputText("##devserver", dev, sizeof dev)) { S.devServer = dev[0] ? dev : "localhost:44334"; S.Save(); }
        ImGui::SetItemTooltip("host:port of your local server (https). Used by the Dev channel only.");
        {
            LocalServerOptions lo = LocalOpts();
            std::string sd = ResolveServerDir(lo), nd = ResolveNetDir(lo);
            label("Server folder");
            static char sdir[300]; static bool sInit = false;
            if (!sInit) { snprintf(sdir, sizeof sdir, "%s", S.devServerDir.c_str()); sInit = true; }
            ImGui::SetNextItemWidth(W - col);
            if (ImGui::InputTextWithHint("##sdir", sd.empty() ? "folder with CampusServer.csproj" : sd.c_str(), sdir, sizeof sdir)) { S.devServerDir = sdir; S.Save(); }
            label("CampusNet folder");
            static char ndir[300]; static bool nInit = false;
            if (!nInit) { snprintf(ndir, sizeof ndir, "%s", S.devNetDir.c_str()); nInit = true; }
            ImGui::SetNextItemWidth(W - col);
            if (ImGui::InputTextWithHint("##ndir", nd.empty() ? "folder with CampusNet.csproj" : nd.c_str(), ndir, sizeof ndir)) { S.devNetDir = ndir; S.Save(); }
            ImGui::SetCursorPosX(col);
            ImGui::PushTextWrapPos(W);
            ImGui::TextDisabled("With the Dev channel, PLAY starts your own server and CampusNet from these folders (built if needed) when they are not already running, "
                                "and stops the ones it started when the game closes. Blank = the Campus2025Server-2025 folder next to the launcher.");
            ImGui::PopTextWrapPos();
            ImGui::SetCursorPosX(col);
            if (Btn("Stop my local servers now")) { int n = StopLocalServers(lo); localMsg_ = n ? "Stopped " + std::to_string(n) + " process" + (n == 1 ? "" : "es") + "." : "Neither of them was running."; }
            ImGui::SameLine();
            if (Btn("Open their logs")) ShellExecuteW(nullptr, L"open", U8Path(AppDataDir()).wstring().c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            if (!localMsg_.empty()) { ImGui::SameLine(); ImGui::TextDisabled("%s", localMsg_.c_str()); }
        }
    }

    section("DISPLAY");
    ImGui::BeginDisabled(locked);
    label("Play in");
    static const char* modes[] = { "Desktop", "VR" };
    if (Pill("mode", &S.mode, modes, 2, ImVec2(u * 14, ImGui::GetFrameHeight() * 1.15f), !locked)) S.Save();
    label("Window");
    static const char* wins[] = { "Windowed", "Fullscreen", "Borderless" };
    if (Pill("win", &S.windowMode, wins, 3, ImVec2(u * 21, ImGui::GetFrameHeight() * 1.15f), !locked && S.mode == 0)) S.Save();
    ImGui::BeginDisabled(S.mode == 1);
    label("Resolution");
    ImGui::SetNextItemWidth(u * 21);
    if (ImGui::BeginCombo("##res", kResolutions[std::clamp(S.resolution, 0, kResolutionCount - 1)].label))
    {
        for (int i = 0; i < kResolutionCount; i++)
            if (ImGui::Selectable(kResolutions[i].label, S.resolution == i)) { S.resolution = i; S.Save(); }
        ImGui::EndCombo();
    }
    ImGui::EndDisabled();
    if (S.mode == 1)
    {
        ImGui::SetCursorPosX(col);
        ImGui::TextDisabled("VR: start SteamVR or your headset's PC app before you press Play.");
    }
    ImGui::EndDisabled();

    section("LAUNCHER");
    label("When the game starts");
    if (ImGui::Checkbox("Close the launcher", &S.closeOnPlay)) S.Save();
    ImGui::SetCursorPosX(col);
    ImGui::TextDisabled("It keeps running out of sight until the game closes, then tidies up and exits.");
    label("Files");
    if (Btn("Open the launcher's folder")) ShellExecuteW(nullptr, L"open", U8Path(AppDataDir()).wstring().c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    ImGui::SameLine();
    if (Btn(showLog_ ? "Hide the log" : "Show the log")) showLog_ = !showLog_;
    label("Game log");
    if (Btn("Copy it next to the launcher")) CopyPlayerLog(false);
    ImGui::SetItemTooltip("Copies Rec Room's Player.log to this folder as CampusPlayerLog<date and time>.log - send it to us when something goes wrong.");
    ImGui::SameLine();
    if (Btn("Last session's log")) CopyPlayerLog(true);
    ImGui::SetItemTooltip("The game starts a new log every time it opens and keeps only the one before it. If it crashed and you have already\nstarted it again, use this one.");
    if (!playerLogMsg_.empty())
    {
        ImGui::SetCursorPosX(col);
        ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(playerLogBad_ ? ui::kBad : kSoft));
        ImGui::PushTextWrapPos(W);
        ImGui::TextWrapped("%s", playerLogMsg_.c_str());
        ImGui::PopTextWrapPos();
        ImGui::PopStyleColor();
        if (!playerLogFile_.empty())
        {
            ImGui::SetCursorPosX(col);
            if (Btn("Show the file"))
            {
                std::wstring arg = L"/select,\"" + U8Path(playerLogFile_).wstring() + L"\"";
                ShellExecuteW(nullptr, L"open", L"explorer.exe", arg.c_str(), nullptr, SW_SHOWNORMAL);
            }
        }
    }

    section("ACCOUNT");
    label("Signed in as");
    if (signedIn)
    {
        ImGui::TextUnformatted(("@" + session_.username).c_str());
        ImGui::SetCursorPosX(col);
        if (Btn("View my profile")) OpenUrl(S.server + "/user/" + session_.username);
        ImGui::SameLine();
        ImGui::BeginDisabled(Busy());
        if (Btn("Sign out")) SignOut();
        ImGui::EndDisabled();
        label("Steam");
        {
            std::vector<std::string> ids;
            if (me_.is_object())
                for (auto& v : me_.value("steam_ids", json::array())) if (v.is_string()) ids.push_back(v.get<std::string>());
            uint64_t local = LocalSteamId();
            bool here = local && std::find(ids.begin(), ids.end(), std::to_string(local)) != ids.end();
            if (here) ImGui::TextColored(ImGui::ColorConvertU32ToFloat4(ui::kGood), SteamWhitelisted()
                                         ? "Linked to this PC's Steam account - whitelisted for PC play"
                                         : "Linked to this PC's Steam account (not whitelisted yet - press Link)");
            else if (!ids.empty()) ImGui::TextUnformatted(ids.size() == 1 ? "Linked to a different Steam account" : "Linked to other Steam accounts");
            else ImGui::TextDisabled("Not linked");
            bool canLink = local && (!here || !SteamWhitelisted());
            if (canLink || steamJob_)
            {
                ImGui::SetCursorPosX(col);
                ImGui::BeginDisabled(steamJob_ != nullptr || Busy() || !IsGameDir(S.gameDir));
                if (Btn(steamJob_ ? "Linking..." : "Link this PC's Steam account")) LinkSteam();
                ImGui::EndDisabled();
            }
            else if (!local && !here)
            {
                ImGui::SetCursorPosX(col);
                ImGui::TextDisabled("Start Steam and sign in to link it.");
            }
            if (!steamMsg_.empty() && !steamJob_)
            {
                ImGui::SetCursorPosX(col);
                ImGui::PushStyleColor(ImGuiCol_Text, ImGui::ColorConvertU32ToFloat4(steamBad_ ? ui::kBad : kSoft));
                ImGui::PushTextWrapPos(W);
                ImGui::TextWrapped("%s", steamMsg_.c_str());
                ImGui::PopTextWrapPos();
                ImGui::PopStyleColor();
            }
        }
    }
    else
    {
        ImGui::TextDisabled("Nobody");
        ImGui::SetCursorPosX(col);
        if (Btn("Sign in")) page_ = Page::Play;
    }

    section("ABOUT");
    label("Launcher");
    std::string ver = "Version " LV_VERSION_STR LV_BUILD_TAG;
    if (!justUpdatedFromVer_.empty()) ver += " - just updated from " + justUpdatedFromVer_;
    else if (updPhase_ == 1) ver += " - checking for updates...";
    else if (UpdPhase() == 2) ver += " - updating to " + UpdVersionShown();
    else if (!updError_.empty()) ver += " - update failed";
    else if (updLatest_) ver += " - up to date";
    ImGui::TextUnformatted(ver.c_str());
    label("Server");
    ImGui::TextDisabled("%s", S.server.c_str());
    ImGui::Dummy(ImVec2(0, u));
}

void App::DrawLog()
{
    if (!showLog_) return;
    const float u = ImGui::GetFontSize();
    ImGui::SetNextWindowSize(ImVec2(u * 44, u * 22), ImGuiCond_FirstUseEver);
    if (ImGui::Begin("Log", &showLog_, ImGuiWindowFlags_NoCollapse))
    {
        if (ImGui::SmallButton("Copy all"))
        {
            std::string all;
            for (auto& l : log_) all += l + "\n";
            ImGui::SetClipboardText(all.c_str());
        }
        ImGui::Separator();
        ImGui::BeginChild("lines");
        for (auto& l : log_) ImGui::TextUnformatted(l.c_str());
        if (ImGui::GetScrollY() >= ImGui::GetScrollMaxY() - 4) ImGui::SetScrollHereY(1.0f);
        ImGui::EndChild();
    }
    ImGui::End();
}

void App::DrawUpdating()
{
    if (UpdPhase() != 2) return;
    ImGui::OpenPopup("Updating");
    const float u = ImGui::GetFontSize();
    ImGui::SetNextWindowSize(ImVec2(u * 24, 0));
    ImGui::SetNextWindowPos(ImGui::GetMainViewport()->GetCenter(), ImGuiCond_Always, ImVec2(0.5f, 0.5f));
    ImGui::PushStyleVar(ImGuiStyleVar_WindowRounding, 10);
    ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(u * 1.4f, u * 1.2f));
    ImGui::PushStyleColor(ImGuiCol_PopupBg, IM_COL32(24, 24, 29, 255));
    if (ImGui::BeginPopupModal("Updating", nullptr, ImGuiWindowFlags_NoTitleBar | ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoMove))
    {
        ImGui::PushFont(BoldF(), u * 1.15f);
        ImGui::Text("Updating to version %s", UpdVersionShown().c_str());
        ImGui::PopFont();
        ImGui::TextDisabled("The launcher restarts by itself when it's done.");
        ImGui::Dummy(ImVec2(0, 4));
        uint64_t got = updJob_ ? updJob_->sent.load() : 0, total = updJob_ ? updJob_->total.load() : 0;
        if (dbg_.launcherUpdating) { total = 12u << 20; got = (uint64_t)(std::fmod(ImGui::GetTime() * 2.5, 12.0) * (1u << 20)); }   // fake progress, loops
        ImDrawList* dl = ImGui::GetWindowDrawList();
        ImVec2 a = ImGui::GetCursorScreenPos();
        float w = ImGui::GetContentRegionAvail().x;
        dl->AddRectFilled(a, ImVec2(a.x + w, a.y + 6), IM_COL32(255, 255, 255, 25), 3);
        if (total) ui::GradientRect(dl, a, ImVec2(a.x + w * std::clamp((float)got / total, 0.0f, 1.0f), a.y + 6));
        ImGui::Dummy(ImVec2(w, 10));
        char lbl[64];
        if (total) snprintf(lbl, sizeof lbl, "%.1f of %.1f MB", got / 1048576.0, total / 1048576.0);
        else snprintf(lbl, sizeof lbl, "Starting...");
        ImGui::TextDisabled("%s", lbl);
        ImGui::EndPopup();
    }
    ImGui::PopStyleColor();
    ImGui::PopStyleVar(2);
}
