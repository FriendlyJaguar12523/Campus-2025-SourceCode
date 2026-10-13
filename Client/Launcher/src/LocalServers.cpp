#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <winhttp.h>
#include <tlhelp32.h>
#include "LocalServers.h"
#include "Util.h"
#include <atomic>
#include <chrono>
#include <filesystem>
#include <mutex>
#include <thread>
#include <vector>

#pragma comment(lib, "ws2_32.lib")
#pragma comment(lib, "winhttp.lib")

namespace fs = std::filesystem;

static std::mutex g_mtx;
static std::string g_status;
static void Say(const std::string& s) { std::lock_guard<std::mutex> lk(g_mtx); g_status = s; }
std::string LocalServersStatus() { std::lock_guard<std::mutex> lk(g_mtx); return g_status; }

static std::atomic<bool> g_srvStarted{ false }, g_netStarted{ false };      // started by THIS launcher run (a server that was already up is never stopped)
bool LocalServersStartedByUs() { return g_srvStarted || g_netStarted; }

static bool IsDir(const fs::path& p) { std::error_code ec; return fs::is_directory(p, ec); }
static bool IsFile(const fs::path& p) { std::error_code ec; return fs::is_regular_file(p, ec); }

// ...\Puck\Campus2025Server-2025 : the repo folder next to (or above) the launcher
static fs::path FindRepo(const std::string& launcherDir)
{
    for (fs::path p = U8Path(launcherDir); !p.empty(); p = p.parent_path())
    {
        fs::path c = p / L"Campus2025Server-2025";
        if (IsDir(c)) return c;
        if (p == p.root_path()) break;
    }
    return {};
}

std::string ResolveServerDir(const LocalServerOptions& o)
{
    if (!o.serverDir.empty()) return o.serverDir;
    fs::path repo = FindRepo(o.launcherDir);
    if (repo.empty()) return "";
    fs::path d = repo / L"Server" / L"2025-RecRoom-Server-E12354-main";
    return IsFile(d / L"CampusServer.csproj") ? PathU8(d) : "";
}

std::string ResolveNetDir(const LocalServerOptions& o)
{
    if (!o.netDir.empty()) return o.netDir;
    fs::path repo = FindRepo(o.launcherDir);
    if (repo.empty()) return "";
    fs::path d = repo / L"CampusNet";
    return IsFile(d / L"CampusNet.csproj") ? PathU8(d) : "";
}

// host:port -> port (443 when there is none); local = localhost / 127.0.0.1
static void SplitHostPort(const std::string& hp, std::string& host, int& port)
{
    size_t c = hp.rfind(':');
    host = c == std::string::npos ? hp : hp.substr(0, c);
    port = c == std::string::npos ? 443 : atoi(hp.c_str() + c + 1);
    if (port <= 0 || port > 65535) port = 443;
}

static bool PortOpen(int port)
{
    static bool wsa = false;
    if (!wsa) { WSADATA d; WSAStartup(MAKEWORD(2, 2), &d); wsa = true; }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return false;
    sockaddr_in a{}; a.sin_family = AF_INET; a.sin_port = htons((u_short)port); inet_pton(AF_INET, "127.0.0.1", &a.sin_addr);
    u_long nb = 1; ioctlsocket(s, FIONBIO, &nb);
    connect(s, (sockaddr*)&a, sizeof a);
    fd_set w; FD_ZERO(&w); FD_SET(s, &w);
    timeval tv{ 0, 400000 };
    bool ok = select(0, nullptr, &w, nullptr, &tv) > 0;
    if (ok) { int err = 0; int len = sizeof err; getsockopt(s, SOL_SOCKET, SO_ERROR, (char*)&err, &len); ok = err == 0; }
    closesocket(s);
    return ok;
}

// the full path of a process image
static std::wstring ImagePath(DWORD pid)
{
    std::wstring out;
    if (HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid))
    {
        wchar_t buf[MAX_PATH * 2]; DWORD n = (DWORD)(sizeof buf / sizeof buf[0]);
        if (QueryFullProcessImageNameW(h, 0, buf, &n)) out.assign(buf, n);
        CloseHandle(h);
    }
    return out;
}

static bool SamePath(const std::wstring& a, const fs::path& b)
{
    std::error_code ec;
    fs::path x = fs::weakly_canonical(fs::path(a), ec), y = fs::weakly_canonical(b, ec);
    return _wcsicmp(x.wstring().c_str(), y.wstring().c_str()) == 0;
}

