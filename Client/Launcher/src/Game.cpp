#include "Game.h"
#include "Util.h"
#include <windows.h>
#include <shobjidl.h>
#include <tlhelp32.h>
#include <filesystem>
#include <fstream>
#include <thread>

namespace fs = std::filesystem;

const Resolution kResolutions[] = {
    { "Game default", 0, 0 },
    { "1280 x 720", 1280, 720 },
    { "1600 x 900", 1600, 900 },
    { "1920 x 1080", 1920, 1080 },
    { "2560 x 1440", 2560, 1440 },
    { "3840 x 2160", 3840, 2160 },
};
const int kResolutionCount = (int)(sizeof(kResolutions) / sizeof(kResolutions[0]));

static const char* kRequired[] = {
    "Recroom_Release.exe", "GameAssembly.dll", "Referee.dll", "UnityPlayer.dll",
    "baselib.dll", "steam_appid.txt",   // steam_api64.dll lives in Recroom_Release_Data\Plugins, not here
};

static bool FileThere(const fs::path& p) { std::error_code ec; return fs::is_regular_file(p, ec); }

bool IsGameDir(const std::string& dir) { return !dir.empty() && FileThere(U8Path(dir) / kGameExe); }

// The patch lives in <game>\PATCH - one folder players can exclude in their antivirus. The patch
// finds Assets\ and writes Logs\ next to itself, so they are in there too. Older launchers used
// <game>\CampusPatch\Patch; CleanPatchCopies still tidies that.
static fs::path PatchDir(const std::string& gameDir) { return U8Path(gameDir) / "PATCH"; }
static fs::path OldPatchDir(const std::string& gameDir) { return U8Path(gameDir) / "CampusPatch" / "Patch"; }

std::string PatchFolder(const std::string& gameDir) { return gameDir.empty() ? std::string() : PathU8(PatchDir(gameDir)); }

const void* ResourceData(int id, size_t& size);   // Tex.cpp

void EnsurePatchFolder(const std::string& gameDir)
{
    if (gameDir.empty()) return;
    static const struct { int id; const wchar_t* name; } kAssets[] = {
        { 210, L"background.png" }, { 211, L"button.png" }, { 212, L"menu_button.png" },
        { 213, L"player.png" }, { 214, L"toggle_off.png" }, { 215, L"toggle_on.png" },
    };
    std::error_code ec;
    fs::path assets = PatchDir(gameDir) / L"Assets";
    fs::create_directories(assets, ec);
    for (auto& a : kAssets)
    {
        size_t n = 0;
        const void* p = ResourceData(a.id, n);
        if (!p || !n) continue;
        fs::path f = assets / a.name;
        if (fs::is_regular_file(f, ec) && fs::file_size(f, ec) == n) continue;   // already there
        std::ofstream out(f, std::ios::binary | std::ios::trunc);
        out.write((const char*)p, (std::streamsize)n);
    }
}

// withAssets = the 2025 game (its patch has menu art and lives in <game>\PATCH). The 2026 patch has no art and no ini - its settings are compiled in -
// so its copy sits right in the game folder, <game>\cp_<random>.dll, and the patch writes Logs\ next to it (<game>\Logs). CleanPatchCopies removes it.
std::string PatchCopyPath(const std::string& gameDir, bool withAssets)
{
    static const char* hex = "0123456789abcdef";
    std::string name = "cp_";
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    unsigned long long x = (unsigned long long)t.QuadPart ^ ((unsigned long long)GetCurrentProcessId() << 32) ^ GetTickCount64();
    for (int i = 0; i < 16; i++) { x = x * 6364136223846793005ULL + 1442695040888963407ULL; name += hex[(x >> 59) & 15]; }
    if (withAssets) EnsurePatchFolder(gameDir);     // (the 2026 patch has no menu art)
    else return PathU8(U8Path(gameDir) / (name + ".dll"));
    return PathU8(PatchDir(gameDir) / (name + ".dll"));
}

