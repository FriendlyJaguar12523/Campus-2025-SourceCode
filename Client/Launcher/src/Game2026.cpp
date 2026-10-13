#include "Game2026.h"
#include "Util.h"
#include <windows.h>
#include <bcrypt.h>
#include <tlhelp32.h>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <thread>

namespace fs = std::filesystem;

// The build the 2026 patch's RVAs were measured on (20260323.14, Steam manifest 8867414017562658247). These are the same three
// fingerprints as 2026Patch\src\RR\BuildGuard.h - the patch refuses to hook any other build, so the launcher says so up front
// instead of starting a game that will sit there unpatched.
namespace {
    struct Fingerprint { const wchar_t* name; const char* hash; };
    const Fingerprint kBuild[] = {
        { L"GameAssembly.dll", "A6D90770CDFDA4043EADB278A16198ED0907C3BA2AC09BC37D06CD4DD22A1D84" },
        { L"Referee.dll",      "5868C90A7C67757F3B678AA356CBD933D646C9749E0ACAA31B7DCC9EAEE12EAD" },
        { L"UnityPlayer.dll",  "30F4A99C49566B2C5045469B5A39572DD2298ADC31F7FE2BC39D8BB8FC9D75BB" },
    };

    // The measured retired-service arena seam (Referee, build 20260323.14): the slot holding the pointer to the arena pointer, and the
    // instruction (mov eax,[r8]) that dereferences it. See the 2026 patch's AGENTS.md ("Injector discovery and early Referee bootstrap").
    constexpr uintptr_t kArenaSlot = 0x6C10858;
    constexpr uintptr_t kBootstrapInstruction = 0x69839D;
}

static bool FileThere(const fs::path& p) { std::error_code ec; return fs::is_regular_file(p, ec); }

bool IsGameDir2026(const std::string& dir)
{
    if (dir.empty()) return false;
    fs::path d = U8Path(dir);
    if (!FileThere(d / kGameExe2026)) return false;
    for (auto& m : kBuild) if (!FileThere(d / m.name)) return false;
    return true;
}

std::vector<std::string> Missing2026Files(const std::string& dir)
{
    std::vector<std::string> out;
    fs::path d = U8Path(dir);
    if (!FileThere(d / kGameExe2026)) out.push_back(kGameExe2026);
    for (auto& m : kBuild) if (!FileThere(d / m.name)) out.push_back(Narrow(m.name));
    return out;
}

static std::string RegString(HKEY root, const wchar_t* key, const wchar_t* value)
{
    wchar_t buf[1024]{};
    DWORD size = sizeof(buf) - sizeof(wchar_t);
    if (RegGetValueW(root, key, value, RRF_RT_REG_SZ, nullptr, buf, &size) != ERROR_SUCCESS) return "";
    return Narrow(buf);
}

std::string FindGameDir2026()
{
    // Steam: the install folder and every library listed in steamapps\libraryfolders.vdf.
    std::vector<fs::path> libraries;
    auto addLibrary = [&](const std::string& p) {
        if (p.empty()) return;
        fs::path lib = U8Path(p);
        for (auto& l : libraries) if (_wcsicmp(l.c_str(), lib.c_str()) == 0) return;
        libraries.push_back(lib);
    };
    std::string steam = RegString(HKEY_CURRENT_USER, L"Software\\Valve\\Steam", L"SteamPath");
    if (steam.empty()) steam = RegString(HKEY_LOCAL_MACHINE, L"SOFTWARE\\WOW6432Node\\Valve\\Steam", L"InstallPath");
    addLibrary(steam);
    if (!steam.empty())
    {
        std::ifstream vdf(U8Path(steam) / "steamapps" / "libraryfolders.vdf");
        std::string line;
        while (std::getline(vdf, line))
        {
            // "path"		"D:\\SteamLibrary"
            size_t k = line.find("\"path\"");
            if (k == std::string::npos) continue;
            size_t a = line.find('"', k + 6);
            size_t b = a == std::string::npos ? a : line.find('"', a + 1);
            if (b == std::string::npos) continue;
            std::string p = line.substr(a + 1, b - a - 1), fixed;
            for (size_t i = 0; i < p.size(); i++) { fixed += p[i]; if (p[i] == '\\' && i + 1 < p.size() && p[i + 1] == '\\') i++; }
            addLibrary(fixed);
        }
    }
    for (auto& lib : libraries)
        for (const char* name : { "RecRoom", "Rec Room" })
        {
            fs::path candidate = lib / "steamapps" / "common" / name;
            if (IsGameDir2026(PathU8(candidate))) return PathU8(candidate);
        }
    return "";
}