// processes whose image IS this exe (a campusnet.exe / CampusServer.exe started from that folder)
static std::vector<DWORD> RunningFrom(const fs::path& exe)
{
    std::vector<DWORD> pids;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return pids;
    PROCESSENTRY32W pe{ sizeof(pe) };
    if (Process32FirstW(snap, &pe))
        do if (_wcsicmp(pe.szExeFile, exe.filename().wstring().c_str()) == 0 && SamePath(ImagePath(pe.th32ProcessID), exe)) pids.push_back(pe.th32ProcessID);
        while (Process32NextW(snap, &pe));
    CloseHandle(snap);
    return pids;
}

static std::wstring EnvBlockWith(const std::vector<std::pair<std::wstring, std::wstring>>& extra, std::vector<wchar_t>& block)
{
    block.clear();
    if (LPWCH src = GetEnvironmentStringsW())
    {
        for (LPWCH p = src; *p; )
        {
            size_t len = wcslen(p);
            bool skip = false;
            for (auto& e : extra) if (_wcsnicmp(p, (e.first + L"=").c_str(), e.first.size() + 1) == 0) skip = true;
            if (!skip) block.insert(block.end(), p, p + len + 1);
            p += len + 1;
        }
        FreeEnvironmentStringsW(src);
    }
    for (auto& e : extra) { std::wstring kv = e.first + L"=" + e.second; block.insert(block.end(), kv.c_str(), kv.c_str() + kv.size() + 1); }
    block.push_back(L'\0');
    return L"";
}

// Start a hidden process with its output in a log file. wait = block until it exits (a build); returns the exit code, or -1 if it didn't start.
static long Run(const std::wstring& cmdline, const fs::path& cwd, const std::vector<std::pair<std::wstring, std::wstring>>& env,
                const fs::path& log, bool wait, int timeoutMs = 600000)
{
    SECURITY_ATTRIBUTES sa{ sizeof sa, nullptr, TRUE };
    HANDLE hLog = CreateFileW(log.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    HANDLE hIn = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, &sa, OPEN_EXISTING, 0, nullptr);
    STARTUPINFOW si{ sizeof si };
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE;
    si.hStdInput = hIn; si.hStdOutput = hLog; si.hStdError = hLog;
    std::vector<wchar_t> block; EnvBlockWith(env, block);
    PROCESS_INFORMATION pi{};
    std::wstring cl = cmdline;
    BOOL ok = CreateProcessW(nullptr, cl.data(), nullptr, nullptr, TRUE, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_PROCESS_GROUP,
                             block.data(), cwd.c_str(), &si, &pi);
    if (hLog != INVALID_HANDLE_VALUE) CloseHandle(hLog);
    if (hIn != INVALID_HANDLE_VALUE) CloseHandle(hIn);
    if (!ok) return -1;
    long code = 0;
    if (wait)
    {
        if (WaitForSingleObject(pi.hProcess, timeoutMs) == WAIT_TIMEOUT) { TerminateProcess(pi.hProcess, 1); code = -2; }
        else { DWORD c = 0; GetExitCodeProcess(pi.hProcess, &c); code = (long)c; }
    }
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    return code;
}

// newest .cs / .csproj / .json under dir (the heavy data / output folders skipped) - to know when the build is out of date
static fs::file_time_type NewestSource(const fs::path& dir)
{
    fs::file_time_type best{};
    std::error_code ec;
    for (fs::recursive_directory_iterator it(dir, fs::directory_options::skip_permission_denied, ec), end; !ec && it != end; it.increment(ec))
    {
        const fs::path& p = it->path();
        if (it->is_directory(ec))
        {
            std::wstring n = p.filename().wstring();
            if (!_wcsicmp(n.c_str(), L"bin") || !_wcsicmp(n.c_str(), L"obj") || !_wcsicmp(n.c_str(), L"Data") || !_wcsicmp(n.c_str(), L"Website") ||
                !_wcsicmp(n.c_str(), L"Web") || !_wcsicmp(n.c_str(), L"publish") || !_wcsicmp(n.c_str(), L"AdminPanel") || n == L".vs")
                it.disable_recursion_pending();
            continue;
        }
        std::wstring ext = p.extension().wstring();
        if (_wcsicmp(ext.c_str(), L".cs") && _wcsicmp(ext.c_str(), L".csproj")) continue;
        auto t = fs::last_write_time(p, ec);
        if (!ec && t > best) best = t;
    }
    return best;
}