void CleanPatchCopies(const std::string& gameDir)
{
    if (gameDir.empty()) return;
    // (the game folder itself too: the 2026 patch copy is <game>\cp_*.dll - nothing else in a Rec Room folder starts with cp_)
    for (const fs::path& dir : { PatchDir(gameDir), OldPatchDir(gameDir), U8Path(gameDir) })
    {
        std::error_code ec;
        for (fs::directory_iterator it(dir, ec), end; !ec && it != end; it.increment(ec))
        {
            std::wstring n = it->path().filename().wstring();
            bool copy = n.rfind(L"cp_", 0) == 0 && it->path().extension() == L".dll";
            bool shipped = dir != U8Path(gameDir) && _wcsicmp(n.c_str(), L"CampusPatch.dll") == 0;   // the patch used to ship with the game (never touch a file in the game folder itself except cp_*.dll)
            if (copy || shipped) DeleteFileW(it->path().c_str());            // in use by a running game: stays until it closes
        }
    }
}

std::vector<std::string> MissingFiles(const std::string& gameDir)
{
    std::vector<std::string> out;
    for (auto rel : kRequired) if (!FileThere(U8Path(gameDir) / rel)) out.push_back(rel);
    return out;
}

static std::string SearchDown(const fs::path& dir, int depth)
{
    std::error_code ec;
    if (FileThere(dir / kGameExe)) return PathU8(dir);
    if (depth == 0) return "";
    for (fs::directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec))
    {
        if (!it->is_directory(ec)) continue;
        DWORD a = GetFileAttributesW(it->path().c_str());
        if (a != INVALID_FILE_ATTRIBUTES && (a & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM))) continue;
        std::string r = SearchDown(it->path(), depth - 1);
        if (!r.empty()) return r;
    }
    return "";
}

std::string FindGameDir(const std::string& launcherDir)
{
    // 1. launcher.ini next to the launcher: GamePath=...
    {
        std::ifstream f(U8Path(launcherDir) / "launcher.ini");
        std::string line;
        while (std::getline(f, line))
            if (line.rfind("GamePath=", 0) == 0)
            {
                std::string p = line.substr(9);
                p.erase(0, p.find_first_not_of(" \t\r\n"));
                p.erase(p.find_last_not_of(" \t\r\n") + 1);
                if (IsGameDir(p)) return p;
            }
    }
    // 2. the launcher's folder and up to 5 parents (and a Campus subfolder of each)
    fs::path cur = U8Path(launcherDir);
    for (int i = 0; i < 6 && !cur.empty(); i++)
    {
        if (FileThere(cur / kGameExe)) return PathU8(cur);
        if (FileThere(cur / "Campus" / kGameExe)) return PathU8(cur / "Campus");
        if (cur == cur.parent_path()) break;
        cur = cur.parent_path();
    }
    // 3. every fixed drive, 3 folders deep
    DWORD drives = GetLogicalDrives();
    for (char d = 'C'; d <= 'Z'; d++)
    {
        if (!(drives & (1u << (d - 'A')))) continue;
        std::string root = std::string(1, d) + ":\\";
        if (GetDriveTypeA(root.c_str()) != DRIVE_FIXED) continue;
        std::string r = SearchDown(U8Path(root), 3);
        if (!r.empty()) return r;
    }
    return "";
}

std::string PickFolder(void* owner, const std::string& start)
{
    return PickFolderTitled(owner, start, L"Choose the folder with Recroom_Release.exe");
}

std::string PickFolderTitled(void* owner, const std::string& start, const wchar_t* title)
{
    std::string out;
    IFileOpenDialog* dlg = nullptr;
    if (FAILED(CoCreateInstance(CLSID_FileOpenDialog, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&dlg)))) return out;
    DWORD opts = 0;
    dlg->GetOptions(&opts);
    dlg->SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
    dlg->SetTitle(title);
    if (!start.empty())
    {
        IShellItem* si = nullptr;
        if (SUCCEEDED(SHCreateItemFromParsingName(Widen(start).c_str(), nullptr, IID_PPV_ARGS(&si)))) { dlg->SetFolder(si); si->Release(); }
    }
    if (SUCCEEDED(dlg->Show((HWND)owner)))
    {
        IShellItem* item = nullptr;
        if (SUCCEEDED(dlg->GetResult(&item)))
        {
            PWSTR p = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &p))) { out = Narrow(p); CoTaskMemFree(p); }
            item->Release();
        }
    }
    dlg->Release();
    return out;
}