std::string PickFolder2026(void* owner, const std::string& start)
{
    return PickFolderTitled(owner, start, L"Choose the folder with RecRoom.exe (Rec Room 20260323.14)");
}

bool GameRunning2026()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;
    PROCESSENTRY32W pe{ sizeof(pe) };
    bool found = false;
    if (Process32FirstW(snap, &pe))
        do if (_wcsicmp(pe.szExeFile, L"RecRoom.exe") == 0) { found = true; break; }
        while (Process32NextW(snap, &pe));
    CloseHandle(snap);
    return found;
}

// SHA-256 of a file, upper-case hex, streamed (GameAssembly.dll is about 250 MB). "" when unreadable.
static std::string HashFileUpper(const fs::path& path)
{
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) return "";
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::string out;
    if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0) == 0)
    {
        std::vector<unsigned char> buffer(1 << 20);
        DWORD got = 0;
        bool ok = true;
        while (ok)
        {
            if (!ReadFile(file, buffer.data(), (DWORD)buffer.size(), &got, nullptr)) { ok = false; break; }
            if (!got) break;
            ok = BCryptHashData(hash, buffer.data(), got, 0) == 0;
        }
        unsigned char digest[32]{};
        if (ok && BCryptFinishHash(hash, digest, sizeof digest, 0) == 0)
        {
            static const char* hex = "0123456789ABCDEF";
            for (unsigned char b : digest) { out += hex[b >> 4]; out += hex[b & 15]; }
        }
        BCryptDestroyHash(hash);
    }
    CloseHandle(file);
    return out;
}

std::string CheckBuild2026(const std::string& dir)
{
    fs::path d = U8Path(dir);
    std::string changed;
    for (auto& m : kBuild)
    {
        std::string h = HashFileUpper(d / m.name);
        if (h.empty()) return "Can't read " + Narrow(m.name) + " in the Rec Room 2026 folder.";
        if (h != m.hash) changed += (changed.empty() ? "" : ", ") + Narrow(m.name);
    }
    if (changed.empty()) return "";
    return "This isn't Rec Room 20260323.14 (" + changed + " differ). The 2026 patch only works on that build (Steam manifest 8867414017562658247) - "
           "choose that game folder in Settings > Developer.";
}

static uintptr_t ModuleBase(DWORD pid, const wchar_t* name)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    MODULEENTRY32W me{ sizeof(me) };
    uintptr_t base = 0;
    if (Module32FirstW(snap, &me))
        do if (_wcsicmp(me.szModule, name) == 0) { base = (uintptr_t)me.modBaseAddr; break; }
        while (Module32NextW(snap, &me));
    CloseHandle(snap);
    return base;
}