// make sure <dir>\bin\Release\net8.0\<exe> exists and is newer than the source; build it when not. "" = ready, else why not.
static std::string EnsureBuilt(const fs::path& dir, const wchar_t* exeName, const fs::path& log, const std::vector<std::pair<std::wstring, std::wstring>>& env, const std::string& what)
{
    fs::path exe = dir / L"bin" / L"Release" / L"net8.0" / exeName;
    std::error_code ec;
    bool fresh = IsFile(exe) && fs::last_write_time(exe, ec) >= NewestSource(dir);
    if (fresh) return "";
    Say("Building your local " + what + " (the first time takes a minute)...");
    long code = Run(L"dotnet build -c Release --nologo -v q", dir, env, log, true);
    if (code == -1) return "Couldn't run 'dotnet' - install the .NET SDK to use the Dev channel.";
    if (code != 0 || !IsFile(exe)) return "Building your local " + what + " failed - see " + PathU8(log);
    return "";
}

std::string StartLocalServers(const LocalServerOptions& o)
{
    std::string host; int port;
    SplitHostPort(o.devServer.empty() ? "localhost:44334" : o.devServer, host, port);
    if (_stricmp(host.c_str(), "localhost") != 0 && host != "127.0.0.1")
    {
        Say("The Dev server is " + o.devServer + " - not on this PC, so nothing to start.");
        return "";
    }
    fs::path logDir = U8Path(o.logDir.empty() ? "." : o.logDir);
    std::error_code ec; fs::create_directories(logDir, ec);

    std::vector<std::pair<std::wstring, std::wstring>> buildEnv = { { L"DOTNET_ROLL_FORWARD", L"Major" }, { L"DOTNET_CLI_TELEMETRY_OPTOUT", L"1" }, { L"DOTNET_NOLOGO", L"1" } };
    const int httpPort = 18080;           // what CampusNet calls the server back on (its /photon/... endpoints)

    // ---- the Campus server
    if (!PortOpen(port))
    {
        std::string sd = ResolveServerDir(o);
        if (sd.empty()) return "Your local server isn't set up: choose its folder in Settings > Developer (the one with CampusServer.csproj).";
        fs::path dir = U8Path(sd);
        if (!IsFile(dir / L"CampusServer.csproj")) return "No CampusServer.csproj in " + sd + " - choose the server's folder in Settings > Developer.";
        fs::path exe = dir / L"bin" / L"Release" / L"net8.0" / L"CampusServer.exe";
        if (RunningFrom(exe).empty())
        {
            std::string err = EnsureBuilt(dir, L"CampusServer.exe", logDir / L"localserver-build.log", buildEnv, "server");
            if (!err.empty()) return err;
            Say("Starting your local server...");
            Run(L"dotnet dev-certs https", dir, buildEnv, logDir / L"localserver-build.log", true, 60000);    // the https certificate the server needs (made once)
            std::wstring urls = L"https://localhost:" + std::to_wstring(port) + L";http://127.0.0.1:" + std::to_wstring(httpPort);
            std::vector<std::pair<std::wstring, std::wstring>> env = buildEnv;
            env.push_back({ L"ASPNETCORE_URLS", urls });
            env.push_back({ L"ASPNETCORE_ENVIRONMENT", L"Development" });
            env.push_back({ L"CAMPUS_LOCAL_DEV", L"1" });
            if (Run(L"\"" + exe.wstring() + L"\"", dir, env, logDir / L"localserver.log", false) == -1) return "Couldn't start your local server (" + PathU8(exe) + ").";
            g_srvStarted = true;
        }
        for (int i = 0; i < 240 && !PortOpen(port); i++)
        {
            Say("Waiting for your local server to start...");
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            if (i > 20 && RunningFrom(dir / L"bin" / L"Release" / L"net8.0" / L"CampusServer.exe").empty())
                return "Your local server stopped right after starting - see " + PathU8(logDir / L"localserver.log");
        }
        if (!PortOpen(port)) return "Your local server didn't come up on port " + std::to_string(port) + " - see " + PathU8(logDir / L"localserver.log");
    }

    // ---- CampusNet (the Photon game server)
    std::string nd = ResolveNetDir(o);
    if (nd.empty()) { Say("No CampusNet folder found - joining rooms won't work (Settings > Developer)."); return ""; }
    fs::path ndir = U8Path(nd);
    fs::path nexe = ndir / L"bin" / L"Release" / L"net8.0" / L"campusnet.exe";
    if (RunningFrom(nexe).empty())
    {
        std::string err = EnsureBuilt(ndir, L"campusnet.exe", logDir / L"localnet-build.log", buildEnv, "CampusNet");
        if (!err.empty()) return err;
        Say("Starting CampusNet...");
        std::wstring base = L"http://127.0.0.1:" + std::to_wstring(httpPort);
        std::vector<std::pair<std::wstring, std::wstring>> env = buildEnv;
        env.push_back({ L"CAMPUSNET_PUBLIC_HOST", L"127.0.0.1" });
        for (const wchar_t* k : { L"AFKEXEMPT", L"CHATGUARD", L"JOINBLOCKS", L"PLACEMENT", L"STUCKLOAD" })
        {
            std::wstring lower = k; for (auto& ch : lower) ch = (wchar_t)towlower(ch);
            env.push_back({ std::wstring(L"CAMPUSNET_") + k + L"_URL", base + L"/photon/" + (std::wstring(k) == L"PLACEMENT" ? L"placement" : lower) });
        }
        // the server's token key: the file in its Data folder when it has one (CampusNet otherwise uses the same built-in default)
        std::string sd = ResolveServerDir(o);
        if (!sd.empty() && IsFile(U8Path(sd) / L"Data" / L"jwt_key.txt")) env.push_back({ L"CAMPUSNET_JWT_KEY_FILE", (U8Path(sd) / L"Data" / L"jwt_key.txt").wstring() });
        if (Run(L"\"" + nexe.wstring() + L"\"", ndir, env, logDir / L"localnet.log", false) == -1) return "Couldn't start CampusNet (" + PathU8(nexe) + ").";
        g_netStarted = true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1500));
        if (RunningFrom(nexe).empty()) return "CampusNet stopped right after starting - see " + PathU8(logDir / L"localnet.log");
    }
    Say("Your local servers are running.");
    return "";
}