void LaunchJob::Say(const std::string& s)
{
    std::lock_guard<std::mutex> lk(mtx);
    status = s;
    log.push_back(s);
}

void LaunchJob::Note(const std::string& s)
{
    std::lock_guard<std::mutex> lk(mtx);
    log.push_back(s);
}

std::string LaunchJob::Status()
{
    std::lock_guard<std::mutex> lk(mtx);
    return status;
}

static bool HasModules(DWORD pid, const wchar_t* const* mods, int n)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snap == INVALID_HANDLE_VALUE) return false;
    std::vector<bool> found(n, false);
    MODULEENTRY32W me{ sizeof(me) };
    if (Module32FirstW(snap, &me))
        do for (int i = 0; i < n; i++) if (_wcsicmp(me.szModule, mods[i]) == 0) found[i] = true;
        while (Module32NextW(snap, &me));
    CloseHandle(snap);
    for (bool b : found) if (!b) return false;
    return true;
}

bool GameRunning()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;
    PROCESSENTRY32W pe{ sizeof(pe) };
    bool found = false;
    if (Process32FirstW(snap, &pe))
        do if (_wcsicmp(pe.szExeFile, L"Recroom_Release.exe") == 0) { found = true; break; }
        while (Process32NextW(snap, &pe));
    CloseHandle(snap);
    return found;
}