// Give Referee its local arena. Referee's startup reads [Referee+0x6C10858], then its pointee, and later dereferences the arena
// (mov eax,[r8] at Referee+0x69839D); the service that normally fills it in is retired, so a zero there is a crash a moment later.
// Wait for the measured instruction and an initialised slot (never write blindly as soon as the module name appears), keep an arena
// that is already there, otherwise allocate 64 MiB and give its three channel descriptors (stride 0x1500030) two bounded queues each
// (a zero length underflows the queue capacity (length - 0x1C) / 2 and faults later). All headers are written BEFORE the pointer is
// published. There is no service consumer for these local queues; a full queue is handled the way the client handles any full queue.
static bool BootstrapReferee(HANDLE process, DWORD pid, DWORD timeoutMs)
{
    uintptr_t referee = 0;
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (GetTickCount64() < deadline && WaitForSingleObject(process, 0) == WAIT_TIMEOUT)
    {
        if (!referee) referee = ModuleBase(pid, L"Referee.dll");
        if (!referee) { SwitchToThread(); continue; }
        unsigned char instruction[3]{};
        SIZE_T count = 0;
        if (!ReadProcessMemory(process, (void*)(referee + kBootstrapInstruction), instruction, 3, &count) || count != 3 ||
            memcmp(instruction, "\x41\x8B\x00", 3)) { SwitchToThread(); continue; }
        uintptr_t pointer = 0, arena = 0;
        if (!ReadProcessMemory(process, (void*)(referee + kArenaSlot), &pointer, sizeof pointer, &count) || count != sizeof pointer || !pointer)
        { SwitchToThread(); continue; }
        if (!ReadProcessMemory(process, (void*)pointer, &arena, sizeof arena, &count) || count != sizeof arena) { SwitchToThread(); continue; }
        if (arena) return true;                      // already initialised: keep it
        arena = (uintptr_t)VirtualAllocEx(process, nullptr, 0x04000000, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!arena) return false;
        for (unsigned channel = 0; channel < 3; ++channel)
        {
            const uintptr_t segment = arena + channel * 0x1500030ull;
            const uintptr_t offsets[2] = { 0xA0, 0x100040 };
            const uint32_t lengths[2] = { 0x80000, 0x1000000 };
            for (unsigned queue = 0; queue < 2; ++queue)
            {
                const uint32_t capacity = (lengths[queue] - 0x1C) / 2;
                const uint32_t header[8] = { lengths[queue], 0, 0, capacity, 0, 0, capacity, 0 };
                if (!WriteProcessMemory(process, (void*)(segment + offsets[queue]), header, sizeof header, &count) || count != sizeof header) return false;
            }
        }
        return WriteProcessMemory(process, (void*)pointer, &arena, sizeof arena, &count) && count == sizeof arena;
    }
    return false;
}

// LoadLibraryW in the game. kernel32's export can be forwarded to another system module: find the module that really owns it and apply the
// function's offset to that module's base in the TARGET process (an absolute pointer from this process is not a remote address).
static bool InjectDll(HANDLE process, DWORD pid, const std::wstring& dll)
{
    SIZE_T bytes = (dll.size() + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote) return false;
    bool ok = false;
    if (WriteProcessMemory(process, remote, dll.c_str(), bytes, nullptr))
    {
        auto loader = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        HMODULE owner = nullptr;
        wchar_t ownerPath[MAX_PATH]{};
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (const wchar_t*)loader, &owner);
        GetModuleFileNameW(owner, ownerPath, MAX_PATH);
        const wchar_t* ownerName = wcsrchr(ownerPath, L'\\');
        ownerName = ownerName ? ownerName + 1 : ownerPath;
        uintptr_t remoteOwner = ModuleBase(pid, ownerName);
        auto remoteLoad = remoteOwner ? (LPTHREAD_START_ROUTINE)(remoteOwner + (uintptr_t)loader - (uintptr_t)owner) : nullptr;
        if (HANDLE th = remoteLoad ? CreateRemoteThread(process, nullptr, 0, remoteLoad, remote, 0, nullptr) : nullptr)
        {
            if (WaitForSingleObject(th, 15000) == WAIT_OBJECT_0)
            {
                const wchar_t* name = wcsrchr(dll.c_str(), L'\\');
                name = name ? name + 1 : dll.c_str();
                ok = ModuleBase(pid, name) != 0;      // loaded: the patch's own startup runs asynchronously (Logs\2026patch.log)
            }
            CloseHandle(th);
        }
    }
    if (ok) VirtualFreeEx(process, remote, 0, MEM_RELEASE);   // (a remote loader that is still running keeps the path)
    return ok;
}