// POST http://127.0.0.1:<port>/api/localdev/shutdown (only a CAMPUS_LOCAL_DEV server has it)
static bool AskServerToShutDown(int httpPort)
{
    bool ok = false;
    if (HINTERNET s = WinHttpOpen(L"CampusLauncher/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, nullptr, nullptr, 0))
    {
        WinHttpSetTimeouts(s, 3000, 3000, 3000, 3000);
        if (HINTERNET c = WinHttpConnect(s, L"127.0.0.1", (INTERNET_PORT)httpPort, 0))
        {
            if (HINTERNET r = WinHttpOpenRequest(c, L"POST", L"/api/localdev/shutdown", nullptr, nullptr, nullptr, 0))
            {
                if (WinHttpSendRequest(r, nullptr, 0, nullptr, 0, 0, 0) && WinHttpReceiveResponse(r, nullptr)) ok = true;
                WinHttpCloseHandle(r);
            }
            WinHttpCloseHandle(c);
        }
        WinHttpCloseHandle(s);
    }
    return ok;
}

static int Kill(const std::vector<DWORD>& pids)
{
    int n = 0;
    for (DWORD pid : pids)
        if (HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pid)) { if (TerminateProcess(h, 0)) n++; CloseHandle(h); }
    return n;
}

int StopLocalServersWeStarted(const LocalServerOptions& o)
{
    int n = 0;
    std::string sd = ResolveServerDir(o), nd = ResolveNetDir(o);
    if (g_srvStarted.exchange(false) && !sd.empty())
    {
        fs::path exe = U8Path(sd) / L"bin" / L"Release" / L"net8.0" / L"CampusServer.exe";
        AskServerToShutDown(18080);                                   // a clean shutdown (the database is closed properly)
        for (int i = 0; i < 40 && !RunningFrom(exe).empty(); i++) std::this_thread::sleep_for(std::chrono::milliseconds(250));
        auto left = RunningFrom(exe);
        n += 1;
        if (!left.empty()) Kill(left);                                // it did not go in 10 s
    }
    if (g_netStarted.exchange(false) && !nd.empty())
        n += Kill(RunningFrom(U8Path(nd) / L"bin" / L"Release" / L"net8.0" / L"campusnet.exe"));
    return n;
}

int StopLocalServers(const LocalServerOptions& o)
{
    int n = 0;
    std::string sd = ResolveServerDir(o), nd = ResolveNetDir(o);
    std::vector<fs::path> exes;
    if (!sd.empty()) exes.push_back(U8Path(sd) / L"bin" / L"Release" / L"net8.0" / L"CampusServer.exe");
    if (!nd.empty()) exes.push_back(U8Path(nd) / L"bin" / L"Release" / L"net8.0" / L"campusnet.exe");
    for (auto& e : exes)
        for (DWORD pid : RunningFrom(e))
            if (HANDLE h = OpenProcess(PROCESS_TERMINATE, FALSE, pid)) { if (TerminateProcess(h, 0)) n++; CloseHandle(h); }
    return n;
}