static void Run(LaunchOptions o, std::shared_ptr<LaunchJob> job)
{
    fs::path dir = U8Path(o.gameDir), exe = dir / kGameExe, dll = U8Path(o.patchDll);
    auto dropPatch = [&] { for (int i = 0; i < 20 && FileThere(dll) && !DeleteFileW(dll.c_str()); i++) Sleep(250); };
    auto fail = [&](const std::string& why) { dropPatch(); job->Say(why); job->done = true; job->exited = true; };
    if (!FileThere(exe)) return fail("Can't find Recroom_Release.exe - choose the game folder in Settings.");
    if (!FileThere(dll)) return fail("The Campus patch didn't download - try again.");

    std::wstring cmd = L"\"" + exe.wstring() + L"\" +forcemode:" + (o.vr ? L"vr" : L"screen");
    if (!o.vr)
    {
        if (o.windowMode == 0) cmd += L" -screen-fullscreen 0";
        else cmd += L" -screen-fullscreen 1 -window-mode " + std::wstring(o.windowMode == 1 ? L"exclusive" : L"borderless");
        if (o.resolution > 0 && o.resolution < kResolutionCount)
            cmd += L" -screen-width " + std::to_wstring(kResolutions[o.resolution].w) + L" -screen-height " + std::to_wstring(kResolutions[o.resolution].h);
    }
    job->Say("Starting the game...");
    job->Note("Command line: " + Narrow(cmd));

    STARTUPINFOW si{ sizeof si };
    PROCESS_INFORMATION pi{};
    // Build a custom env block = parent env + CAMPUS_LAUNCH_TOKEN. The patch reads this var at
    // init and forwards it to the server with /api/patch/proof. The server treats a login with
    // a fresh patch proof but NO launcher-launch proof as "ran outside the launcher" and yanks
    // the player's Steam whitelist.
    std::vector<wchar_t> envBlock;
    LPVOID lpEnv = nullptr;
    DWORD createFlags = 0;
    // (and, on the Dev channel, CAMPUS_NS_HOST / CAMPUS_PHOTON_HOST - the patch then talks to the developer's own server)
    {
        std::vector<std::pair<std::wstring, std::wstring>> extra;
        if (!o.launchToken.empty()) extra.push_back({ L"CAMPUS_LAUNCH_TOKEN", Widen(o.launchToken) });
        if (!o.nsHost.empty()) extra.push_back({ L"CAMPUS_NS_HOST", Widen(o.nsHost) });
        if (!o.photonHost.empty()) extra.push_back({ L"CAMPUS_PHOTON_HOST", Widen(o.photonHost) });
        LPWCH src = extra.empty() ? nullptr : GetEnvironmentStringsW();
        if (src)
        {
            // Copy every "KEY=VAL\0" entry, skipping any of ours already there (so ours win, and a stale
            // CAMPUS_NS_HOST in the launcher's own environment can't leak into a normal launch);
            // append ours; then the double-null terminator.
            for (LPWCH p = src; *p; )
            {
                size_t len = wcslen(p);
                bool skip = false;
                for (const wchar_t* k : { L"CAMPUS_LAUNCH_TOKEN=", L"CAMPUS_NS_HOST=", L"CAMPUS_PHOTON_HOST=" })
                    if (_wcsnicmp(p, k, wcslen(k)) == 0) skip = true;
                if (!skip) envBlock.insert(envBlock.end(), p, p + len + 1);
                p += len + 1;
            }
            FreeEnvironmentStringsW(src);
            for (auto& e : extra)
            {
                std::wstring kv = e.first + L"=" + e.second;
                envBlock.insert(envBlock.end(), kv.c_str(), kv.c_str() + kv.size() + 1);
            }
            envBlock.push_back(L'\0');
            lpEnv = envBlock.data();
            createFlags |= CREATE_UNICODE_ENVIRONMENT;
        }
    }
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, createFlags, lpEnv, dir.c_str(), &si, &pi))
        return fail("Couldn't start the game (error " + std::to_string(GetLastError()) + ").");
    job->pid = pi.dwProcessId;

    job->Say("Waiting for the game to load...");
    const wchar_t* required[] = { L"GameAssembly.dll", L"Referee.dll" };
    int waited = 0;
    for (; waited < 30000; waited += 100)
    {
        DWORD code = 0;
        if (GetExitCodeProcess(pi.hProcess, &code) && code != STILL_ACTIVE)
        {
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return fail("The game closed before it finished loading (exit code " + std::to_string(code) + ").");
        }
        if (HasModules(pi.dwProcessId, required, 2)) break;
        Sleep(100);
    }
    if (waited >= 30000)
    {
        TerminateProcess(pi.hProcess, 1);
        WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return fail("The game took too long to load - try again.");
    }

    job->Say("Loading the Campus patch...");
    std::string dllPath = PathU8(dll);
    std::wstring dllW = dll.wstring();
    bool ok = false;
    size_t bytes = (dllW.size() + 1) * sizeof(wchar_t);
    if (LPVOID remote = VirtualAllocEx(pi.hProcess, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE))
    {
        WriteProcessMemory(pi.hProcess, remote, dllW.c_str(), bytes, nullptr);
        auto load = (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        if (HANDLE th = CreateRemoteThread(pi.hProcess, nullptr, 0, load, remote, 0, nullptr))
        {
            WaitForSingleObject(th, 15000);
            DWORD loaded = 0;
            GetExitCodeThread(th, &loaded);
            CloseHandle(th);
            ok = loaded != 0;
        }
        VirtualFreeEx(pi.hProcess, remote, 0, MEM_RELEASE);
    }
    CloseHandle(pi.hThread);
    if (!ok)
    {
        TerminateProcess(pi.hProcess, 1);      // an unpatched game can't reach Campus anyway
        WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hProcess);
        return fail("The game started, but the Campus patch didn't load - try again.");
    }
    job->ok = true;
    job->Say("Campus is running.");
    job->done = true;
    // The game keeps the patch file open until it closes; then the copy goes.
    WaitForSingleObject(pi.hProcess, INFINITE);
    CloseHandle(pi.hProcess);
    dropPatch();
    job->Note(FileThere(dll) ? "The game closed, but the patch copy couldn't be deleted yet - it goes at the next start."
                             : "The game closed - patch copy deleted.");
    job->exited = true;
}

void StartGame(const LaunchOptions& o, std::shared_ptr<LaunchJob> job)
{
    std::thread([o, job] { Run(o, job); }).detach();
}