static void Run2026(LaunchOptions o, std::shared_ptr<LaunchJob> job)
{
    fs::path dir = U8Path(o.gameDir), exe = dir / kGameExe2026, dll = U8Path(o.patchDll);
    auto dropPatch = [&] { for (int i = 0; i < 20 && FileThere(dll) && !DeleteFileW(dll.c_str()); i++) Sleep(250); };
    auto fail = [&](const std::string& why) { dropPatch(); job->Say(why); job->done = true; job->exited = true; };
    if (!FileThere(exe)) return fail("Can't find RecRoom.exe - choose the Rec Room 2026 folder in Settings > Developer.");
    if (!FileThere(dll)) return fail("The Campus 2026 patch didn't download - try again.");

    job->Say("Checking the Rec Room 2026 files...");
    std::string bad = CheckBuild2026(o.gameDir);
    if (!bad.empty()) return fail(bad);

    std::wstring cmd = L"\"" + exe.wstring() + L"\" +forcemode:" + (o.vr ? L"vr" : L"screen");
    // The community launchers start the client with ONLY +forcemode:screen / +forcemode:vr, so that is all it gets here: Unity's own
    // window switches (-screen-fullscreen / -window-mode / -screen-width) are not passed to the 2026 client - the game's saved
    // display settings decide. (The launcher's window / resolution choices apply to the 2025 game only.)
    job->Say("Starting the game...");
    job->Note("Command line: " + Narrow(cmd));

    // Environment = this one + CAMPUS_LAUNCH_TOKEN (the patch forwards it with its proof; the server's launcher-launch gate wants it).
    // A stale CAMPUS_NS_HOST / CAMPUS_PHOTON_HOST / CAMPUS_LAUNCH_TOKEN in the launcher's own environment is dropped, so it can't
    // redirect a normal 2026 launch.
    std::vector<wchar_t> envBlock;
    if (LPWCH src = GetEnvironmentStringsW())
    {
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
        if (!o.launchToken.empty())
        {
            std::wstring kv = L"CAMPUS_LAUNCH_TOKEN=" + Widen(o.launchToken);
            envBlock.insert(envBlock.end(), kv.c_str(), kv.c_str() + kv.size() + 1);
        }
        envBlock.push_back(L'\0');
    }

    STARTUPINFOW si{ sizeof si };
    PROCESS_INFORMATION pi{};
    if (!CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, envBlock.empty() ? 0 : CREATE_UNICODE_ENVIRONMENT,
                        envBlock.empty() ? nullptr : envBlock.data(), dir.c_str(), &si, &pi))
        return fail("Couldn't start the game (error " + std::to_string(GetLastError()) + ").");
    job->pid = pi.dwProcessId;
    auto killGame = [&] { TerminateProcess(pi.hProcess, 1); WaitForSingleObject(pi.hProcess, 5000); CloseHandle(pi.hThread); CloseHandle(pi.hProcess); };

    // This window is short and time-critical (the game is already running): spin at top priority, like the community injector does.
    job->Say("Preparing the game for the patch...");
    // Same as the community injector: HIGH process priority class + highest thread priority while it races Referee's first use of the
    // arena, both put back as soon as it is published. (15 s here, a little longer than the injector's 10, for a slow first start.)
    const DWORD oldClass = GetPriorityClass(GetCurrentProcess());
    int oldPriority = GetThreadPriority(GetCurrentThread());
    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    bool ready = BootstrapReferee(pi.hProcess, pi.dwProcessId, 15000);
    SetThreadPriority(GetCurrentThread(), oldPriority);
    if (oldClass) SetPriorityClass(GetCurrentProcess(), oldClass);
    if (!ready)
    {
        DWORD code = 0;
        bool exited = GetExitCodeProcess(pi.hProcess, &code) && code != STILL_ACTIVE;
        if (!exited) TerminateProcess(pi.hProcess, 1);
        WaitForSingleObject(pi.hProcess, 5000);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return fail(exited ? "The game closed before it finished loading (exit code " + std::to_string(code) + ")."
                           : "The game started, but it couldn't be prepared for the Campus patch - try again.");
    }

    job->Say("Loading the Campus 2026 patch...");
    for (int waited = 0; waited < 5000 && !ModuleBase(pi.dwProcessId, L"GameAssembly.dll") && WaitForSingleObject(pi.hProcess, 0) == WAIT_TIMEOUT; waited += 10) Sleep(10);
    bool ok = InjectDll(pi.hProcess, pi.dwProcessId, dll.wstring());
    CloseHandle(pi.hThread);
    if (!ok)
    {
        killGame();                                // an unpatched game can't reach Campus anyway
        return fail("The game started, but the Campus 2026 patch didn't load - try again.");
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

void StartGame2026(const LaunchOptions& o, std::shared_ptr<LaunchJob> job)
{
    std::thread([o, job] { Run2026(o, job); }).detach();
}
